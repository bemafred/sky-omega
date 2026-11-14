using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaTerm.Core;

namespace AvaTerm.App;

public sealed partial class TerminalControl
{
    static readonly bool IsMac = OperatingSystem.IsMacOS();

    // mouse state
    int _reportButton = -1;                 // button currently reported to the application
    (int Col, int Row) _lastReportCell = (-1, -1);
    bool _selecting, _selectionStarted, _dragged, _wasFocusedOnPress;
    int _clickCount;
    long _pressLine;
    (int Col, int Row) _pressCell;
    double _wheelRemainder;

    // ------------------------------------------------------------------ helpers

    /// <summary>Sends keyboard-style input: snaps the view back to the live screen first.</summary>
    void SendInput(byte[] data)
    {
        if (data.Length == 0) return;
        lock (Term.Sync)
        {
            Term.ViewOffset = 0;
            Term.ClearSelection();
        }
        _session.Send(data);
        _blinkOn = true;
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    static TermMods Mods(KeyModifiers m) =>
        ((m & KeyModifiers.Shift) != 0 ? TermMods.Shift : 0) |
        ((m & KeyModifiers.Alt) != 0 ? TermMods.Alt : 0) |
        ((m & KeyModifiers.Control) != 0 ? TermMods.Ctrl : 0);

    (int Col, int Row) CellAt(Point p)
    {
        if (_cellW <= 0) return (0, 0);
        int cols, rows;
        lock (Term.Sync) { cols = Term.Cols; rows = Term.Rows; }
        return (Math.Clamp((int)Math.Floor((p.X - Pad) / _cellW), 0, cols - 1),
                Math.Clamp((int)Math.Floor((p.Y - Pad) / _cellH), 0, rows - 1));
    }

    // ------------------------------------------------------------------ clipboard

    public async void CopySelection(bool clear)
    {
        string text;
        lock (Term.Sync)
        {
            text = Term.GetSelectionText();
            if (clear) Term.ClearSelection();
        }
        InvalidateVisual();
        if (text.Length > 0) await SetClipboardAsync(text);
    }

    void SetClipboard(string text) => _ = SetClipboardAsync(text);

    async Task SetClipboardAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception) { /* clipboard owned by another process; nothing useful to do */ }
    }

    public async void Paste()
    {
        string? text = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) text = await clipboard.TryGetTextAsync();
        }
        catch (Exception) { }
        if (string.IsNullOrEmpty(text)) return;
        byte[] data;
        lock (Term.Sync) data = InputEncoder.Paste(Term, text);
        SendInput(data);
    }

    // ------------------------------------------------------------------ focus

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _focused = true;
        _blinkOn = true;
        ReportFocus(true);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _focused = false;
        ReportFocus(false);
        InvalidateVisual();
    }

    void ReportFocus(bool focused)
    {
        bool wanted;
        lock (Term.Sync) wanted = Term.FocusEvents;
        if (wanted) _session.Send(InputEncoder.Focus(focused));
    }

    // ------------------------------------------------------------------ keyboard

    static bool MapKey(Key key, out TermKey mapped)
    {
        TermKey? k = key switch
        {
            Key.Up => TermKey.Up, Key.Down => TermKey.Down, Key.Left => TermKey.Left, Key.Right => TermKey.Right,
            Key.Home => TermKey.Home, Key.End => TermKey.End, Key.Insert => TermKey.Insert, Key.Delete => TermKey.Delete,
            Key.PageUp => TermKey.PageUp, Key.PageDown => TermKey.PageDown,
            Key.F1 => TermKey.F1, Key.F2 => TermKey.F2, Key.F3 => TermKey.F3, Key.F4 => TermKey.F4,
            Key.F5 => TermKey.F5, Key.F6 => TermKey.F6, Key.F7 => TermKey.F7, Key.F8 => TermKey.F8,
            Key.F9 => TermKey.F9, Key.F10 => TermKey.F10, Key.F11 => TermKey.F11, Key.F12 => TermKey.F12,
            Key.Enter => TermKey.Enter, Key.Back => TermKey.Backspace, Key.Tab => TermKey.Tab, Key.Escape => TermKey.Escape,
            _ => null,
        };
        mapped = k ?? default;
        return k != null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var key = e.Key;
        var m = e.KeyModifiers;
        bool ctrl = (m & KeyModifiers.Control) != 0, shift = (m & KeyModifiers.Shift) != 0;
        bool alt = (m & KeyModifiers.Alt) != 0, meta = (m & KeyModifiers.Meta) != 0;
        bool hasSelection, altScreen;
        lock (Term.Sync) { hasSelection = Term.HasSelection; altScreen = Term.AltScreenActive; }

        // --- terminal-level shortcuts
        bool command = IsMac ? meta && !ctrl : ctrl && shift;      // the "application shortcut" chord
        if (command && key == Key.C) { CopySelection(false); e.Handled = true; return; }
        if ((command && key == Key.V) || (shift && !ctrl && key == Key.Insert)) { Paste(); e.Handled = true; return; }
        if (!IsMac && ctrl && !shift && !alt && key == Key.C && hasSelection)
        {
            // Ctrl+C copies while something is selected, and interrupts otherwise.
            CopySelection(true);
            e.Handled = true;
            return;
        }

        bool zoomChord = IsMac ? meta : ctrl && !alt;
        bool zoomKey = key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0;
        if (zoomChord && zoomKey)
        {
            Zoom(key is Key.OemPlus or Key.Add ? 1 : key is Key.OemMinus or Key.Subtract ? -1 : 0);
            e.Handled = true;
            return;
        }

        bool historyKey = key is Key.PageUp or Key.PageDown or Key.Home or Key.End;
        if (historyKey && shift && !ctrl && !alt && !altScreen)
        {
            var (scrollback, _, rows) = GetScrollState();
            ScrollLines(key switch
            {
                Key.PageUp => rows - 1,
                Key.PageDown => -(rows - 1),
                Key.Home => scrollback,
                _ => -scrollback,
            });
            e.Handled = true;
            return;
        }

        if (IsMac && meta) return;   // other Cmd chords belong to the window / system

        // --- keys with dedicated escape sequences
        if (MapKey(key, out var termKey))
        {
            byte[] data;
            bool wordMotion = IsMac && alt && !ctrl && !shift && !_settings.MacOptionIsMeta && (key == Key.Left || key == Key.Right);
            if (wordMotion)
                data = [0x1B, key == Key.Left ? (byte)'b' : (byte)'f'];   // Option+arrow: word motion, as in Terminal.app
            else
                lock (Term.Sync) data = InputEncoder.Key(Term, termKey, Mods(m));
            SendInput(data);
            e.Handled = true;
            return;
        }

        // Ctrl+Alt is how Windows reports AltGr: leave it to text input so AltGr characters work.
        if (ctrl && alt) return;

        if (ctrl)
        {
            int code = -1;
            if (key >= Key.A && key <= Key.Z) code = key - Key.A + 1;
            else if (key == Key.Space) code = 0;
            else if (key >= Key.D2 && key <= Key.D8) code = InputEncoder.ControlCode((char)('2' + (key - Key.D2)));
            else
            {
                char symbol = key switch
                {
                    Key.OemOpenBrackets => '[',
                    Key.OemCloseBrackets => ']',
                    Key.OemPipe or Key.OemBackslash => '\\',
                    Key.OemQuestion => '/',
                    _ => e.KeySymbol is { Length: 1 } s ? s[0] : '\0',
                };
                code = InputEncoder.ControlCode(symbol);
            }
            if (code >= 0)
            {
                SendInput([(byte)code]);
                e.Handled = true;
            }
            return;
        }

        if (alt && (!IsMac || _settings.MacOptionIsMeta))
        {
            string? symbol = null;
            if (key >= Key.A && key <= Key.Z)
                symbol = ((char)((shift ? 'A' : 'a') + (key - Key.A))).ToString();
            else if (key >= Key.D0 && key <= Key.D9 && !shift)
                symbol = ((char)('0' + (key - Key.D0))).ToString();
            else if (!string.IsNullOrEmpty(e.KeySymbol) && !char.IsControl(e.KeySymbol[0]))
                symbol = e.KeySymbol;
            if (symbol != null)
            {
                SendInput(InputEncoder.Text(symbol, altPrefix: true));
                e.Handled = true;
            }
        }
        // Everything else arrives as text through OnTextInput (layout, dead keys and IME applied).
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        SendInput(InputEncoder.Text(e.Text));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _wasFocusedOnPress = IsFocused;
        Focus();

        var point = e.GetCurrentPoint(this);
        var (col, row) = CellAt(point.Position);
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        int button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0,
            PointerUpdateKind.MiddleButtonPressed => 1,
            PointerUpdateKind.RightButtonPressed => 2,
            _ => -1,
        };
        if (button < 0) return;
        e.Handled = true;

        // An application that asked for the mouse (tmux, vim, htop) gets it; Shift overrides.
        byte[]? report = null;
        bool hasSelection;
        lock (Term.Sync)
        {
            if (!shift) report = InputEncoder.Mouse(Term, MouseAction.Press, button, col, row, Mods(e.KeyModifiers));
            hasSelection = Term.HasSelection;
        }
        if (report != null)
        {
            _reportButton = button;
            _lastReportCell = (col, row);
            _session.Send(report);
            e.Pointer.Capture(this);
            return;
        }

        switch (button)
        {
            case 1:
                Paste();
                break;
            case 2:
                if (hasSelection) CopySelection(true);
                else Paste();
                break;
            case 0:
                _selecting = true;
                _dragged = false;
                _clickCount = e.ClickCount;
                _pressCell = (col, row);
                lock (Term.Sync)
                {
                    _pressLine = Term.ViewTopId + row;
                    if (_clickCount == 2) Term.StartSelection(_pressLine, col, SelectionUnit.Word);
                    else if (_clickCount >= 3) Term.StartSelection(_pressLine, col, SelectionUnit.Line);
                    else if (shift && hasSelection) Term.ExtendSelection(_pressLine, col);
                    else Term.ClearSelection();
                    _selectionStarted = Term.HasSelection;
                }
                e.Pointer.Capture(this);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var (col, row) = CellAt(position);

        if (!_selecting)
        {
            if ((col, row) == _lastReportCell) return;
            byte[]? report;
            lock (Term.Sync)
            {
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && _reportButton < 0) return;
                report = InputEncoder.Mouse(Term, MouseAction.Move, _reportButton, col, row, Mods(e.KeyModifiers));
            }
            _lastReportCell = (col, row);
            if (report != null) _session.Send(report);
            return;
        }

        if (!_dragged && (col, row) == _pressCell) return;
        _dragged = true;
        lock (Term.Sync)
        {
            if (!_selectionStarted)
            {
                Term.StartSelection(_pressLine, _pressCell.Col, SelectionUnit.Char);
                _selectionStarted = true;
            }
            // Dragging past the top or bottom edge scrolls the history along.
            if (position.Y < 0) Term.ViewOffset++;
            else if (position.Y > Bounds.Height) Term.ViewOffset--;
            Term.ExtendSelection(Term.ViewTopId + row, col);
        }
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (col, row) = CellAt(e.GetPosition(this));

        if (_reportButton >= 0)
        {
            byte[]? report;
            lock (Term.Sync)
                report = InputEncoder.Mouse(Term, MouseAction.Release, _reportButton, col, row, Mods(e.KeyModifiers));
            _reportButton = -1;
            e.Pointer.Capture(null);
            if (report != null) _session.Send(report);
            e.Handled = true;
            return;
        }

        if (!_selecting || e.InitialPressMouseButton != MouseButton.Left) return;
        _selecting = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (_selectionStarted)
        {
            if (_settings.CopyOnSelect) CopySelection(false);
            return;
        }

        // A plain click on the line being edited: walk the shell cursor to that cell.
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (_dragged || _clickCount != 1 || shift || !_wasFocusedOnPress || !_settings.ClickToMoveCursor) return;
        byte[]? keys = null;
        lock (Term.Sync)
        {
            int delta = Term.ComputeCursorMove(row, col);
            if (delta != 0) keys = InputEncoder.CursorMove(Term, delta);
        }
        if (keys != null) SendInput(keys);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;

        // Touchpads deliver fractions of a notch; accumulate until a whole line is due.
        _wheelRemainder += e.Delta.Y * Math.Max(1, _settings.WheelLines);
        int lines = (int)Math.Truncate(_wheelRemainder);
        if (lines == 0) return;
        _wheelRemainder -= lines;

        var (col, row) = CellAt(e.GetPosition(this));
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var output = new List<byte>();
        lock (Term.Sync)
        {
            if (Term.MouseMode != MouseTracking.None && !shift)
            {
                var action = lines > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
                var one = InputEncoder.Mouse(Term, action, -1, col, row, Mods(e.KeyModifiers));
                for (int i = Math.Abs(lines); i > 0 && one != null; i--) output.AddRange(one);
            }
            else if (Term.AltScreenActive)
            {
                // Full-screen programs without mouse support (less, man) scroll with cursor keys.
                if (Term.AlternateScroll)
                {
                    var one = InputEncoder.Key(Term, lines > 0 ? TermKey.Up : TermKey.Down, TermMods.None);
                    for (int i = Math.Abs(lines); i > 0; i--) output.AddRange(one);
                }
            }
            else Term.ViewOffset += lines;
        }
        if (output.Count > 0) _session.Send(output.ToArray());
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }
}
