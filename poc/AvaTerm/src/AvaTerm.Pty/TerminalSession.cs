using AvaTerm.Core;

namespace AvaTerm.Pty;

/// <summary>Couples a <see cref="Terminal"/> to a child process on a pseudo terminal.</summary>
public sealed class TerminalSession : IDisposable
{
    readonly PtyOptions _options;
    PtyConnection? _pty;

    public Terminal Terminal { get; }
    public bool HasExited { get; private set; }

    /// <summary>Raised on the reader thread after new output has been applied.</summary>
    public event Action? Updated;
    /// <summary>Raised on a background thread with the exit code of the child.</summary>
    public event Action<int>? Exited;

    public TerminalSession(PtyOptions options, int scrollbackLines = 10_000)
    {
        _options = options;
        Terminal = new Terminal(options.Cols, options.Rows, scrollbackLines);
    }

    public void Start()
    {
        if (_pty != null) return;
        var pty = PtyFactory.Spawn(_options with { Cols = Terminal.Cols, Rows = Terminal.Rows });
        _pty = pty;
        Terminal.Reply += data => pty.Write(data);
        var reader = new Thread(() => ReadLoop(pty)) { IsBackground = true, Name = "pty-reader" };
        reader.Start();
        pty.Exited += code =>
        {
            reader.Join(500);   // let the last output land before announcing the exit
            HasExited = true;
            Exited?.Invoke(code);
        };
    }

    void ReadLoop(PtyConnection pty)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int n = pty.Read(buffer);
            if (n <= 0) break;
            lock (Terminal.Sync) Terminal.Feed(buffer.AsSpan(0, n));
            Updated?.Invoke();
        }
    }

    public void Send(ReadOnlySpan<byte> data) => _pty?.Write(data);

    public void Resize(int cols, int rows)
    {
        lock (Terminal.Sync)
        {
            if (cols == Terminal.Cols && rows == Terminal.Rows) return;
            Terminal.Resize(cols, rows);
            cols = Terminal.Cols;
            rows = Terminal.Rows;
        }
        _pty?.Resize(cols, rows);
    }

    public void Dispose() => _pty?.Dispose();
}
