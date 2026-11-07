#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 106 — the debugger in a worker process, and a supervisor that recovers from losing it
// (ADR-016 Step 5) ======================================================================================================
//
// The worker is decided (ADR-016, 2026-11-06): on macOS and Linux a crash during a debugger request leaves mscordbi's
// event thread waiting forever and the DAC's lock held (findings 100, 104, 105), and nothing inside the process can
// recover. This probe proves the design, not the need. Three files:
//   106-supervisor.cs (this) — never references DrHook.Engine, so dbgshim, mscordbi and the DAC never load here.
//   106-worker.cs — hosts DrHook.Engine and runs one session per process, driven by NDJSON over its stdin/stdout.
//   106-worker-target.cs — a target that ends by itself and writes its progress to a beat file, so the supervisor can
//             tell without a debugger whether it runs, is held, or finished.
// The crashing target is probe 100's (100-orphaned-read-target.cs, flood-segv: it floods thread events and faults in
// native code 0.5 to 3 s after a debugger attaches, with no warning stop; finding 104: it hung the debugger in 1 to 7
// of 10 runs on Unix, never on Windows).
//
// Before the supervisor discards a worker it writes its intent and the target's last known state to a local journal
// (one JSON line, flushed to disk), then ends the worker with the OS: after a lost debugger the documented way to end
// anything is the operating system's (DebuggerError's contract). It ends only its own worker; a target is ended only
// when it is the probe's own and is left running at the end of a run, and that is recorded.
//
// Modes (one run = one line):
//   induce-and-recover — Hypothesis: the worker detects its own lost debugger (the target gone, no ProcessExited within
//             2 s; finding 104's bound is 175 ms), the supervisor discards it, and a fresh worker's session succeeds
//             (launch, a Break stop, locals, a natural end with ProcessExited). Construction: up to 8 attempts, each a
//             worker that launches the crashing target and continues every stop; the first stuck attempt is discarded;
//             then a fresh session. Recorded: time to detect and to recover, what the discarded worker left (the dead
//             target's process entry), the worker's start time. Predicted: on Unix, a stuck attempt in
//             at least one run and the fresh session succeeding every run; on Windows, no stuck attempt and the fresh
//             session succeeding every run.
//   held-launched, held-attached — Question (not predicted; unknown on every platform, ADR-016): what happens to a
//             target held at a stop when its worker dies? Construction: a worker launches the target (held-launched) or
//             attaches to one the supervisor started (held-attached), waits for its Break stop, reads its locals; the
//             supervisor discards the healthy worker and watches the target for 10 s: died, resumed (its beat
//             advances), or held (alive, no progress). Then a fresh worker attaches to it if it is alive (bounded at
//             30 s), detaches, and the target is given 60 s to finish by itself. Predicted only: the supervisor starts
//             a fresh worker every run.
//   supervisor-detects — Hypothesis: the supervisor detects a lost debugger from outside, with no verdict from the
//             worker: a command not answered within its bound. Construction: as induce-and-recover, but the worker
//             reports only facts (target gone, ProcessExited or not) and the supervisor ignores them for its decision;
//             after each crash it asks the same worker to attach to a fresh target, bounded at 30 s. No answer:
//             suspected unresponsive; it discards the worker and runs a fresh session. Afterwards the decision is
//             compared with the worker's facts. Predicted: on Unix, a suspected
//             worker in at least one run, every suspicion agreeing with the facts (no ProcessExited), the fresh session
//             succeeding every run; on Windows, no suspicion and the fresh session succeeding every run.
//
// Result codes: induce-and-recover: induced-fresh-ok, not-induced-fresh-ok, or …-fresh-failed-<step>; held-*:
// <launched|attached>-<died|resumed|held>-<reattach-ok|reattach-hung|reattach-failed|no-reattach>-<ended|left-killed>;
// supervisor-detects: suspected-agrees-fresh-ok, suspected-disagrees-…, not-suspected-fresh-ok; setup-failed-<step>.
// RESULT(0) when every mode's prediction held, 1 otherwise, 2 when a build failed.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 106-supervisor.cs [runs] [mode ...]
//   dotnet run --no-cache 106-supervisor.cs 10 induce-and-recover held-launched held-attached supervisor-detects

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

string[] allModes = ["induce-and-recover", "held-launched", "held-attached", "supervisor-detects"];
int runs = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1;
string[] modes = args.Length > 1 ? args[1..] : allModes;
foreach (string m in modes)
    if (!allModes.Contains(m)) { Console.WriteLine($"unknown mode '{m}'; modes: {string.Join(", ", allModes)}"); return 1; }

string? workerDll = Build(Path.GetFullPath("106-worker.cs"));
string? workTarget = Build(Path.GetFullPath("106-worker-target.cs"));
string? crashTarget = Build(Path.GetFullPath("100-orphaned-read-target.cs"));
if (workerDll is null || workTarget is null || crashTarget is null) { Console.WriteLine("FAIL(2): a build failed"); return 2; }
bool windows = OperatingSystem.IsWindows();
var journal = new Journal(Path.Combine(Path.GetTempPath(), $"probe106-journal-{Environment.ProcessId}.ndjson"));
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, supervisor {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"journal: {journal.Path}");

var results = new Dictionary<string, List<string>>();
foreach (string mode in modes)
{
    for (int i = 1; i <= runs; i++)
    {
        var run = new Run($"{mode} run {i}");
        string result = mode switch
        {
            "induce-and-recover" => InduceAndRecover(run, workerDll, crashTarget, workTarget, journal),
            "held-launched" => HeldTarget(run, workerDll, workTarget, journal, launched: true),
            "held-attached" => HeldTarget(run, workerDll, workTarget, journal, launched: false),
            _ => SupervisorDetects(run, workerDll, crashTarget, workTarget, journal),
        };
        Console.WriteLine($"{mode} run {i}: {result}");
        if (!results.TryGetValue(mode, out List<string>? list)) results[mode] = list = [];
        list.Add(result);
    }
}

Console.WriteLine(string.Join("; ", results.SelectMany(kv => kv.Value.GroupBy(r => r).Select(g => $"{kv.Key} {g.Key}: {g.Count()}"))));
var verdicts = new List<bool>();
foreach ((string mode, List<string> outcomes) in results)
{
    bool freshEveryRun = outcomes.All(r => r.EndsWith("-fresh-ok", StringComparison.Ordinal));
    (string text, bool holds) = mode switch
    {
        "induce-and-recover" when windows => ("no stuck attempt, the fresh session succeeding every run",
            freshEveryRun && outcomes.All(r => r.StartsWith("not-induced-", StringComparison.Ordinal))),
        "induce-and-recover" => ("a stuck attempt in at least one run, the fresh session succeeding every run",
            freshEveryRun && outcomes.Any(r => r.StartsWith("induced-", StringComparison.Ordinal))),
        "supervisor-detects" when windows => ("no suspicion, the fresh session succeeding every run",
            freshEveryRun && outcomes.All(r => r.StartsWith("not-suspected-", StringComparison.Ordinal))),
        "supervisor-detects" => ("a suspicion in at least one run, every one agreeing with the worker's facts, the fresh session succeeding every run",
            freshEveryRun && outcomes.Any(r => r.StartsWith("suspected-", StringComparison.Ordinal))
            && outcomes.All(r => !r.StartsWith("suspected-disagrees", StringComparison.Ordinal))),
        _ => ("a fresh worker started every run (the target's fate is recorded, not predicted)",
            outcomes.All(r => !r.StartsWith("setup-failed", StringComparison.Ordinal) && !r.Contains("worker-not-ready", StringComparison.Ordinal))),
    };
    Console.WriteLine($"{mode}: predicted {text}: {(holds ? "holds" : "does not hold")} over {outcomes.Count} runs");
    verdicts.Add(holds);
}
bool confirmed = verdicts.All(v => v);
Console.WriteLine(confirmed
    ? "RESULT(0): confirmed — every mode's prediction held (see the header for each hypothesis)"
    : "RESULT(1): not confirmed as stated (see the outcomes above)");
return confirmed ? 0 : 1;

// ---- induce-and-recover --------------------------------------------------------------------------------------------
static string InduceAndRecover(Run run, string workerDll, string crashTarget, string workTarget, Journal journal)
{
    bool induced = false;
    long discardedAt = -1;
    for (int attempt = 1; attempt <= 8 && !induced; attempt++)
    {
        run.Say($"attempt {attempt}");
        Worker worker = Worker.Start(workerDll, run);
        if (worker.Await(["ready"], TimeSpan.FromSeconds(30)) is null) { worker.Discard(journal, "worker-not-ready", null); return "setup-failed-worker-not-ready"; }
        run.Say($"worker {worker.Process.Id} ready {run.Ms - worker.SpawnedAt} ms after its start");
        worker.Send(new JsonObject { ["cmd"] = "launch", ["args"] = new JsonArray(crashTarget, "flood-segv") });
        JsonObject? started = worker.Await(["started", "error"], TimeSpan.FromSeconds(30));
        if (started?["ev"]?.ToString() != "started") { worker.Discard(journal, "launch-failed", null); return "setup-failed-launch"; }
        int pid = (int)started["pid"]!;
        long? identity = ProcessFacts.Read(pid)?.Start;
        run.Say($"target {pid} launched by the worker");

        worker.Send(new JsonObject { ["cmd"] = "run-to-end", ["detect"] = true, ["exitedMs"] = 2000 });
        long goneAt = -1;
        bool stuck = false;
        JsonObject? end = worker.Await(["end"], TimeSpan.FromSeconds(60), seen: e =>
        {
            string ev = e["ev"]!.ToString();
            if (ev == "target-gone") goneAt = run.Ms;
            if (ev == "debugger-stuck") stuck = true;
            if (ev is "target-gone" or "exited" or "no-exited" or "debugger-lost" or "anomaly") run.Say($"worker: {e.ToJsonString()}");
        });
        if (end is null) { run.Say("the worker did not answer run-to-end within 60 s"); stuck = true; }
        if (!stuck)
        {
            run.Say("the debugger stayed usable; the worker is closed");
            worker.CloseOrDiscard(journal);
            continue;
        }
        induced = true;
        run.Say($"the worker reports its debugger stuck, {(goneAt >= 0 ? $"{run.Ms - goneAt} ms after it saw the target gone" : "the target's end not reported")}");
        var lastKnown = new JsonObject
        {
            ["pid"] = pid, ["state"] = "crashed: gone", ["observedBy"] = "worker",
            ["observedAtRunMs"] = goneAt, ["certainty"] = "observed (waitid on the worker's own child)",
        };
        long discardMs = worker.Discard(journal, "debugger-stuck: target gone, no ProcessExited within 2 s", lastKnown);
        discardedAt = run.Ms;
        run.Say($"discarded the worker in {discardMs} ms");
        Thread.Sleep(1000);
        run.Say($"1 s later the target {ProcessFacts.Describe(pid, identity)}");
    }
    string fresh = FreshSession(run, workerDll, workTarget);
    if (discardedAt >= 0 && fresh == "fresh-ok") run.Say($"recovered: a fresh session completed {run.Ms - discardedAt} ms after the discard");
    return $"{(induced ? "induced" : "not-induced")}-{fresh}";
}

// A fresh worker's whole session: launch, a Break stop, locals, a natural end with ProcessExited, a clean close.
static string FreshSession(Run run, string workerDll, string workTarget)
{
    string beat = Path.Combine(Path.GetTempPath(), $"probe106-beat-{Guid.NewGuid():N}");
    Worker worker = Worker.Start(workerDll, run);
    try
    {
        if (worker.Await(["ready"], TimeSpan.FromSeconds(30)) is null) { worker.Discard(null, "fresh worker not ready", null); return "fresh-failed-ready"; }
        worker.Send(new JsonObject { ["cmd"] = "launch", ["args"] = new JsonArray(workTarget, beat, "20") });
        if (worker.Await(["started", "error"], TimeSpan.FromSeconds(30))?["ev"]?.ToString() != "started") { worker.Discard(null, "fresh launch failed", null); return "fresh-failed-launch"; }
        worker.Send(new JsonObject { ["cmd"] = "wait-break", ["ms"] = 20000 });
        JsonObject? stop = worker.Await(["stop", "no-stop", "error"], TimeSpan.FromSeconds(30));
        if (stop?["reason"]?.ToString() != "Break") { worker.Discard(null, "fresh: no Break stop", null); return "fresh-failed-break"; }
        worker.Send(new JsonObject { ["cmd"] = "locals" });
        JsonObject? locals = worker.Await(["locals", "error"], TimeSpan.FromSeconds(30));
        string? round = locals?["values"]?["round"]?.ToString();
        if (round is null) { worker.Discard(null, "fresh: no locals", null); return "fresh-failed-locals"; }
        worker.Send(new JsonObject { ["cmd"] = "run-to-end", ["detect"] = true, ["exitedMs"] = 2000 });
        JsonObject? end = worker.Await(["end"], TimeSpan.FromSeconds(60));
        if (end?["outcome"]?.ToString() != "exited") { worker.Discard(null, $"fresh: ended {end?["outcome"]}", null); return "fresh-failed-end"; }
        if (!worker.CloseOrDiscard(null)) return "fresh-failed-close";
        run.Say($"fresh session: Break with round={round}, natural end with ProcessExited, beat '{Beat.Read(beat)}', worker closed");
        return "fresh-ok";
    }
    finally { try { File.Delete(beat); } catch (IOException) { } }
}

// ---- held-launched, held-attached ----------------------------------------------------------------------------------
static string HeldTarget(Run run, string workerDll, string workTarget, Journal journal, bool launched)
{
    string variant = launched ? "launched" : "attached";
    string beat = Path.Combine(Path.GetTempPath(), $"probe106-beat-{Guid.NewGuid():N}");
    Process? ownTarget = null;
    try
    {
        Worker worker = Worker.Start(workerDll, run);
        if (worker.Await(["ready"], TimeSpan.FromSeconds(30)) is null) { worker.Discard(journal, "worker-not-ready", null); return "setup-failed-worker-not-ready"; }
        int pid;
        if (launched)
            worker.Send(new JsonObject { ["cmd"] = "launch", ["args"] = new JsonArray(workTarget, beat, "600") });
        else
        {
            ownTarget = StartOwnTarget(workTarget, beat, 600);
            worker.Send(new JsonObject { ["cmd"] = "attach", ["pid"] = ownTarget.Id });
        }
        JsonObject? started = worker.Await(["started", "error"], TimeSpan.FromSeconds(30));
        if (started?["ev"]?.ToString() != "started") { worker.Discard(journal, "start failed", null); return "setup-failed-start"; }
        pid = (int)started["pid"]!;
        long? identity = ProcessFacts.Read(pid)?.Start;
        worker.Send(new JsonObject { ["cmd"] = "wait-break", ["ms"] = 20000 });
        JsonObject? stop = worker.Await(["stop", "no-stop", "error"], TimeSpan.FromSeconds(30));
        if (stop?["reason"]?.ToString() != "Break") { worker.Discard(journal, "no Break stop", null); return "setup-failed-break"; }
        worker.Send(new JsonObject { ["cmd"] = "locals" });
        string? round = worker.Await(["locals", "error"], TimeSpan.FromSeconds(30))?["values"]?["round"]?.ToString();
        string beatAtStop = Beat.Read(beat);
        run.Say($"target {pid} ({variant}) held at a Break stop, round={round ?? "not read"}, beat '{beatAtStop}', {ProcessFacts.Describe(pid, identity)}");

        var lastKnown = new JsonObject
        {
            ["pid"] = pid, ["state"] = "held at a Break stop", ["round"] = round, ["observedBy"] = "worker",
            ["observedAtRunMs"] = run.Ms, ["certainty"] = "observed",
        };
        long discardMs = worker.Discard(journal, "held-target: the healthy worker is ended with its target at a stop", lastKnown);
        long killedAt = run.Ms;
        run.Say($"discarded the worker in {discardMs} ms");

        // Watch the target for 10 s without a debugger.
        string fate = "held";
        string lastSeen = "";
        while (run.Ms - killedAt < 10_000)
        {
            ProcessFacts.Snapshot? facts = ProcessFacts.Read(pid);
            string b = Beat.Read(beat);
            string seen = facts is null || facts.Value.Start != identity ? "gone" : $"{facts.Value.State}, beat '{b}'";
            if (seen != lastSeen) { run.Say($"{run.Ms - killedAt} ms after the discard: target {seen}"); lastSeen = seen; }
            if (facts is null || facts.Value.Start != identity || facts.Value.State == "zombie") { fate = "died"; break; }
            if (b != beatAtStop) fate = "resumed";
            Thread.Sleep(250);
        }
        run.Say($"the target's fate without its worker: {fate}");

        string reattach = "no-reattach";
        if (fate != "died")
        {
            Worker second = Worker.Start(workerDll, run);
            if (second.Await(["ready"], TimeSpan.FromSeconds(30)) is null) { second.Discard(journal, "second worker not ready", null); return $"{variant}-{fate}-worker-not-ready"; }
            second.Send(new JsonObject { ["cmd"] = "attach", ["pid"] = pid });
            JsonObject? attached = second.Await(["started", "error"], TimeSpan.FromSeconds(30));
            if (attached is null)
            {
                reattach = "reattach-hung";
                second.Discard(journal, "reattach not answered within 30 s", new JsonObject { ["pid"] = pid, ["state"] = $"{fate} (no debugger)", ["certainty"] = "observed" });
            }
            else if (attached["ev"]!.ToString() == "error")
            {
                reattach = "reattach-failed";
                run.Say($"reattach: {attached.ToJsonString()}");
                second.CloseOrDiscard(journal);
            }
            else
            {
                reattach = "reattach-ok";
                second.Send(new JsonObject { ["cmd"] = "wait-break", ["ms"] = 10000 });
                JsonObject? again = second.Await(["stop", "no-stop", "error"], TimeSpan.FromSeconds(20));
                run.Say($"reattached; next stop: {again?.ToJsonString() ?? "none within 20 s"}");
                second.CloseOrDiscard(journal);
            }
        }

        // Give the target 60 s to finish by itself (600 rounds of 50 ms is 30 s of work).
        string ending = "ended";
        var waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < TimeSpan.FromSeconds(60))
        {
            ProcessFacts.Snapshot? facts = ProcessFacts.Read(pid);
            if (facts is null || facts.Value.Start != identity || facts.Value.State == "zombie") break;
            Thread.Sleep(250);
        }
        ProcessFacts.Snapshot? left = ProcessFacts.Read(pid);
        if (left is not null && left.Value.Start == identity && left.Value.State != "zombie")
        {
            ending = "left-killed";
            journal.Record(new JsonObject { ["act"] = "kill-target", ["pid"] = pid, ["reason"] = "probe 106's own target still running 60 s after its last session", ["beat"] = Beat.Read(beat) });
            ProcessFacts.Kill(pid);
            run.Say($"the target was still running 60 s later (beat '{Beat.Read(beat)}'); killed, recorded in the journal");
        }
        else run.Say($"the target ended by itself; beat '{Beat.Read(beat)}'");
        return $"{variant}-{fate}-{reattach}-{ending}";
    }
    finally
    {
        ownTarget?.Dispose();
        try { File.Delete(beat); } catch (IOException) { }
    }
}

// ---- supervisor-detects --------------------------------------------------------------------------------------------
static string SupervisorDetects(Run run, string workerDll, string crashTarget, string workTarget, Journal journal)
{
    string? verdict = null;
    long discardedAt = -1;
    for (int attempt = 1; attempt <= 8 && verdict is null; attempt++)
    {
        run.Say($"attempt {attempt}");
        Worker worker = Worker.Start(workerDll, run);
        if (worker.Await(["ready"], TimeSpan.FromSeconds(30)) is null) { worker.Discard(journal, "worker-not-ready", null); return "setup-failed-worker-not-ready"; }
        worker.Send(new JsonObject { ["cmd"] = "launch", ["args"] = new JsonArray(crashTarget, "flood-segv") });
        JsonObject? started = worker.Await(["started", "error"], TimeSpan.FromSeconds(30));
        if (started?["ev"]?.ToString() != "started") { worker.Discard(journal, "launch-failed", null); return "setup-failed-launch"; }
        int crashPid = (int)started["pid"]!;

        // The worker reports facts only (no verdict); the supervisor records them and does not decide on them.
        worker.Send(new JsonObject { ["cmd"] = "run-to-end", ["detect"] = false, ["exitedMs"] = 2000 });
        string facts = "none";
        JsonObject? end = worker.Await(["end"], TimeSpan.FromSeconds(60), seen: e =>
        {
            string ev = e["ev"]!.ToString();
            if (ev is "exited" or "no-exited") facts = ev;
        });
        run.Say($"crash target {crashPid}: the worker's facts: {facts}{(end is null ? " (run-to-end not answered within 60 s)" : "")}");

        // The next session in the same worker: attach to a fresh target, bounded from here.
        string beat = Path.Combine(Path.GetTempPath(), $"probe106-beat-{Guid.NewGuid():N}");
        using Process next = StartOwnTarget(workTarget, beat, 40);
        long? nextIdentity = ProcessFacts.Read(next.Id)?.Start;
        var asking = Stopwatch.StartNew();
        JsonObject? answer = null;
        if (end is not null)
        {
            worker.Send(new JsonObject { ["cmd"] = "attach", ["pid"] = next.Id });
            answer = worker.Await(["started", "error"], TimeSpan.FromSeconds(30));
        }
        if (answer is not null)
        {
            run.Say($"the worker answered the next attach in {asking.ElapsedMilliseconds} ms: {answer["ev"]}{(facts == "no-exited" ? " (although no ProcessExited came)" : "")}");
            worker.CloseOrDiscard(journal);
            WaitForOwnTargetEnd(run, next, nextIdentity, journal);
            try { File.Delete(beat); } catch (IOException) { }
            continue;
        }
        long suspectedAt = run.Ms;
        run.Say($"suspected unresponsive: {(end is null ? "run-to-end" : "the next attach")} not answered within its bound");
        verdict = facts == "no-exited" ? "agrees" : "disagrees";
        long discardMs = worker.Discard(journal, "suspected unresponsive: a command not answered within its bound",
            new JsonObject { ["pid"] = crashPid, ["state"] = "crashed: gone (the worker's report)", ["observedBy"] = "worker", ["certainty"] = "reported by a worker later suspected" });
        discardedAt = run.Ms;
        run.Say($"discarded the worker in {discardMs} ms, {discardedAt - suspectedAt} ms after the suspicion; the decision {verdict} with the worker's facts ({facts})");
        WaitForOwnTargetEnd(run, next, nextIdentity, journal);
        try { File.Delete(beat); } catch (IOException) { }
    }
    string fresh = FreshSession(run, workerDll, workTarget);
    if (discardedAt >= 0 && fresh == "fresh-ok") run.Say($"recovered: a fresh session completed {run.Ms - discardedAt} ms after the discard");
    return verdict is null ? $"not-suspected-{fresh}" : $"suspected-{verdict}-{fresh}";
}

// A target the supervisor started itself (the attach variants): its own child, read and dropped output.
static Process StartOwnTarget(string workTarget, string beat, int rounds)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add(workTarget);
    psi.ArgumentList.Add(beat);
    psi.ArgumentList.Add(rounds.ToString(CultureInfo.InvariantCulture));
    Process target = Process.Start(psi)!;
    target.OutputDataReceived += (_, _) => { };
    target.ErrorDataReceived += (_, _) => { };
    target.BeginOutputReadLine();
    target.BeginErrorReadLine();
    Thread.Sleep(1000); // lets main start; the attach waits for the target's debugger readiness itself (finding 99)
    return target;
}

static void WaitForOwnTargetEnd(Run run, Process target, long? identity, Journal journal)
{
    if (target.WaitForExit(30_000)) return;
    journal.Record(new JsonObject { ["act"] = "kill-target", ["pid"] = target.Id, ["reason"] = "probe 106's own target still running 30 s after its session" });
    run.Say($"own target {target.Id} still running 30 s after its session ({ProcessFacts.Describe(target.Id, identity)}); killed, recorded in the journal");
    try { target.Kill(); } catch (InvalidOperationException) { }
}

static string? Build(string csPath)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c Debug")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string outp = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(outp); return null; }
    foreach (string line in outp.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) continue;
        string path = line[(arrow + 3)..].Trim();
        if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path) && Path.GetFileNameWithoutExtension(path) == Path.GetFileNameWithoutExtension(csPath)) return path;
    }
    return null;
}

// ---- the supervisor's view of a worker ------------------------------------------------------------------------------
sealed class Worker
{
    public Process Process { get; }
    public long SpawnedAt { get; }
    readonly Run _run;
    readonly BlockingCollection<JsonObject> _events = new();
    readonly List<string> _stderr = [];

    Worker(Process process, Run run, long spawnedAt) { Process = process; _run = run; SpawnedAt = spawnedAt; }

    public static Worker Start(string workerDll, Run run)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add("serve");
        long spawnedAt = run.Ms;
        var worker = new Worker(Process.Start(psi)!, run, spawnedAt);
        worker.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (worker._stderr) worker._stderr.Add(e.Data); };
        worker.Process.BeginErrorReadLine();
        new Thread(() =>
        {
            string? line;
            while ((line = worker.Process.StandardOutput.ReadLine()) is not null)
            {
                JsonObject ev;
                try { ev = JsonNode.Parse(line)!.AsObject(); }
                catch (System.Text.Json.JsonException) { ev = new JsonObject { ["ev"] = "stray", ["text"] = line }; }
                worker._events.Add(ev);
            }
            worker._events.Add(new JsonObject { ["ev"] = "eof" });
        }) { IsBackground = true, Name = "probe106-worker-reader" }.Start();
        return worker;
    }

    public void Send(JsonObject command)
    {
        try { Process.StandardInput.WriteLine(command.ToJsonString()); Process.StandardInput.Flush(); }
        catch (IOException) { } // the worker is gone; the next Await reports eof
    }

    // The next event named one of `names`, or an error, debugger-lost or eof event; null when the bound expires.
    // Every event read on the way is passed to `seen`; stray stdout lines are reported (the protocol channel is polluted).
    public JsonObject? Await(string[] names, TimeSpan bound, Action<JsonObject>? seen = null)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan left = bound - waiting.Elapsed;
            if (left <= TimeSpan.Zero || !_events.TryTake(out JsonObject? ev, left)) return null;
            string name = ev["ev"]?.ToString() ?? "";
            if (name == "stray") _run.Say($"worker stdout not protocol: {ev["text"]}");
            seen?.Invoke(ev);
            if (names.Contains(name) || name is "error" or "debugger-lost" or "eof") return ev;
        }
    }

    // Close the session and end the worker by itself; a worker that does not end within its bounds is discarded.
    public bool CloseOrDiscard(Journal? journal)
    {
        Send(new JsonObject { ["cmd"] = "close" });
        bool closed = Await(["closed"], TimeSpan.FromSeconds(30))?["ev"]?.ToString() == "closed";
        Send(new JsonObject { ["cmd"] = "exit" });
        Await(["bye"], TimeSpan.FromSeconds(5));
        if (closed && Process.WaitForExit(10_000)) { ReportStderr(); return true; }
        _run.Say($"worker {Process.Id} did not close and end by itself (closed: {closed})");
        Discard(journal, "the worker did not close and end by itself", null);
        return false;
    }

    // Journal first, then end the worker with the OS. Only the worker: a target it launched is its child but is not
    // killed with it (Process.Kill without entireProcessTree).
    public long Discard(Journal? journal, string reason, JsonObject? lastKnown)
    {
        journal?.Record(new JsonObject { ["act"] = "discard-worker", ["worker"] = Process.Id, ["reason"] = reason, ["target"] = lastKnown });
        var ending = Stopwatch.StartNew();
        try { Process.Kill(); } catch (InvalidOperationException) { }
        bool ended = Process.WaitForExit(10_000);
        journal?.Record(new JsonObject { ["act"] = "discarded", ["worker"] = Process.Id, ["ended"] = ended, ["ms"] = ending.ElapsedMilliseconds });
        ReportStderr();
        return ending.ElapsedMilliseconds;
    }

    void ReportStderr()
    {
        lock (_stderr) foreach (string line in _stderr.Take(5)) _run.Say($"worker stderr: {line}");
    }
}

sealed class Run(string name)
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    public long Ms => _clock.ElapsedMilliseconds;
    public void Say(string text) { Console.WriteLine($"    {text} [{Ms} ms]"); Console.Out.Flush(); }
    public override string ToString() => name;
}

// Append-only, one JSON object per line, flushed to disk before the act it records.
sealed class Journal(string path)
{
    public string Path { get; } = path;
    public void Record(JsonObject entry)
    {
        entry["at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        byte[] bytes = Encoding.UTF8.GetBytes(entry.ToJsonString() + "\n");
        using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}

static class Beat
{
    public static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : "none"; }
        catch (IOException) { return "unreadable"; }
    }
}

// A process seen from outside, without a debugger: its state and start time (a PID alone does not identify a
// process, since PIDs are reused). Linux /proc/<pid>/stat; macOS proc_pidinfo(PROC_PIDTBSDINFO) (struct
// proc_bsdinfo: pbi_status at 4, pbi_start_tvsec at 120, pbi_start_tvusec at 128, size 136; checked against the
// SDK headers on omega, 2026-11-07); Windows the process handle (no state beyond alive or exited).
static class ProcessFacts
{
    public readonly record struct Snapshot(string State, long Start);

    public static Snapshot? Read(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{pid}/stat"); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            string state = fields[0] switch { "Z" => "zombie", "T" or "t" => "stopped", "R" => "running", "S" or "D" => "sleeping", var s => s };
            return new Snapshot(state, long.Parse(fields[19], CultureInfo.InvariantCulture));
        }
        if (OperatingSystem.IsMacOS())
        {
            byte[] info = new byte[136];
            if (proc_pidinfo(pid, 3, 0, info, info.Length) != info.Length) return null;
            string state = BitConverter.ToUInt32(info, 4) switch { 1 => "idle", 2 => "running", 3 => "sleeping", 4 => "stopped", 5 => "zombie", var s => $"status {s}" };
            return new Snapshot(state, (long)BitConverter.ToUInt64(info, 120) * 1_000_000 + (long)BitConverter.ToUInt64(info, 128));
        }
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.HasExited ? null : new Snapshot("alive", p.StartTime.ToUniversalTime().Ticks);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string Describe(int pid, long? identity)
    {
        Snapshot? s = Read(pid);
        if (s is null) return "is gone";
        if (identity is not null && s.Value.Start != identity) return "is gone (its PID now belongs to another process)";
        return $"is {s.Value.State}";
    }

    public static void Kill(int pid)
    {
        try { using Process p = Process.GetProcessById(pid); p.Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
    }

    [DllImport("libproc", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int buffersize);
}
