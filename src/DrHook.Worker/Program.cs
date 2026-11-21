// drhook-worker — the debugger in a process of its own (ADR-016 6.7's design, accepted 2026-11-21).
//
// drhook-mcp starts one worker per session and relays the session's tools to it over this process's stdin and stdout,
// one JSON object per line (DrHook.Engine's WorkerProtocol). The worker runs the session as EngineSteppingSession runs
// it, and is the only DrHook process that loads dbgshim, mscordbi and the DAC, so a debugger that is lost costs this
// process and not drhook-mcp.
//
// Its life:
//   1. ready: it says so, with its PID, its runtime and the protocol's version.
//   2. in session: the requests are run one at a time, in order. Each answer says what the session is afterwards, and
//      whether its debugger is lost. The target's identity is sent as soon as the session has a target, and each stop
//      or release as the session records it. A health request is answered at once, by the thread that reads stdin:
//      the tool running, and whether the session waits for its target or does debugger work (ADR-016 6.8).
//   3. ending: on drhook-mcp's exit request, or when its stdin ends (drhook-mcp is gone), or on SIGTERM, SIGINT or
//      SIGHUP. A session still open is ended as drhook_stop ends it; a target drhook_stop left running is left running
//      (ADR-016 6.2's open rule, unchanged). The worker then exits. If the request it is running does not let the end
//      happen within EndBound, the worker exits anyway.
//   4. holding: after a drhook_stop that left an owned target running, the worker stays, debugging nothing, so that
//      drhook_kill can still end that target through the session (KillLeftRunningTarget). When the target ends, the
//      worker says so and exits.
//
// stdout carries the protocol and nothing else: whatever else would write to it goes to stderr, which drhook-mcp
// passes on to its own.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Worker;
using SkyOmega.DrHook.Worker;

var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
Console.SetOut(Console.Error);
var channel = new WorkerChannel(protocolOut);
var worker = new WorkerLife(channel, new EngineSteppingSession(new ForwardedRecords(channel)));
return worker.Run(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));

namespace SkyOmega.DrHook.Worker
{
    internal sealed class WorkerLife(WorkerChannel channel, EngineSteppingSession session)
    {
        /// <summary>How long the end may wait for the request that is running: 6.8's bound for debugger work.</summary>
        private static readonly TimeSpan EndBound = TimeSpan.FromSeconds(30);

        private readonly BlockingCollection<Action> _requests = new();
        private readonly ManualResetEventSlim _endAsked = new(false);
        private readonly ManualResetEventSlim _ended = new(false);
        private int _endQueued;
        private int _announcedTarget; // the PID the last target event named, 0 before any
        private int _watchingHeldTarget;
        private volatile Running? _running; // the request running, for health answers; null when idle

        private sealed record Running(string Tool, long StartedAt);

        public int Run(TextReader requests)
        {
            session.TargetKnown += (pid, start, owned) =>
            {
                _announcedTarget = pid;
                channel.Send(WorkerProtocol.Target(pid, start, owned));
            };
            session.StopRecorded += (stop, where) => channel.Send(stop is null
                ? WorkerProtocol.State("running", null, null)
                : WorkerProtocol.State(stop.EndsSession ? "ended" : "stopped", stop.Reason.ToString(), where));
            channel.Send(WorkerProtocol.Ready(Environment.ProcessId, RuntimeInformation.FrameworkDescription));
            new Thread(RunRequests) { IsBackground = true, Name = "drhook-worker requests" }.Start();
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
            new Thread(() => ReadRequests(requests)) { IsBackground = true, Name = "drhook-worker stdin" }.Start();

            _endAsked.Wait();
            if (!_ended.Wait(EndBound))
                Console.Error.WriteLine($"drhook-worker: the request running did not let the session end within {EndBound.TotalSeconds:0} s; exiting");
            return 0;
        }

        private void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true; // the worker ends its session first, as at the end of its stdin
            AskEnd($"drhook-worker received {context.Signal}");
        }

        private void ReadRequests(TextReader requests)
        {
            string? line;
            while ((line = requests.ReadLine()) is not null)
            {
                JsonNode? message;
                try { message = JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"drhook-worker: not a message ({ex.Message}): {line}"); continue; }
                if (message?["exit"] is not null) { AskEnd("drhook-mcp asked the worker to exit"); return; }
                if (message?["health"] is not null) { AnswerHealth((long)message["id"]!); continue; }
                if (message?["tool"] is JsonNode tool)
                {
                    long id = (long)message["id"]!;
                    string name = (string)tool!;
                    JsonObject args = message["args"] as JsonObject ?? new JsonObject();
                    Queue(() => Answer(id, name, args));
                }
            }
            AskEnd("drhook-mcp ended: the worker's stdin closed");
        }

        private void RunRequests()
        {
            foreach (Action request in _requests.GetConsumingEnumerable()) request();
        }

        private void Queue(Action request)
        {
            try { _requests.Add(request); }
            catch (InvalidOperationException) { /* the end has run: nothing more is done */ }
        }

        // The phase is the session's own mark (the engine marks where it waits for the target); its length counts from
        // the later of the request's start and the last change of that mark.
        private void AnswerHealth(long id)
        {
            long now = Stopwatch.GetTimestamp();
            if (_running is not { } running)
            {
                channel.Send(WorkerProtocol.HealthAnswer(id, null, WorkerProtocol.Idle, 0, 0));
                return;
            }
            long phaseSince = Math.Max(running.StartedAt, session.WaitingChangedAt);
            channel.Send(WorkerProtocol.HealthAnswer(id, running.Tool,
                session.WaitingForTarget ? WorkerProtocol.WaitingForTarget : WorkerProtocol.DebuggerWork,
                (long)Stopwatch.GetElapsedTime(phaseSince, now).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(running.StartedAt, now).TotalMilliseconds));
        }

        private void Answer(long id, string tool, JsonObject args)
        {
            ToolAnswer answer;
            _running = new Running(tool, Stopwatch.GetTimestamp());
            try { answer = SessionTools.Run(session, tool, args); }
            catch (Exception ex) { answer = new ToolAnswer(OperatorJson.Error($"{tool} failed in the debugger worker: {ex.GetType().Name}: {ex.Message}")); }
            finally { _running = null; }

            string? lost = session.DebuggerLostCause;
            string state = lost is not null ? WorkerProtocol.Lost
                : session.IsActive ? WorkerProtocol.Active
                : session.HoldsLeftRunningTarget ? WorkerProtocol.Holding
                : WorkerProtocol.Ended;
            channel.Send(answer.Result is { } result
                ? WorkerProtocol.Result(id, result, state, lost)
                : WorkerProtocol.ImageResult(id, answer.Caption ?? "", answer.Png, state, lost));
            if (state == WorkerProtocol.Holding && Interlocked.Exchange(ref _watchingHeldTarget, 1) == 0)
                new Thread(WatchHeldTarget) { IsBackground = true, Name = "drhook-worker held target" }.Start();
        }

        // A holding worker exits once the target it holds has ended, by itself or by drhook_kill.
        private void WatchHeldTarget()
        {
            while (session.HoldsLeftRunningTarget) Thread.Sleep(100);
            int pid = _announcedTarget;
            Queue(() =>
            {
                channel.Send(WorkerProtocol.TargetEnded(pid));
                End("the target it held has ended");
            });
            _endAsked.Set();
        }

        private void AskEnd(string reason)
        {
            Queue(() => End(reason));
            _endAsked.Set();
        }

        // The worker's end: a session still open is ended as drhook_stop ends it; a target left running is left running.
        private void End(string reason)
        {
            if (Interlocked.Exchange(ref _endQueued, 1) != 0) return;
            try
            {
                if (session.IsActive)
                    session.StopAsync($"{reason}; the session ends as drhook_stop ends it (ADR-016 6.7)", CancellationToken.None).GetAwaiter().GetResult();
                session.Dispose(); // the publisher's socket; a target left running is not touched
            }
            catch (Exception ex) { Console.Error.WriteLine($"drhook-worker: ending the session failed: {ex.GetType().Name}: {ex.Message}"); }
            _requests.CompleteAdding();
            _ended.Set();
        }
    }
}
