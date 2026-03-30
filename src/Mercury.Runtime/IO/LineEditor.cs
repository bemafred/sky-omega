using System.Text;

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// BCL-only readline-style line editor for interactive console input.
/// Provides history navigation (↑↓), cursor movement (←→ Home End),
/// word navigation (Alt+B Alt+F), and in-place editing.
///
/// Adapted from SkyChatBot ConsoleLineEditor (Summer 2025 MVP).
/// </summary>
public sealed class LineEditor
{
    private readonly List<string> _history;
    private int _historyIndex;
    private string? _savedInput;

    private readonly StringBuilder _buffer = new();
    private int _cursor;
    private int _promptLength;
    private int _renderTop;
    private int _previousRenderLength;

    public LineEditor(List<string> history)
    {
        _history = history;
    }

    /// <summary>
    /// Read a line of input with editing support. Returns null on EOF (Ctrl+D).
    /// The prompt must already be written to the console before calling this.
    /// </summary>
    public string? ReadLine(int promptLength)
    {
        _buffer.Clear();
        _cursor = 0;
        _promptLength = promptLength;
        _renderTop = Console.CursorTop;
        _previousRenderLength = 0;
        _historyIndex = _history.Count;
        _savedInput = null;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Ctrl+D on empty line = EOF
            if (key.Key == ConsoleKey.D && key.Modifiers == ConsoleModifiers.Control && _buffer.Length == 0)
                return null;

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var input = _buffer.ToString();
                    AddToHistory(input);
                    return input;

                case ConsoleKey.UpArrow:
                    NavigateHistory(-1);
                    break;

                case ConsoleKey.DownArrow:
                    NavigateHistory(1);
                    break;

                case ConsoleKey.LeftArrow:
                    if (_cursor > 0)
                    {
                        _cursor--;
                        SetConsoleCursor();
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (_cursor < _buffer.Length)
                    {
                        _cursor++;
                        SetConsoleCursor();
                    }
                    break;

                case ConsoleKey.Home:
                    _cursor = 0;
                    SetConsoleCursor();
                    break;

                case ConsoleKey.End:
                    _cursor = _buffer.Length;
                    SetConsoleCursor();
                    break;

                case ConsoleKey.B when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                    WordBack();
                    break;

                case ConsoleKey.F when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                    WordForward();
                    break;

                case ConsoleKey.Backspace:
                    if (_cursor > 0)
                    {
                        _buffer.Remove(_cursor - 1, 1);
                        _cursor--;
                        Redraw();
                    }
                    break;

                case ConsoleKey.Delete:
                    if (_cursor < _buffer.Length)
                    {
                        _buffer.Remove(_cursor, 1);
                        Redraw();
                    }
                    break;

                // Ctrl+U — kill to start of line
                case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                    if (_cursor > 0)
                    {
                        _buffer.Remove(0, _cursor);
                        _cursor = 0;
                        Redraw();
                    }
                    break;

                // Ctrl+K — kill to end of line
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    if (_cursor < _buffer.Length)
                    {
                        _buffer.Remove(_cursor, _buffer.Length - _cursor);
                        Redraw();
                    }
                    break;

                // Ctrl+A — beginning of line
                case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
                    _cursor = 0;
                    SetConsoleCursor();
                    break;

                // Ctrl+E — end of line
                case ConsoleKey.E when key.Modifiers == ConsoleModifiers.Control:
                    _cursor = _buffer.Length;
                    SetConsoleCursor();
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _buffer.Insert(_cursor, key.KeyChar);
                        _cursor++;
                        Redraw();
                    }
                    break;
            }
        }
    }

    private void NavigateHistory(int direction)
    {
        var newIndex = _historyIndex + direction;

        if (direction < 0 && newIndex < 0)
            return;
        if (direction > 0 && newIndex > _history.Count)
            return;

        // Save current input when first navigating away
        if (_historyIndex == _history.Count)
            _savedInput = _buffer.ToString();

        _historyIndex = newIndex;

        _buffer.Clear();
        if (_historyIndex < _history.Count)
            _buffer.Append(_history[_historyIndex]);
        else if (_savedInput is not null)
            _buffer.Append(_savedInput);

        _cursor = _buffer.Length;
        Redraw();
    }

    private void WordBack()
    {
        if (_cursor == 0) return;

        _cursor--;
        while (_cursor > 0 && char.IsWhiteSpace(_buffer[_cursor]))
            _cursor--;
        while (_cursor > 0 && !char.IsWhiteSpace(_buffer[_cursor - 1]))
            _cursor--;

        SetConsoleCursor();
    }

    private void WordForward()
    {
        if (_cursor >= _buffer.Length) return;

        while (_cursor < _buffer.Length && !char.IsWhiteSpace(_buffer[_cursor]))
            _cursor++;
        while (_cursor < _buffer.Length && char.IsWhiteSpace(_buffer[_cursor]))
            _cursor++;

        SetConsoleCursor();
    }

    private void AddToHistory(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) &&
            (_history.Count == 0 || _history[^1] != input))
        {
            _history.Add(input);
        }
    }

    private void Redraw()
    {
        var width = Console.WindowWidth;
        if (width <= 0) width = 80;

        // Move to render start
        Console.SetCursorPosition(_promptLength, _renderTop);

        var text = _buffer.ToString();
        Console.Write(text);

        // Clear any leftover characters from previous longer content
        var totalLength = _promptLength + text.Length;
        if (totalLength < _previousRenderLength + _promptLength)
        {
            var clearCount = _previousRenderLength - text.Length;
            if (clearCount > 0)
                Console.Write(new string(' ', clearCount));
        }

        _previousRenderLength = text.Length;

        // Position cursor
        SetConsoleCursor();
    }

    private void SetConsoleCursor()
    {
        var width = Console.WindowWidth;
        if (width <= 0) width = 80;

        var absolutePos = _promptLength + _cursor;
        var row = _renderTop + absolutePos / width;
        var col = absolutePos % width;

        Console.SetCursorPosition(col, row);
    }
}
