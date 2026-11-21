// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// The mercury CLI as a process a test started: what is typed to it goes to its input, and its output is read as it
/// comes. Disposing it kills it if it still runs.
/// </summary>
internal sealed class MercuryCli : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly ConcurrentQueue<string> _errorLines = new();

    private MercuryCli(IReadOnlyDictionary<string, string> environment, string[] args)
    {
        // The test project references mercury, so its launcher is beside the tests.
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mercury.exe" : "mercury"))
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
        _process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is not null)
                _outputLines.Enqueue(line.Data);
        };
        _process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
                _errorLines.Enqueue(line.Data);
        };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Starts mercury with <paramref name="args"/>, and <paramref name="environment"/> over this process's
    /// environment.</summary>
    public static MercuryCli Start(IReadOnlyDictionary<string, string> environment, params string[] args) =>
        new(environment, args);

    /// <summary>What it has written to standard output so far, by line; a prompt is on the line that follows it.</summary>
    public string Output => string.Join(Environment.NewLine, _outputLines);

    /// <summary>What it has written to standard error so far.</summary>
    public string Errors => string.Join(Environment.NewLine, _errorLines);

    /// <summary>Types <paramref name="line"/> to it.</summary>
    public void Type(string line) => _process.StandardInput.WriteLine(line);

    /// <summary>Ends its input, as Ctrl+D does.</summary>
    public void EndInput() => _process.StandardInput.Close();

    /// <summary>Waits until a line of its output starts with <paramref name="start"/>. Fails, with what it said, when
    /// it ends first.</summary>
    public async Task WhenSaid(string start, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!_outputLines.Any(line => line.StartsWith(start, StringComparison.Ordinal)))
        {
            if (_process.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(60))
                Xunit.Assert.Fail($"mercury did not say '{start}':{Environment.NewLine}{Output}{Environment.NewLine}{Errors}");
            await Task.Delay(50, ct);
        }
    }

    /// <summary>Its exit code, once it has ended and its output has been read to the end.</summary>
    public async Task<int> Ended(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(10_000);
        }
        _process.Dispose();
    }
}
