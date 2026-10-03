using System.Text;

namespace SkyOmega.DrHook.Viz.Tui;

/// <summary>The dashboard's terminal surface — BCL <see cref="System.Console"/> plus ANSI escapes (ADR-012 Q2): the
/// alternate screen buffer (so the user's scrollback is untouched and restored on exit), a hidden cursor, and
/// redraw-in-place. <see cref="Present"/> writes only the rows that changed since the last frame, as ONE write, so
/// a busy delta stream does not flicker; a size change repaints everything.</summary>
public sealed class AnsiTerminal
{
    private const string Esc = "\u001b[";
    private readonly TextWriter _out;
    private string[] _previous = [];
    private bool _active;

    public AnsiTerminal(TextWriter output) => _out = output;

    /// <summary>Switch to the alternate screen, hide the cursor, clear.</summary>
    public void Enter()
    {
        _out.Write($"{Esc}?1049h{Esc}?25l{Esc}2J{Esc}H");
        _out.Flush();
        _active = true;
        _previous = [];
    }

    /// <summary>Show the cursor and return to the normal screen (the user's terminal as it was). Idempotent.</summary>
    public void Exit()
    {
        if (!_active) return;
        _out.Write($"{Esc}?25h{Esc}?1049l");
        _out.Flush();
        _active = false;
    }

    /// <summary>Draw a composed frame (rows from <see cref="DashboardScreen.Compose"/>), rewriting only changed rows.</summary>
    public void Present(string[] rows)
    {
        var frame = new StringBuilder();
        bool repaint = rows.Length != _previous.Length || (rows.Length > 0 && rows[0].Length != _previous[0].Length);
        if (repaint) frame.Append($"{Esc}2J");
        for (int r = 0; r < rows.Length; r++)
        {
            if (!repaint && rows[r] == _previous[r]) continue;
            frame.Append(Esc).Append(r + 1).Append(";1H").Append(rows[r]);
        }
        if (frame.Length == 0) return;
        _out.Write(frame.ToString());
        _out.Flush();
        _previous = rows;
    }
}
