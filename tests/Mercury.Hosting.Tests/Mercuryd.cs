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

    private Mercuryd(params string[] args)
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
    public static Mercuryd Start(params string[] args) => new(args);

    /// <summary>The process's id.</summary>
    public int Id => _process.Id;

    /// <summary>What it has written to standard error so far.</summary>
    public string Errors => string.Join(Environment.NewLine, _errorLines);

    /// <summary>Waits until it says it is ready, its endpoints started. Fails, with what it said, when it ends
    /// first.</summary>
    public async Task WhenReady(CancellationToken ct)
    {
        var ended = _process.WaitForExitAsync(ct);
        var first = await Task.WhenAny(_ready.Task, ended).WaitAsync(TimeSpan.FromSeconds(60), ct);
        if (first != _ready.Task)
            Assert.Fail($"mercuryd ended with {_process.ExitCode} before it was ready:{Environment.NewLine}{Errors}");
    }

    /// <summary>Its exit code, once it has ended and its standard error has been read to the end.</summary>
    public async Task<int> Ended(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        return _process.ExitCode;
    }

    /// <summary>Sends it a signal (Linux and macOS).</summary>
    public void Signal(int signal) => Assert.Equal(0, kill(_process.Id, signal));

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
}
