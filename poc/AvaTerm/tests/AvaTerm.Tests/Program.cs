// Dependency-free test runner: `dotnet run --project tests/AvaTerm.Tests`.
// Core tests run everywhere; the PTY tests start real shells (and tmux when installed).
using System.Text;
using AvaTerm.Core;
using AvaTerm.Pty;

int passed = 0, failed = 0, skipped = 0;

// An optional argument runs only the tests whose name contains it.
string? filter = args.Length > 0 ? args[0] : null;

void Test(string name, Action body)
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try
    {
        body();
        passed++;
        Console.WriteLine($"  ok    {name}");
    }
    catch (SkipException e)
    {
        skipped++;
        Console.WriteLine($"  skip  {name}: {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}: {e.Message}");
    }
}

static void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{expected}> but was <{actual}>");
}

static void True(bool condition, string what)
{
    if (!condition) throw new Exception(what);
}

static Terminal Term(int cols = 20, int rows = 5, int scrollback = 100) => new(cols, rows, scrollback);
static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

static string Row(Terminal t, int row)
{
    var line = t.GetViewLine(row);
    var sb = new StringBuilder();
    for (int i = 0; i < Math.Min(t.Cols, line.Cells.Length); i++)
    {
        var c = line.Cells[i];
        if ((c.Attr & CellAttr.WideTail) != 0) continue;
        sb.Append(c.Rune == 0 ? " " : char.ConvertFromUtf32(c.Rune)).Append(c.Combining);
    }
    return sb.ToString().TrimEnd();
}

static string ScreenText(Terminal t)
{
    var sb = new StringBuilder();
    for (int r = 0; r < t.Rows; r++) sb.Append(Row(t, r)).Append('\n');
    return sb.ToString();
}

const string E = "\u001b";

Console.WriteLine("Core");

Test("prints text and tracks the cursor", () =>
{
    var t = Term();
    Feed(t, "hello\r\nworld");
    Eq("hello", Row(t, 0));
    Eq("world", Row(t, 1));
    Eq(5, t.CursorX);
    Eq(1, t.CursorY);
});

Test("wraps at the right margin and marks the line", () =>
{
    var t = Term(10, 4);
    Feed(t, "0123456789ab");
    Eq("0123456789", Row(t, 0));
    Eq("ab", Row(t, 1));
    True(t.GetViewLine(0).Wrapped, "first line should be soft-wrapped");
    True(!t.GetViewLine(1).Wrapped, "second line is not wrapped");
});

Test("deferred wrap: a full line does not scroll until the next character", () =>
{
    var t = Term(5, 2);
    Feed(t, "abcde");
    Eq(0, t.CursorY);
    Feed(t, "\r\nxy");
    Eq("abcde", Row(t, 0));
    Eq("xy", Row(t, 1));
    Eq(0, t.ScrollbackCount);
});

Test("cursor addressing and erase", () =>
{
    var t = Term();
    Feed(t, "aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc");
    Feed(t, $"{E}[2;4H{E}[K");
    Eq("bbb", Row(t, 1));
    Feed(t, $"{E}[1;3H{E}[1K");
    Eq("   aaaaaaa", Row(t, 0));
    Feed(t, $"{E}[2J");
    Eq("\n\n\n\n\n", ScreenText(t));
});

Test("insert and delete characters and lines", () =>
{
    var t = Term();
    Feed(t, "abcdef");
    Feed(t, $"{E}[1;3H{E}[2@");
    Eq("ab  cdef", Row(t, 0));
    Feed(t, $"{E}[3P");
    Eq("abdef", Row(t, 0));
    Feed(t, $"\r\nsecond{E}[1;1H{E}[L");
    Eq("", Row(t, 0));
    Eq("abdef", Row(t, 1));
    Eq("second", Row(t, 2));
    Feed(t, $"{E}[2M");
    Eq("second", Row(t, 0));
});

Test("SGR: 16, 256 and direct colours in both syntaxes", () =>
{
    var t = Term();
    Feed(t, $"{E}[1;31mA{E}[38;5;200mB{E}[38;2;1;2;3mC{E}[48:2::4:5:6mD{E}[38:5:99mE{E}[0mF");
    var c = t.GetViewLine(0).Cells;
    Eq(TermColor.Indexed(1), c[0].Fg);
    True((c[0].Attr & CellAttr.Bold) != 0, "bold");
    Eq(TermColor.Indexed(200), c[1].Fg);
    Eq(TermColor.Rgb(1, 2, 3), c[2].Fg);
    Eq(TermColor.Rgb(4, 5, 6), c[3].Bg);
    Eq(TermColor.Indexed(99), c[4].Fg);
    Eq(0u, c[5].Fg);
    Eq(CellAttr.None, c[5].Attr);
});

Test("xterm private CSI (modifyOtherKeys) is not mistaken for SGR", () =>
{
    var t = Term();
    Feed(t, $"{E}[>4;2mX");
    Eq(CellAttr.None, t.GetViewLine(0).Cells[0].Attr);
    Eq(0u, t.GetViewLine(0).Cells[0].Bg);
});

Test("scrollback keeps history and stable line ids", () =>
{
    var t = Term(20, 5, 100);
    for (int i = 0; i < 30; i++) Feed(t, $"line{i}\r\n");
    Eq(26, t.ScrollbackCount);
    Eq("line26", Row(t, 0));
    t.ViewOffset = 26;
    Eq("line0", Row(t, 0));
    long id = t.ViewTopId;
    Feed(t, "more\r\n");
    Eq("line0", Row(t, 0), "view stays anchored while output continues;");
    Eq(id, t.ViewTopId);
    t.ViewOffset = 0;
    Eq("more", Row(t, 3));
});

Test("scrollback is bounded", () =>
{
    var t = Term(20, 5, 10);
    for (int i = 0; i < 100; i++) Feed(t, $"line{i}\r\n");
    Eq(10, t.ScrollbackCount);
    t.ViewOffset = 1000;
    Eq(10, t.ViewOffset);
    Eq("line86", Row(t, 0));
});

Test("scroll region scrolls only inside its margins", () =>
{
    var t = Term(10, 5);
    Feed(t, "r0\r\nr1\r\nr2\r\nr3\r\nr4");
    Feed(t, $"{E}[2;4r{E}[4;1H\nNEW");
    Eq("r0", Row(t, 0));
    Eq("r2", Row(t, 1));
    Eq("r3", Row(t, 2));
    Eq("NEW", Row(t, 3));
    Eq("r4", Row(t, 4));
    Eq(0, t.ScrollbackCount);
    Feed(t, $"{E}[2;1H{E}M");
    Eq("", Row(t, 1));
    Eq("r2", Row(t, 2));
});

Test("alternate screen preserves the primary screen and cursor", () =>
{
    var t = Term();
    Feed(t, "shell$ ");
    Feed(t, $"{E}[?1049h{E}[HFULLSCREEN");
    True(t.AltScreenActive, "alt active");
    Eq("FULLSCREEN", Row(t, 0));
    Eq(0, t.ScrollbackCount);
    Feed(t, $"{E}[?1049l");
    True(!t.AltScreenActive, "alt left");
    Eq("shell$", Row(t, 0));
    Eq(7, t.CursorX);
});

Test("wide and combining characters", () =>
{
    var t = Term(10, 3);
    Feed(t, "a日本é");
    var c = t.GetViewLine(0).Cells;
    True((c[1].Attr & CellAttr.Wide) != 0 && (c[2].Attr & CellAttr.WideTail) != 0, "wide pair");
    Eq(6, t.CursorX);
    Eq("́", c[5].Combining);
    Eq("a日本é", Row(t, 0));
    Feed(t, $"{E}[1;3HX");
    Eq(' ', (char)c[1].Rune);
    Eq("a X本é", Row(t, 0));
});

Test("UTF-8 split across reads", () =>
{
    var t = Term();
    var bytes = Encoding.UTF8.GetBytes("å😀");
    foreach (var b in bytes) t.Feed([b]);
    Eq("å😀", Row(t, 0));
});

Test("DEC line drawing charset", () =>
{
    var t = Term();
    Feed(t, $"{E}(0lqk{E}(Bx");
    Eq("┌─┐x", Row(t, 0));
});

Test("device queries are answered", () =>
{
    var t = Term();
    var replies = new List<string>();
    t.Reply += b => replies.Add(Encoding.ASCII.GetString(b));
    Feed(t, $"ab{E}[6n{E}[c{E}[>c{E}[?2004$p{E}]11;?{E}\\");
    Eq($"{E}[1;3R", replies[0]);
    Eq($"{E}[?62;22c", replies[1]);
    True(replies[2].StartsWith($"{E}[>"), "DA2");
    Eq($"{E}[?2004;2$y", replies[3]);
    True(replies[4].StartsWith($"{E}]11;rgb:"), "OSC 11");
});

Test("OSC title and clipboard", () =>
{
    var t = Term();
    string? title = null, clip = null;
    t.TitleChanged += s => title = s;
    t.ClipboardSet += s => clip = s;
    Feed(t, $"{E}]0;my title\a{E}]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes("copied"))}{E}\\ok");
    Eq("my title", title);
    Eq("copied", clip);
    Eq("ok", Row(t, 0));
});

Test("DCS and other strings are swallowed", () =>
{
    var t = Term();
    Feed(t, $"a{E}P+q544e{E}\\b{E}_ignored{E}\\c");
    Eq("abc", Row(t, 0));
});

Test("selection: characters, words, lines, wrapped text", () =>
{
    var t = Term(10, 4);
    Feed(t, "foo bar/baz\r\nsecond row");
    long top = t.ScreenTopId;
    t.StartSelection(top, 5, SelectionUnit.Word);
    Eq("bar/ba", t.GetSelectionText());      // word stops at the right margin of the row
    t.StartSelection(top, 0, SelectionUnit.Char);
    t.ExtendSelection(top + 1, 0);
    Eq("foo bar/baz", t.GetSelectionText()); // soft wrap joins without a newline
    t.StartSelection(top + 2, 3, SelectionUnit.Line);
    Eq("second row", t.GetSelectionText());
    t.StartSelection(top + 1, 0, SelectionUnit.Char);
    t.ExtendSelection(top + 2, 5);
    Eq("z\nsecond", t.GetSelectionText());
    t.StartSelection(top + 2, 5, SelectionUnit.Char);
    t.ExtendSelection(top + 2, 2);
    Eq("cond", t.GetSelectionText(), "backwards drag;");
});

Test("selection survives scrolling into history", () =>
{
    var t = Term(20, 3, 50);
    Feed(t, "keep me\r\n");
    t.StartSelection(t.ScreenTopId, 0, SelectionUnit.Line);
    for (int i = 0; i < 10; i++) Feed(t, "x\r\n");
    Eq("keep me", t.GetSelectionText());
});

Test("resize keeps content: rows move to and from history", () =>
{
    var t = Term(20, 6, 50);
    for (int i = 0; i < 6; i++) Feed(t, $"l{i}" + (i < 5 ? "\r\n" : ""));
    t.Resize(20, 3);
    Eq(3, t.ScrollbackCount);
    Eq("l3", Row(t, 0));
    Eq("l5", Row(t, 2));
    Eq(2, t.CursorY);
    t.Resize(20, 6);
    Eq(0, t.ScrollbackCount);
    Eq("l0", Row(t, 0));
    Eq(5, t.CursorY);
});

Test("resize narrower then wider does not lose text", () =>
{
    var t = Term(20, 3);
    Feed(t, "0123456789abcdefghij");
    t.Resize(8, 3);
    Eq("01234567", Row(t, 0));
    t.Resize(20, 3);
    Eq("0123456789abcdefghij", Row(t, 0));
});

Test("keyboard encoding", () =>
{
    var t = Term();
    string K(TermKey k, TermMods m = 0) => Encoding.ASCII.GetString(InputEncoder.Key(t, k, m));
    Eq($"{E}[A", K(TermKey.Up));
    Eq($"{E}[1;5C", K(TermKey.Right, TermMods.Ctrl));
    Eq($"{E}[3~", K(TermKey.Delete));
    Eq($"{E}[5;2~", K(TermKey.PageUp, TermMods.Shift));
    Eq($"{E}OP", K(TermKey.F1));
    Eq($"{E}[15~", K(TermKey.F5));
    Eq($"{E}[Z", K(TermKey.Tab, TermMods.Shift));
    Eq("\u007f", K(TermKey.Backspace));
    Feed(t, $"{E}[?1h");
    Eq($"{E}OA", K(TermKey.Up));
    Eq(3, InputEncoder.ControlCode('c'));
    Eq(0, InputEncoder.ControlCode(' '));
    Eq(27, InputEncoder.ControlCode('['));
});

Test("bracketed paste", () =>
{
    var t = Term();
    Eq("a\rb", Encoding.ASCII.GetString(InputEncoder.Paste(t, "a\r\nb")));
    Feed(t, $"{E}[?2004h");
    Eq($"{E}[200~x{E}[201~", Encoding.ASCII.GetString(InputEncoder.Paste(t, $"x{E}[201~")));
});

Test("mouse reporting", () =>
{
    var t = Term(80, 24);
    Eq(null, InputEncoder.Mouse(t, MouseAction.Press, 0, 1, 1, 0));
    Feed(t, $"{E}[?1000h{E}[?1006h");
    Eq($"{E}[<0;11;6M", Encoding.ASCII.GetString(InputEncoder.Mouse(t, MouseAction.Press, 0, 10, 5, 0)!));
    Eq($"{E}[<0;11;6m", Encoding.ASCII.GetString(InputEncoder.Mouse(t, MouseAction.Release, 0, 10, 5, 0)!));
    Eq($"{E}[<64;1;1M", Encoding.ASCII.GetString(InputEncoder.Mouse(t, MouseAction.WheelUp, -1, 0, 0, 0)!));
    Eq(null, InputEncoder.Mouse(t, MouseAction.Move, 0, 1, 1, 0));
    Feed(t, $"{E}[?1002h");
    Eq($"{E}[<32;2;2M", Encoding.ASCII.GetString(InputEncoder.Mouse(t, MouseAction.Move, 0, 1, 1, 0)!));
    Feed(t, $"{E}[?1006l");
    var legacy = InputEncoder.Mouse(t, MouseAction.Release, 0, 0, 0, 0)!;
    Eq(32 + 3, (int)legacy[3]);
});

Test("click-to-place computes cursor key presses", () =>
{
    var t = Term(10, 4);
    Feed(t, "$ echo hi");
    Eq(-4, t.ComputeCursorMove(0, 5));
    Eq(0, t.ComputeCursorMove(1, 3), "other line;");
    Eq(0, t.ComputeCursorMove(0, 9), "at or past the cursor at end of text;");
    Feed(t, " there");            // wraps onto row 1
    Eq(1, t.CursorY);
    Eq(-(5 + 5), t.ComputeCursorMove(0, 5), "across the soft wrap;");
    Eq($"{E}[D{E}[D", Encoding.ASCII.GetString(InputEncoder.CursorMove(t, -2)));
    Feed(t, $"{E}[?1049h");
    Eq(0, t.ComputeCursorMove(0, 0), "never on the alternate screen;");
});

Console.WriteLine("PTY");

static void WaitFor(TerminalSession s, Func<Terminal, bool> condition, string what, int timeoutMs = 8000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (true)
    {
        lock (s.Terminal.Sync)
        {
            if (condition(s.Terminal)) return;
            if (DateTime.UtcNow > deadline)
                throw new Exception($"timed out waiting for {what}. Screen:\n{ScreenText(s.Terminal)}");
        }
        Thread.Sleep(25);
    }
}

static bool Shows(Terminal t, string text) => ScreenText(t).Contains(text);
static void Type(TerminalSession s, string text) => s.Send(Encoding.UTF8.GetBytes(text));
bool unix = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

Test("spawns a command on a real terminal with the requested size", () =>
{
    var options = unix
        ? new PtyOptions { Command = "/bin/sh", Args = ["-c", "tty; stty size; echo done-$TERM; exit 7"], Cols = 100, Rows = 30 }
        : new PtyOptions { Command = "cmd.exe", Args = ["/c", "echo done-xterm-256color& exit 7"], Cols = 100, Rows = 30 };
    using var s = new TerminalSession(options);
    int? exit = null;
    s.Exited += c => exit = c;
    s.Start();
    WaitFor(s, t => Shows(t, "done-xterm-256color"), "command output");
    if (unix)
    {
        lock (s.Terminal.Sync)
        {
            True(Shows(s.Terminal, "/dev/"), "child should see a tty");
            True(Shows(s.Terminal, "30 100"), "child should see 30x100");
        }
    }
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (exit == null && DateTime.UtcNow < deadline) Thread.Sleep(20);
    Eq((int?)7, exit, "exit code");
});

Test("interactive shell: input, job control, resize", () =>
{
    if (!unix) throw new SkipException("unix only");
    using var s = new TerminalSession(new PtyOptions
    {
        Command = "/bin/bash", Args = ["--norc", "--noprofile", "-i"], Cols = 80, Rows = 24,
        Environment = new Dictionary<string, string?> { ["PS1"] = "PROMPT> " },
    });
    s.Start();
    WaitFor(s, t => Shows(t, "PROMPT>"), "prompt");
    lock (s.Terminal.Sync)
        True(!Shows(s.Terminal, "no job control") && !Shows(s.Terminal, "cannot set terminal"),
            "bash must get a controlling terminal:\n" + ScreenText(s.Terminal));
    Type(s, "echo sum=$((40+2))\r");
    WaitFor(s, t => Shows(t, "sum=42"), "echo");
    s.Resize(132, 40);
    Type(s, "stty size\r");
    WaitFor(s, t => Shows(t, "40 132"), "new size after resize");
    Type(s, "sleep 30\r");
    Thread.Sleep(200);
    Type(s, "\u0003echo interrupted-$?\r");
    WaitFor(s, t => Shows(t, "interrupted-130"), "Ctrl+C reaching the foreground job");
    Type(s, "printf 'å😀\\n'\r");
    WaitFor(s, t => ScreenText(t).Split('\n').Any(l => l == "å😀"), "UTF-8 round trip");
});

Test("tmux: runs, draws, splits, tracks the mouse, follows resize", () =>
{
    if (!unix) throw new SkipException("unix only");
    if (PtyFactory.FindOnPath("tmux") == null) throw new SkipException("tmux not installed");
    string socket = "avaterm-test-" + Environment.ProcessId;
    using var s = new TerminalSession(new PtyOptions
    {
        Command = "tmux", Args = ["-L", socket, "-f", "/dev/null", "new-session", "/bin/sh"], Cols = 100, Rows = 30,
        Environment = new Dictionary<string, string?> { ["PS1"] = "$ ", ["ENV"] = null },
    });
    try
    {
        s.Start();
        WaitFor(s, t => t.AltScreenActive && Row(t, 29).Contains("[0]"), "tmux status line on the last row");
        Type(s, "echo tmux-$((6*7))\r");
        WaitFor(s, t => Shows(t, "tmux-42"), "shell inside tmux");

        Type(s, "\u0002%");   // prefix + % : split left/right
        WaitFor(s, t => Enumerable.Range(0, 29).All(r => Row(t, r).Contains('│')), "pane border");

        Type(s, $"tmux -L {socket} set -g mouse on\r");
        WaitFor(s, t => t.MouseMode != MouseTracking.None && t.MouseSgr, "tmux enabling mouse reporting");

        // Click in the left pane; tmux should move the active pane (cursor jumps left of the border).
        byte[] press, release;
        lock (s.Terminal.Sync)
        {
            True(s.Terminal.CursorX > 50, "cursor starts in the right pane");
            press = InputEncoder.Mouse(s.Terminal, MouseAction.Press, 0, 5, 5, 0)!;
            release = InputEncoder.Mouse(s.Terminal, MouseAction.Release, 0, 5, 5, 0)!;
        }
        s.Send(press);
        s.Send(release);
        WaitFor(s, t => t.CursorX < 50, "mouse click selecting the left pane");

        s.Resize(120, 40);
        WaitFor(s, t => Row(t, 39).Contains("[0]"), "status line on the new last row after resize");
    }
    finally
    {
        try { System.Diagnostics.Process.Start("tmux", $"-L {socket} kill-server")?.WaitForExit(3000); }
        catch (Exception) { }
    }
});

// Runs the same full-screen program twice: once directly on our emulator, once inside a detached
// tmux whose own emulator serves as the reference, then compares the two screens.
void Reference(string name, string command, string[] args, params string[] keys)
{
    Test($"matches tmux's rendering of {name}", () =>
    {
        if (!unix) throw new SkipException("unix only");
        string? exe = PtyFactory.FindOnPath(command);
        if (PtyFactory.FindOnPath("tmux") == null || exe == null) throw new SkipException($"needs tmux and {command}");
        const int cols = 90, rows = 28;

        // Each side edits its own copy (same relative name), so editors do not trip over
        // each other's lock or swap files.
        string root = Path.Combine(Path.GetTempPath(), $"avaterm-test-{Environment.ProcessId}");
        string ourDir = Path.Combine(root, "ours"), refDir = Path.Combine(root, "reference");
        const string sample = "sample.txt";
        Directory.CreateDirectory(ourDir);
        Directory.CreateDirectory(refDir);
        var text = new StringBuilder();
        for (int i = 1; i <= 120; i++)
            text.Append($"{i:000}\tline with a tab, åäö 日本語 and {(i % 7 == 0 ? new string('x', 140) : "short text")}\n");
        File.WriteAllText(Path.Combine(ourDir, sample), text.ToString());
        File.WriteAllText(Path.Combine(refDir, sample), text.ToString());

        string socket = $"avaterm-ref-{Environment.ProcessId}-{name}";
        string Tmux(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tmux", $"-u -L {socket} -f /dev/null {arguments}")
                { RedirectStandardOutput = true, RedirectStandardError = true };
            // Same locale rule as UnixPty, so both sides run the program under identical conditions.
            if (Environment.GetEnvironmentVariable("LANG") == null && Environment.GetEnvironmentVariable("LC_ALL") == null)
                psi.Environment["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        static string Normalize(string screen) =>
            string.Join('\n', screen.Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');

        string commandLine = exe + " " + string.Join(' ', args.Append(sample));
        using var s = new TerminalSession(new PtyOptions
            { Command = exe, Args = [.. args, sample], Cols = cols, Rows = rows, WorkingDirectory = ourDir });
        try
        {
            Tmux($"new-session -d -x {cols} -y {rows} -c {refDir} \"{commandLine}\" ; set -g status off");
            s.Start();
            Thread.Sleep(600);
            foreach (var key in keys)
            {
                Type(s, key);
                Tmux($"send-keys -l \"{key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                Thread.Sleep(250);
            }
            string ours = "", reference = "";
            var deadline = DateTime.UtcNow.AddSeconds(6);
            do
            {
                Thread.Sleep(150);
                reference = Normalize(Tmux("capture-pane -p"));
                lock (s.Terminal.Sync) ours = Normalize(ScreenText(s.Terminal));
            } while (ours != reference && DateTime.UtcNow < deadline);

            if (ours != reference)
            {
                var a = ours.Split('\n');
                var b = reference.Split('\n');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    string x = i < a.Length ? a[i] : "<missing>", y = i < b.Length ? b[i] : "<missing>";
                    if (x != y)
                        throw new Exception($"row {i} differs\n      ours: {x}\n      tmux: {y}\n--- ours ---\n{ours}\n--- tmux ---\n{reference}");
                }
            }
            True(reference.Length > 200, "reference screen should not be empty");
        }
        finally
        {
            Tmux("kill-server");
            s.Dispose();
            Thread.Sleep(200);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });
}

Reference("less", "less", ["-N"], " ", "j", "j", "G", "k", "k", "k");
Reference("vim", "vim", ["-u", "NONE", "-N", "-n"], ":set nu\r", "\u0006", "10j", "dd", "ix\u001b", "\u0004", "kkkk", ":vsplit\r", "\u0005\u0005");
Reference("nano", "nano", [], "\u0016", "hello", "\u0016", "\u0019");

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped");
return failed == 0 ? 0 : 1;

sealed class SkipException(string message) : Exception(message);
