#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 87 — the dual-reaper race guard (finding 87)
// ================================================================
//
// Hypothesis: AttachAndOwn on a Process.Start'ed direct child emits exactly one TargetIsHostChild
// anomaly (pid = child, hostPid = self); on the /bin/sh-parented topology it emits none.
using System.Diagnostics;
using System.Text.RegularExpressions;
using SkyOmega.DrHook.Engine;

// Run from poc/drhook-engine/ after building the integration targets.
string exe = Path.GetFullPath("../../tests/DrHook.Engine.IntegrationTargets.Mtp/bin/Debug/net10.0/DrHook.Engine.IntegrationTargets.Mtp");
foreach (bool viaShell in new[] { false, true })
{
    var psi = viaShell ? new ProcessStartInfo("/bin/sh") : new ProcessStartInfo(exe);
    if (viaShell) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("\"$0\" \"$@\" & wait $!"); psi.ArgumentList.Add(exe); }
    psi.ArgumentList.Add("--debug"); psi.ArgumentList.Add("--filter"); psi.ArgumentList.Add("FullyQualifiedName~RunBriefObservableWork");
    psi.RedirectStandardOutput = true; psi.UseShellExecute = false;
    using Process boot = Process.Start(psi)!;
    int pid = -1; string? line;
    while ((line = boot.StandardOutput.ReadLine()) is not null) { var m = Regex.Match(line, @"Process Id:\s*(\d+)"); if (m.Success) { pid = int.Parse(m.Groups[1].Value); break; } }
    var sink = new BoundedAnomalySink(64);
    using (DebugSession s = DebugSession.AttachAndOwn(pid, sink)) { s.WaitForStop(TimeSpan.FromSeconds(5)); s.Resume(); }
    var hits = sink.Drain().Anomalies.Where(a => a.Kind == AnomalyKind.TargetIsHostChild).ToArray();
    Console.WriteLine($"viaShell={viaShell} target={pid} self={Environment.ProcessId} TargetIsHostChild={hits.Length} {string.Join(";", hits.Select(h => h.Observed))}");
    try { boot.Kill(true); } catch { }
}
