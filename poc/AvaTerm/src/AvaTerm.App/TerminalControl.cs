using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaTerm.Core;
using AvaTerm.Pty;

namespace AvaTerm.App;

/// <summary>
/// The terminal surface: renders the cell grid and turns keyboard, mouse and clipboard
/// activity into terminal input. Input handling lives in TerminalControl.Input.cs.
/// </summary>
public sealed partial class TerminalControl : Control
{
    const double Pad = 4;

    readonly Settings _settings;
    readonly ColorTheme _theme;
    readonly TerminalSession _session;
    readonly DispatcherTimer _blinkTimer;

    readonly Typeface[] _typefaces = new Typeface[4];   // regular, bold, italic, bold italic
    readonly Dictionary<GlyphKey, FormattedText> _glyphs = new();
    readonly Dictionary<uint, IBrush> _brushes = new();
    double _fontSize, _cellW, _cellH, _baseline, _scale = 1;
    uint[] _rowFg = [], _rowBg = [];

    bool _started, _focused, _blinkOn = true;
    int _invalidateQueued;

    readonly record struct GlyphKey(int Rune, string? Combining, int Style, uint Color);

    Terminal Term => _session.Terminal;

    /// <summary>Raised on the UI thread when scrollback size or view position may have changed.</summary>
    public event Action? ScrollChanged;
    public event Action<string>? TitleChanged;
    public event Action<int>? Exited;

    public TerminalControl(Settings settings, ColorTheme theme)
    {
        _settings = settings;
        _theme = theme;
        _fontSize = settings.FontSize;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _session = new TerminalSession(
            new PtyOptions { Command = settings.Shell, Args = settings.ShellArgs },
            settings.ScrollbackLines);
        Term.DefaultForegroundRgb = theme.Foreground;
        Term.DefaultBackgroundRgb = theme.Background;

        // These fire on the reader thread while it holds the terminal lock: hop to the UI thread.
        _session.Updated += QueueInvalidate;
        _session.Exited += code => Dispatcher.UIThread.Post(() => Exited?.Invoke(code));
        Term.TitleChanged += title => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        Term.ClipboardSet += text => Dispatcher.UIThread.Post(() => SetClipboard(text));

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _blinkTimer.Tick += (_, _) =>
        {
            if (!_focused) return;
            _blinkOn = !_blinkOn;
            InvalidateVisual();
        };
    }

    public void Shutdown()
    {
        _blinkTimer.Stop();
        _session.Dispose();
    }

    // ------------------------------------------------------------------ layout

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top) top.ScalingChanged += OnScalingChanged;
        UpdateMetrics();
        _blinkTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top) top.ScalingChanged -= OnScalingChanged;
        _blinkTimer.Stop();
    }

    void OnScalingChanged(object? sender, EventArgs e) => UpdateMetrics();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGrid();
    }

    void UpdateMetrics()
    {
        _scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var family = new FontFamily(_settings.FontFamily);
        _typefaces[0] = new Typeface(family);
        _typefaces[1] = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _typefaces[2] = new Typeface(family, FontStyle.Italic);
        _typefaces[3] = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaces[0], _fontSize, Brushes.White);
        // Whole device pixels per cell keep backgrounds and box drawing seamless.
        _cellW = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace / 10 * _scale)) / _scale;
        _cellH = Math.Max(1, Math.Ceiling(probe.Height * _scale)) / _scale;
        _baseline = probe.Baseline;
        _glyphs.Clear();
        UpdateGrid();
        InvalidateVisual();
    }

    void UpdateGrid()
    {
        if (_cellW <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        int cols = Math.Max(2, (int)((Bounds.Width - 2 * Pad) / _cellW));
        int rows = Math.Max(1, (int)((Bounds.Height - 2 * Pad) / _cellH));
        _session.Resize(cols, rows);
        if (!_started)
        {
            _started = true;
            try { _session.Start(); }
            catch (Exception e)
            {
                lock (Term.Sync)
                    Term.Feed(Encoding.UTF8.GetBytes($"\u001b[31mCould not start the shell:\r\n{e.Message}\u001b[0m\r\n"));
            }
        }
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    public void Zoom(int steps)
    {
        _fontSize = steps == 0 ? _settings.FontSize : Math.Clamp(_fontSize + steps, 6, 72);
        UpdateMetrics();
    }

    void QueueInvalidate()
    {
        // Coalesce: however fast output arrives, at most one refresh is pending.
        if (Interlocked.Exchange(ref _invalidateQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _invalidateQueued, 0);
            InvalidateVisual();
            ScrollChanged?.Invoke();
        });
    }

    // ------------------------------------------------------------------ scrolling

    public (int Scrollback, int Offset, int Rows) GetScrollState()
    {
        lock (Term.Sync) return (Term.ScrollbackCount, Term.ViewOffset, Term.Rows);
    }

    /// <summary>Scrolls the view; positive values move back into history.</summary>
    public void ScrollLines(int lines)
    {
        lock (Term.Sync) Term.ViewOffset += lines;
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    public void ScrollToOffset(int offset)
    {
        lock (Term.Sync) Term.ViewOffset = offset;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ rendering

    IBrush BrushFor(uint rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var brush))
        {
            if (_brushes.Count > 4096) _brushes.Clear();
            brush = new ImmutableSolidColorBrush(ToColor(rgb));
            _brushes[rgb] = brush;
        }
        return brush;
    }

    static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    FormattedText Glyph(in Cell cell, uint color)
    {
        int style = ((cell.Attr & CellAttr.Bold) != 0 ? 1 : 0) | ((cell.Attr & CellAttr.Italic) != 0 ? 2 : 0);
        var key = new GlyphKey(cell.Rune, cell.Combining, style, color);
        if (!_glyphs.TryGetValue(key, out var text))
        {
            if (_glyphs.Count > 20_000) _glyphs.Clear();
            text = new FormattedText(char.ConvertFromUtf32(cell.Rune) + cell.Combining, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _typefaces[style], _fontSize, BrushFor(color));
            _glyphs[key] = text;
        }
        return text;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(BrushFor(_theme.Background), new Rect(Bounds.Size));
        if (_cellW <= 0) return;

        var term = Term;
        lock (term.Sync)
        {
            int cols = term.Cols, rows = term.Rows;
            if (_rowFg.Length < cols)
            {
                _rowFg = new uint[cols];
                _rowBg = new uint[cols];
            }
            long topId = term.ViewTopId;
            bool hasSelection = term.HasSelection;
            var (selStart, selEnd) = hasSelection ? term.SelectionRange : default;

            for (int row = 0; row < rows; row++)
            {
                var line = term.GetViewLine(row);
                long id = topId + row;
                bool rowSelected = hasSelection && id >= selStart.Line && id <= selEnd.Line;
                DrawRow(ctx, line, row, Math.Min(cols, line.Cells.Length), term.ReverseVideo,
                    rowSelected ? (id == selStart.Line ? selStart.Col : 0) : int.MaxValue,
                    rowSelected ? (id == selEnd.Line ? selEnd.Col : int.MaxValue) : -1);
            }
            DrawCursor(ctx, term);
        }
    }

    void ResolveColors(in Cell cell, bool selected, bool reverse, out uint fg, out uint bg)
    {
        fg = _theme.Resolve(cell.Fg, true);
        bg = _theme.Resolve(cell.Bg, false);
        if (((cell.Attr & CellAttr.Inverse) != 0) != reverse) (fg, bg) = (bg, fg);
        if ((cell.Attr & CellAttr.Faint) != 0) fg = ColorTheme.Blend(fg, bg, 0.5);
        if (selected) bg = _theme.Selection;
        if ((cell.Attr & CellAttr.Invisible) != 0) fg = bg;
    }

    void DrawRow(DrawingContext ctx, TermLine line, int row, int count, bool reverse, int selFrom, int selTo)
    {
        double y = Pad + row * _cellH;
        var cells = line.Cells;

        // Backgrounds, merged into runs of equal colour.
        int runStart = 0;
        for (int x = 0; x <= count; x++)
        {
            if (x < count)
                ResolveColors(cells[x], x >= selFrom && x <= selTo, reverse, out _rowFg[x], out _rowBg[x]);
            if (x > runStart && (x == count || _rowBg[x] != _rowBg[runStart]))
            {
                if (_rowBg[runStart] != _theme.Background)
                    ctx.FillRectangle(BrushFor(_rowBg[runStart]),
                        new Rect(Pad + runStart * _cellW, y, (x - runStart) * _cellW, _cellH));
                runStart = x;
            }
        }

        // Glyphs and decorations, one cell at a time so nothing can drift off the grid.
        double decoration = Math.Max(1, Math.Round(_cellH / 16 * _scale)) / _scale;
        for (int x = 0; x < count; x++)
        {
            ref readonly var cell = ref cells[x];
            if ((cell.Attr & CellAttr.WideTail) != 0) continue;
            bool wide = (cell.Attr & CellAttr.Wide) != 0;
            double cx = Pad + x * _cellW, width = wide ? 2 * _cellW : _cellW;
            uint fg = _rowFg[x];

            if (!cell.IsBlank) DrawGlyph(ctx, cell, new Rect(cx, y, width, _cellH), fg, wide);

            if ((cell.Attr & CellAttr.Underline) != 0)
                ctx.FillRectangle(BrushFor(fg), new Rect(cx, Math.Min(y + _baseline + decoration, y + _cellH - decoration), width, decoration));
            if ((cell.Attr & CellAttr.Strike) != 0)
                ctx.FillRectangle(BrushFor(fg), new Rect(cx, y + Math.Round(_cellH * 0.55), width, decoration));
        }
    }

    void DrawGlyph(DrawingContext ctx, in Cell cell, Rect rect, uint color, bool clip)
    {
        if (BoxDrawing.TryDraw(ctx, cell.Rune, rect, BrushFor(color), ToColor(color), _scale)) return;
        var text = Glyph(cell, color);
        // Fallback fonts have their own metrics; align on the baseline of the main font.
        var origin = new Point(rect.X, rect.Y + _baseline - text.Baseline);
        if (clip || cell.Rune > 0x2E7F)
        {
            using (ctx.PushClip(rect)) ctx.DrawText(text, origin);
        }
        else ctx.DrawText(text, origin);
    }

    void DrawCursor(DrawingContext ctx, Terminal term)
    {
        if (!term.CursorVisible || term.ViewOffset != 0) return;
        if (_focused && _settings.CursorBlink && term.CursorBlink && !_blinkOn) return;

        int cx = term.CursorX, cy = term.CursorY;
        var cells = term.GetViewLine(cy).Cells;
        if (cx >= cells.Length) return;
        if ((cells[cx].Attr & CellAttr.WideTail) != 0 && cx > 0) cx--;
        var cell = cells[cx];
        bool wide = (cell.Attr & CellAttr.Wide) != 0;
        var rect = new Rect(Pad + cx * _cellW, Pad + cy * _cellH, wide ? 2 * _cellW : _cellW, _cellH);
        var brush = BrushFor(_theme.Cursor);
        double thin = Math.Max(1, Math.Round(_scale)) / _scale;

        if (!_focused)
        {
            ctx.DrawRectangle(null, new Pen(brush, thin), rect.Deflate(thin / 2));
            return;
        }
        switch (term.CursorShape)
        {
            case CursorShape.Bar:
                ctx.FillRectangle(brush, new Rect(rect.X, rect.Y, thin * 2, rect.Height));
                break;
            case CursorShape.Underline:
                ctx.FillRectangle(brush, new Rect(rect.X, rect.Bottom - thin * 2, rect.Width, thin * 2));
                break;
            default:
                ctx.FillRectangle(brush, rect);
                if (!cell.IsBlank) DrawGlyph(ctx, cell, rect, _theme.Background, wide);
                break;
        }
    }
}
