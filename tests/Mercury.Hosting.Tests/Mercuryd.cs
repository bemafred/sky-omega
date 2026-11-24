// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// A mercuryd process a test started, as a service manager starts it: no input, and its standard error read as it
/// comes. Disposing it kills it if it still runs.
/// </summary>
internal sealed class Mercuryd : IDisposable
{
    /// <summary>The stop signals' numbers, the same on Linux and macOS.</summary>
    public const int Sigint = 2, Sigterm = 15;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _errorLines = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Mercuryd(IReadOnlyDictionary<string, string> environment, string[] args)
    {
        // The test project references mercuryd, so its launcher is beside the tests.
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mercuryd.exe" : "mercuryd"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        _process = new Process { StartInfo = start };
        _process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null)
                return;
            _errorLines.Enqueue(line.Data);
            if (line.Data == "Ready.")
                _ready.TrySetResult();
        };
        _process.OutputDataReceived += (_, _) => { };
        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        // A service manager gives a service no input, so its input ends at once. mercury-mcp stops there (ADR-065 Why).
        _process.StandardInput.Close();
    }

    /// <summary>Starts mercuryd with <paramref name="args"/>.</summary>
    public static Mercuryd Start(params string[] args) => new(new Dictionary<string, string>(), args);

    /// <summary>Starts mercuryd with <paramref name="args"/>, and <paramref name="environment"/> over this process's
    /// environment.</summary>
    public static Mercuryd Start(IReadOnlyDictionary<string, string> environment, params string[] args) =>
        new(environment, args);

    /// <summary>
    /// Starts mercuryd with the arguments <paramref name="args"/> gives for a free port, waits until it is ready, and
    /// gives it and the port. When it ends saying that the port is in use, which another took after the system named
    /// it, it is started again on another port (<see cref="FreePort"/>). Fails, with what it said, when it ends for
    /// another reason.
    /// </summary>
    public static Task<(Mercuryd Mercuryd, int Port)> Serve(Func<int, string[]> args, CancellationToken ct) =>
        Serve(new Dictionary<string, string>(), args, ct);

    /// <summary>
    /// <see cref="Serve(Func{int, string[]}, CancellationToken)"/>, with <paramref name="environment"/> over this
    /// process's environment.
    /// </summary>
    public static Task<(Mercuryd Mercuryd, int Port)> Serve(IReadOnlyDictionary<string, string> environment,
        Func<int, string[]> args, CancellationToken ct) => FreePort.Start(async port =>
    {
        var mercuryd = new Mercuryd(environment, args(port));
        if (await mercuryd.ReadyOrEnded(ct))
            return mercuryd;
        var (exitCode, errors) = (mercuryd._process.ExitCode, mercuryd.Errors);
        mercuryd.Dispose();
        if (!errors.Contains(": The port is in use."))
            Assert.Fail($"mercuryd ended with {exitCode} before it was ready:{Environment.NewLine}{errors}");
        return null;
    });

    /// <summary>The process's id.</summary>
    public int Id => _process.Id;

    /// <summary>What it has written to standard error so far.</summary>
    public string Errors => string.Join(Environment.NewLine, _errorLines);

    /// <summary>Waits until it says it is ready, its endpoints started. Fails, with what it said, when it ends
    /// first.</summary>
    public async Task WhenReady(CancellationToken ct)
    {
        if (!await ReadyOrEnded(ct))
            Assert.Fail($"mercuryd ended with {_process.ExitCode} before it was ready:{Environment.NewLine}{Errors}");
    }

    /// <summary>Whether it said it is ready, its endpoints started, before it ended.</summary>
    private async Task<bool> ReadyOrEnded(CancellationToken ct)
    {
        var ended = _process.WaitForExitAsync(ct);
        return await Task.WhenAny(_ready.Task, ended).WaitAsync(TimeSpan.FromSeconds(60), ct) == _ready.Task;
    }

    /// <summary>Its exit code, once it has ended and its standard error has been read to the end.</summary>
    public async Task<int> Ended(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        return _process.ExitCode;
    }

    /// <summary>Sends it a signal (Linux and macOS).</summary>
    public void Signal(int signal) => Assert.Equal(0, kill(_process.Id, signal));

    /// <summary>
    /// Whether SIGINT is ignored in this process, and so in every mercuryd it starts (Linux and macOS). A command that a
    /// non-interactive shell starts in the background (<c>&amp;</c>, <c>nohup</c>) has SIGINT ignored, by POSIX, and its
    /// children inherit that. .NET keeps a signal that is ignored at the start ignored, so mercuryd's handler for SIGINT
    /// never runs: SIGINT is not a stop signal for it then. Measured on zbook and elite, 2026-11-22.
    /// </summary>
    public static bool SigintIsIgnoredHere
    {
        get
        {
            // The handler is the first field of struct sigaction on Linux and on macOS; SIG_IGN is 1 on both.
            var current = Marshal.AllocHGlobal(256);
            try
            {
                Assert.Equal(0, sigaction(Sigint, IntPtr.Zero, current));
                return Marshal.ReadIntPtr(current) == 1;
            }
            finally
            {
                Marshal.FreeHGlobal(current);
            }
        }
    }

    /// <summary>Kills it: SIGKILL on Linux and macOS, TerminateProcess on Windows.</summary>
    public void Kill() => _process.Kill();

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(10_000);
        }
        _process.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int sigaction(int signal, IntPtr action, IntPtr oldAction);
}
