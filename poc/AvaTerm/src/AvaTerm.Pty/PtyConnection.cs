using System.Collections.Concurrent;

namespace AvaTerm.Pty;

public sealed record PtyOptions
{
    /// <summary>Program to run. Null picks the user's shell.</summary>
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    /// <summary>Variables added to (or, with a null value, removed from) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>A child process attached to a pseudo terminal.</summary>
public abstract class PtyConnection : IDisposable
{
    readonly BlockingCollection<byte[]> _outbox = new();
    readonly Thread _writer;
    int _exitRaised;

    public int ProcessId { get; protected set; }
    public int? ExitCode { get; private set; }

    readonly object _exitLock = new();
    Action<int>? _exited;

    /// <summary>
    /// Raised once, on a background thread, when the child process ends. A handler added after
    /// the exit is still called, so a short-lived child cannot slip past its subscriber.
    /// </summary>
    public event Action<int>? Exited
    {
        add
        {
            if (value == null) return;
            int? code;
            lock (_exitLock)
            {
                code = ExitCode;
                if (code == null) _exited += value;
            }
            if (code is { } c) Task.Run(() => value(c));
        }
        remove
        {
            lock (_exitLock) _exited -= value;
        }
    }

    protected PtyConnection()
    {
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "pty-writer" };
    }

    protected void StartWriter() => _writer.Start();

    /// <summary>Blocks until output is available. Returns 0 once the terminal is closed.</summary>
    public abstract int Read(byte[] buffer);

    /// <summary>Queues input for the child. Never blocks the caller.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _outbox.IsAddingCompleted) return;
        try { _outbox.Add(data.ToArray()); }
        catch (InvalidOperationException) { }
    }

    public abstract void Resize(int cols, int rows);

    protected abstract void WriteCore(byte[] data);

    void WriterLoop()
    {
        try
        {
            foreach (var chunk in _outbox.GetConsumingEnumerable()) WriteCore(chunk);
        }
        catch (Exception) { /* terminal went away */ }
    }

    protected void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        Action<int>? handlers;
        lock (_exitLock)
        {
            ExitCode = code;
            handlers = _exited;
        }
        handlers?.Invoke(code);
    }

    public void Dispose()
    {
        _outbox.CompleteAdding();
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected abstract void DisposeCore();
}

public static class PtyFactory
{
    public static PtyConnection Spawn(PtyOptions options)
    {
        if (OperatingSystem.IsWindows()) return new WindowsPty(options);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return new UnixPty(options);
        throw new PlatformNotSupportedException("No pseudo terminal implementation for this platform.");
    }

    /// <summary>The shell a new tab starts when nothing is configured.</summary>
    public static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
                if (FindOnPath(name) is { } found) return found;
            return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell) && File.Exists(shell)) return shell;
        foreach (var candidate in new[] { "/bin/zsh", "/bin/bash", "/bin/sh" })
            if (File.Exists(candidate)) return candidate;
        return "/bin/sh";
    }

    public static string? FindOnPath(string file)
    {
        if (file.Contains(Path.DirectorySeparatorChar) || file.Contains('/'))
            return File.Exists(file) ? file : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, file);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static Dictionary<string, string> BuildEnvironment(PtyOptions options, bool ignoreCase)
    {
        var env = new Dictionary<string, string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        foreach (var (k, v) in options.Environment)
        {
            if (v == null) env.Remove(k);
            else env[k] = v;
        }
        return env;
    }
}
