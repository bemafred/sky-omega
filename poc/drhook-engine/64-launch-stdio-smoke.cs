#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 64 — does a launched debuggee reach the host's standard streams? =======================
//
// ADR-011 D2 isolated a launched debuggee's console on POSIX (finding 75): under drhook-mcp, the server's stdin and
// stdout are the JSON-RPC channel, so a debuggee that inherits them corrupts it. The Windows launch path still uses
// dbgshim's CreateProcessForLaunch, which takes no stdio handles. Finding 96 listed that as a gap, inferred from a
// code comment. This probe OBSERVES it.
//
// The probe plays the MCP server: the outer process starts itself again ("inner") with stdin, stdout and stderr
// redirected to pipes, as an MCP client starts drhook-mcp. The inner process launches the target with the public
// DebugSession.Launch and runs it to exit. The outer process writes one line to the inner's stdin (an MCP request)
// and collects the inner's stdout and stderr.
//
// Hypothesis: on Windows, the target's markers reach the inner's stdout/stderr pipes, and the target may consume
// the stdin line (it inherits all three handles). On macOS/Linux (ADR-011 D2) neither happens.
//
// Result codes: 0 isolated (no marker on the host streams, stdin line not taken); 1 LEAK observed; 2 usage;
//   3 target build failed; 4 inner failed.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 64-launch-stdio-smoke.cs

using System;
using System.Diagnostics;
using System.IO;
using SkyOmega.DrHook.Engine;

if (args.Length >= 1 && args[0] == "inner") return Inner(args[1], args[2]);
return Outer();

static int Outer()
{
    string? dll = BuildTarget(Path.GetFullPath("64-launch-stdio-target.cs"));
    if (dll is null) { Console.Error.WriteLine("FAIL(3): could not build 64-launch-stdio-target.cs"); return 3; }
    string report = Path.Combine(Path.GetTempPath(), $"probe64-report-{Environment.ProcessId}.txt");
    File.Delete(report);

    var psi = new ProcessStartInfo(Environment.ProcessPath!)
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    psi.ArgumentList.Add("inner");
    psi.ArgumentList.Add(dll);
    psi.ArgumentList.Add(report);
    using Process inner = Process.Start(psi)!;
    inner.StandardInput.WriteLine("PROBE64_MCP_REQUEST_LINE");
    inner.StandardInput.Flush();
    var stderrTask = inner.StandardError.ReadToEndAsync();
    string hostStdout = inner.StandardOutput.ReadToEnd();
    string hostStderr = stderrTask.Result;
    inner.WaitForExit();

    Console.WriteLine($"platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"inner exit code: {inner.ExitCode}");
    Console.WriteLine("---- host stdout (the MCP channel) ----");
    Console.Write(hostStdout);
    Console.WriteLine("---- host stderr ----");
    Console.Write(hostStderr);
    Console.WriteLine("---- target report ----");
    Console.Write(File.Exists(report) ? File.ReadAllText(report) : "(no report: the target did not finish)\n");
    if (inner.ExitCode != 0) { Console.WriteLine("FAIL(4): inner failed"); return 4; }

    bool stdoutLeak = hostStdout.Contains("PROBE64_TARGET_", StringComparison.Ordinal);
    bool stderrLeak = hostStderr.Contains("PROBE64_TARGET_", StringComparison.Ordinal);
    bool stdinTaken = File.Exists(report) && File.ReadAllText(report).Contains("stdin=PROBE64_MCP_REQUEST_LINE", StringComparison.Ordinal);
    Console.WriteLine($"stdout leak: {stdoutLeak}; stderr leak: {stderrLeak}; stdin line taken by target: {stdinTaken}");
    if (stdoutLeak || stderrLeak || stdinTaken) { Console.WriteLine("RESULT(1): LEAK — the launched debuggee reaches the host's streams"); return 1; }
    Console.WriteLine("RESULT(0): isolated");
    return 0;
}

static int Inner(string dll, string report)
{
    string apphost = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
    (string program, string[] targetArgs) = File.Exists(apphost)
        ? (apphost, new[] { report })
        : ("dotnet", new[] { dll, report });

    var sink = new BoundedAnomalySink(64);
    using (DebugSession session = DebugSession.Launch(program, targetArgs, Path.GetDirectoryName(dll), sink))
    {
        for (int stops = 0; stops < 50; stops++)
        {
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(15));
            if (stop is null) { Console.Error.WriteLine("inner: no stop within 15 s"); return 5; }
            if (stop.Reason == StopReason.ProcessExited) break;
            session.Resume();
        }
    }
    // The inner's own legitimate output on the "MCP channel".
    Console.WriteLine("INNER_DONE");
    return 0;
}

static string? BuildTarget(string csPath)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c Debug")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string outp = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(outp); return null; }
    foreach (var line in outp.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            string path = line[(arrow + 3)..].Trim();
            if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
        }
    }
    return null;
}
