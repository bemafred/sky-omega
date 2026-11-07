using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace SkyOmega.DrHook.Engine.Diagnostics;

/// <summary>
/// Captures and SUMMARIZES live observations from a running .NET process via EventPipe.
///
/// Cross-LLM refinements applied:
///   #1 Signal summarization — collapsed stacks, thread state overview, anomaly flags.
///      Raw EventPipe output overwhelms LLM context. Summaries are diagnosis-ready.
///   #2 Code version anchoring — assembly version captured with every observation.
///      Grounds the observation in a specific code state. Prevents bitemporal desync.
///   #3 Hypothesis field — consumer states expectations BEFORE inspecting.
///      Captures the delta between expectation and reality. Without a prior expectation,
///      an observation has no epistemic value — it's just data.
///
/// EventPipe is the runtime's native cross-platform tracing mechanism.
/// DOTNET_DiagnosticPorts exposes it via IPC — no vsdbg, no proprietary tooling.
///
/// The stacks are the sample profiler's own: each sample carries its thread's managed stack as code addresses. They
/// are named from the method rundown the runtime writes when the session stops, so the session is written to a trace
/// file and resolved by TraceLog, as dotnet-trace does (observed 2026-11-07 on omega: one second of 12 threads, 0.5 MB
/// of trace, 78 ms of rundown, 64 ms to resolve; framework and application frames all named). EventPipe sees managed
/// frames only: a thread outside managed code shows the managed frame it left from, never the native frames below.
/// </summary>
public sealed class StackInspector
{
    // Informational: the runtime's GC and contention events are Informational. At Warning, the level until 2026-11-07,
    // both counts were always zero, so GC_PRESSURE and CONTENTION could never fire.
    private static readonly IReadOnlyList<EventPipeProvider> DefaultProviders =
    [
        new EventPipeProvider(
            "Microsoft-DotNETCore-SampleProfiler",
            System.Diagnostics.Tracing.EventLevel.Informational,
            (long)ClrTraceEventParser.Keywords.None),

        new EventPipeProvider(
            "Microsoft-Windows-DotNETRuntime",
            System.Diagnostics.Tracing.EventLevel.Informational,
            (long)(ClrTraceEventParser.Keywords.Exception |
                   ClrTraceEventParser.Keywords.GC |
                   ClrTraceEventParser.Keywords.Contention))
    ];

    /// <summary>The most trace the sampling phase writes before the capture stops early. Observed 2026-11-07 on omega:
    /// about 530 KiB a second for a process of 12 threads, so about two minutes there, and less for more threads. The
    /// rundown written after the stop is not counted against it: without the rundown no frame has a name.</summary>
    public const long MaxSampleTraceBytes = 64L * 1024 * 1024;

    /// <summary>The most frames named per sampled stack, from the innermost outwards; deeper frames are counted.</summary>
    public const int MaxFramesPerStack = 64;

    public async Task<ObservationSnapshot> CaptureAsync(int pid, int durationMs, string hypothesis, CancellationToken ct)
    {
        // Capture code version for anchoring
        string assemblyVersion = "unknown";
        string processName = "unknown";
        try
        {
            var proc = Process.GetProcessById(pid);
            processName = proc.ProcessName;
            assemblyVersion = proc.MainModule?.FileVersionInfo.FileVersion ?? "unknown";
        }
        catch
        {
            // Process may not be accessible — continue with unknowns
        }

        // The sample profiler samples every managed thread on each tick, whatever it is doing, so a thread's share of
        // all samples measures the number of threads, not activity. What a sample says about its thread is its type:
        // Managed (running managed code) or External (outside it: waiting, blocked, in native code).
        var threadSamples = new Dictionary<int, int>();        // threadId → sample count
        var managedSamples = new Dictionary<int, int>();       // threadId → samples taken in managed code
        var stackTallies = new Dictionary<(int ThreadId, string Key), StackTally>();
        var exceptions = new List<ExceptionEvent>();
        var gcEvents = new List<GcEvent>();
        var contentionCount = 0;

        string tracePath = Path.Combine(Path.GetTempPath(), $"drhook-snapshot-{pid}-{Guid.NewGuid():N}.nettrace");
        string etlxPath = Path.ChangeExtension(tracePath, ".etlx");
        int observedMs;
        bool truncated;
        try
        {
            var client = new DiagnosticsClient(pid);
            var observed = Stopwatch.StartNew();
            using (var session = client.StartEventPipeSession(DefaultProviders, requestRundown: true))
            await using (var file = File.Create(tracePath))
            {
                var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // The copy runs until the stream ends, after the stop and its rundown; it is not cancelled with ct,
                // because a trace cut before its rundown has no names.
                var copy = Task.Run(async () =>
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await session.EventStream.ReadAsync(buffer, CancellationToken.None)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                        written += read;
                        if (written > MaxSampleTraceBytes) limitReached.TrySetResult();
                    }
                }, CancellationToken.None);

                var window = Task.Delay(durationMs, ct);
                truncated = await Task.WhenAny(window, limitReached.Task) == limitReached.Task;
                observedMs = (int)observed.ElapsedMilliseconds;
                session.Stop();
                await copy;
            }
            ct.ThrowIfCancellationRequested();

            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
            using var log = new TraceLog(etlxPath);
            var source = log.Events.GetSource();

            source.Clr.ExceptionStart += e =>
            {
                exceptions.Add(new ExceptionEvent(e.TimeStamp, e.ExceptionType, e.ExceptionMessage, e.ThreadID));
            };

            source.Clr.GCStart += e =>
            {
                gcEvents.Add(new GcEvent(e.TimeStamp, e.Depth, e.Reason.ToString()));
            };

            source.Clr.ContentionStart += _ =>
            {
                contentionCount++;
            };

            new SampleProfilerTraceEventParser(source).ThreadSample += e =>
            {
                var tid = e.ThreadID;
                bool managed = e.Type == ClrThreadSampleType.Managed;
                threadSamples[tid] = threadSamples.GetValueOrDefault(tid, 0) + 1;
                if (managed)
                    managedSamples[tid] = managedSamples.GetValueOrDefault(tid, 0) + 1;

                var frames = new List<string>();
                int deeper = 0;
                for (var frame = e.CallStack(); frame is not null; frame = frame.Caller)
                {
                    if (frames.Count == MaxFramesPerStack) { deeper++; continue; }
                    var address = frame.CodeAddress;
                    frames.Add(address.FullMethodName is { Length: > 0 } name ? name
                        : address.ModuleName is { Length: > 0 } module ? $"[unresolved frame in {module}]"
                        : "[unresolved frame]");
                }
                if (deeper > 0) frames.Add($"[{deeper} deeper frames]");

                var key = (tid, string.Join('\n', frames));
                if (!stackTallies.TryGetValue(key, out var tally))
                    stackTallies[key] = tally = new StackTally(frames);
                tally.SampleCount++;
                if (managed) tally.ManagedSampleCount++;
            };

            source.Process();
        }
        finally
        {
            File.Delete(tracePath);
            File.Delete(etlxPath);
        }

        // ─── Build summary (refinement #1: summarization) ──────────────

        var stacksByThread = stackTallies
            .GroupBy(kv => kv.Key.ThreadId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CollapsedStack>)g
                .Select(kv => new CollapsedStack(kv.Value.Frames, kv.Value.SampleCount, kv.Value.ManagedSampleCount))
                .OrderByDescending(s => s.SampleCount)
                .ToList());
        var totalSamples = threadSamples.Values.Sum();
        var threads = threadSamples
            .Select(kv => new ThreadSummary(kv.Key, kv.Value, managedSamples.GetValueOrDefault(kv.Key, 0),
                stacksByThread.GetValueOrDefault(kv.Key) ?? []))
            .ToList();

        var anomalies = new List<string>();

        if (truncated)
            anomalies.Add($"TRUNCATED: the trace reached {MaxSampleTraceBytes / (1024 * 1024)} MiB after {observedMs} of the {durationMs} ms asked for; the capture stopped early");

        // Anomaly: a thread running managed code in more than 80% of its samples (a tight loop, a spin-wait, or
        // sustained work). A thread that waits is sampled as External and is not one.
        foreach (var hot in threads.Where(t => t.SampleCount > 10 && t.ManagedPercentage > ThreadSummary.RunningPercentage))
            anomalies.Add($"HOTSPOT: Thread {hot.ThreadId} was running managed code in {hot.ManagedSampleCount}/{hot.SampleCount} of its samples ({hot.ManagedPercentage:F0}%), at {hot.At ?? "an unknown frame"} — a tight loop, a spin-wait, or sustained work");

        // Anomaly: high GC pressure
        if (gcEvents.Count > 5)
            anomalies.Add($"GC_PRESSURE: {gcEvents.Count} GC events in {observedMs}ms — possible allocation storm");

        // Anomaly: contention
        if (contentionCount > 0)
            anomalies.Add($"CONTENTION: {contentionCount} lock contention events — possible deadlock risk");

        // Anomaly: unhandled exceptions
        if (exceptions.Count > 0)
            anomalies.Add($"EXCEPTIONS: {exceptions.Count} exception(s) thrown: {string.Join(", ", exceptions.Select(e => e.Type).Distinct())}");

        // Anomaly: no activity at all. Waiting threads are sampled too, so activity is managed samples, not samples.
        if (managedSamples.Count == 0 && exceptions.Count == 0 && gcEvents.Count == 0)
            anomalies.Add(totalSamples == 0
                ? "IDLE: No samples captured — the observation window may be too short"
                : $"IDLE: No thread ran managed code in {totalSamples} samples — every thread is waiting, blocked, or in native code");

        return new ObservationSnapshot(
            Pid: pid,
            ProcessName: processName,
            AssemblyVersion: assemblyVersion,
            CapturedAt: DateTime.UtcNow,
            DurationMs: observedMs,
            Hypothesis: hypothesis,
            ThreadCount: threadSamples.Count,
            TotalSamples: totalSamples,
            ThreadSummary: threads,
            Exceptions: exceptions,
            GcEvents: gcEvents,
            ContentionCount: contentionCount,
            Anomalies: anomalies);
    }

    private sealed class StackTally(IReadOnlyList<string> frames)
    {
        public IReadOnlyList<string> Frames { get; } = frames;
        public int SampleCount;
        public int ManagedSampleCount;
    }
}

public sealed record ExceptionEvent(DateTime Timestamp, string Type, string Message, int ThreadId);
public sealed record GcEvent(DateTime Timestamp, int Generation, string Reason);

/// <summary>One distinct stack a thread was sampled in: its frames from the innermost outwards, how many of the thread's
/// samples found it there, and how many of those found it running managed code.</summary>
public sealed record CollapsedStack(IReadOnlyList<string> Frames, int SampleCount, int ManagedSampleCount);

/// <summary>One thread's samples: how many were taken, how many found it running managed code (the rest found it
/// outside managed code: waiting, blocked, or in native code), and the distinct stacks they found it in, most
/// frequent first.</summary>
public sealed record ThreadSummary(int ThreadId, int SampleCount, int ManagedSampleCount, IReadOnlyList<CollapsedStack> Stacks)
{
    /// <summary>Above this share of managed samples a thread is running managed code; below
    /// <see cref="OutsidePercentage"/> it is outside managed code. Observed 2026-11-07: a spinning thread was sampled
    /// in managed code in 605 of 605 samples, and threads in a pipe read, in Monitor.Wait and in Thread.Sleep in 0 of
    /// 605. Between the two it is partly both.</summary>
    public const double RunningPercentage = 80;
    public const double OutsidePercentage = 20;

    public double ManagedPercentage => 100.0 * ManagedSampleCount / Math.Max(SampleCount, 1);

    /// <summary>The thread's state over the capture, from its samples' types.</summary>
    public string State => ManagedPercentage > RunningPercentage ? "running managed code"
        : ManagedPercentage < OutsidePercentage ? "outside managed code"
        : "partly running managed code";

    /// <summary>The innermost named frame of the thread's most frequent stack: where it ran, or for a thread outside
    /// managed code, the managed frame it left from (Monitor.Wait, Thread.Sleep, a P/Invoke such as Interop+Sys.Read).
    /// Unnamed frames are passed over: a P/Invoke stub generated at run time has no module and no name (observed
    /// 2026-11-07 innermost under SocketAsyncEngine.EventLoop), and the stack keeps it.</summary>
    public string? At => Stacks is [{ Frames: var frames }, ..] ? frames.FirstOrDefault(f => !f.StartsWith('[')) : null;
}

public sealed record ObservationSnapshot(
    int Pid,
    string ProcessName,
    string AssemblyVersion,
    DateTime CapturedAt,
    int DurationMs,
    string Hypothesis,
    int ThreadCount,
    int TotalSamples,
    List<ThreadSummary> ThreadSummary,
    List<ExceptionEvent> Exceptions,
    List<GcEvent> GcEvents,
    int ContentionCount,
    List<string> Anomalies)
{
    /// <summary>The most threads written to JSON, the most distinct stacks written per thread, the share of a thread's
    /// samples a stack other than its most frequent needs to be written, and the most frames written per stack. Bounds
    /// what an LLM reads; the snapshot itself keeps everything. The share leaves out the moments a thread passed
    /// through (in a capture of its own process, the capture's start and stop, one sample each).</summary>
    public const int JsonMaxThreads = 64;
    public const int JsonStacksPerThread = 3;
    public const double JsonMinStackPercentage = 10;
    public const int JsonFramesPerStack = 16;

    // JsonNode writes itself; JsonSerializer.Serialize would need reflection-based metadata, which a trimmed, AOT or
    // file-based host disables.
    public string ToJson()
    {
        // Threads running managed code first, then by samples. Each written thread's most frequent stacks are
        // collapsed across threads: one entry per distinct stack, naming every thread sampled in it, which folds a
        // pool of idle threads into one entry and leaves a lone stuck thread its own.
        var writtenThreads = ThreadSummary
            .OrderByDescending(t => t.ManagedSampleCount)
            .ThenByDescending(t => t.SampleCount)
            .ThenBy(t => t.ThreadId)
            .Take(JsonMaxThreads)
            .ToList();
        var stackIndex = new Dictionary<string, int>();
        var stacks = new List<(IReadOnlyList<string> Frames, int Samples, int Managed, List<int> Threads)>();
        var threadStacks = new Dictionary<int, List<int>>();
        foreach (var thread in writtenThreads)
        {
            var indices = threadStacks[thread.ThreadId] = [];
            foreach (var stack in thread.Stacks
                .Where((stack, rank) => rank == 0 || 100.0 * stack.SampleCount / thread.SampleCount >= JsonMinStackPercentage)
                .Take(JsonStacksPerThread))
            {
                string key = string.Join('\n', stack.Frames);
                if (!stackIndex.TryGetValue(key, out int index))
                {
                    stackIndex[key] = index = stacks.Count;
                    stacks.Add((stack.Frames, 0, 0, []));
                }
                var entry = stacks[index];
                stacks[index] = (entry.Frames, entry.Samples + stack.SampleCount, entry.Managed + stack.ManagedSampleCount, entry.Threads);
                entry.Threads.Add(thread.ThreadId);
                indices.Add(index);
            }
        }

        return new JsonObject
        {
            // ── Context (anchoring) ──
            ["pid"]             = Pid,
            ["processName"]     = ProcessName,
            ["assemblyVersion"] = AssemblyVersion,
            ["capturedAt"]      = CapturedAt.ToString("O"),
            ["durationMs"]      = DurationMs,

            // ── Epistemic (hypothesis) ──
            ["hypothesis"]      = Hypothesis,

            // ── Summary (not raw data) ──
            ["summary"] = new JsonObject
            {
                ["threadCount"]     = ThreadCount,
                ["totalSamples"]    = TotalSamples,
                ["exceptionCount"]  = Exceptions.Count,
                ["gcEventCount"]    = GcEvents.Count,
                ["contentionCount"] = ContentionCount,
            },

            // ── Thread state overview ──
            ["threads"] = new JsonArray(writtenThreads.Select(t => (JsonNode)new JsonObject
            {
                ["threadId"]          = t.ThreadId,
                ["state"]             = t.State,
                ["at"]                = t.At,
                ["samples"]           = t.SampleCount,
                ["managedSamples"]    = t.ManagedSampleCount,
                ["managedPercentage"] = Math.Round(t.ManagedPercentage, 1),
                ["stacks"]            = new JsonArray(threadStacks[t.ThreadId].Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
            }).ToArray()),
            ["threadsOmitted"] = ThreadSummary.Count - writtenThreads.Count,

            // ── Collapsed stacks (innermost frame first; a thread's "stacks" index into this list) ──
            ["stacks"] = new JsonArray(stacks.Select(s => (JsonNode)new JsonObject
            {
                ["samples"]        = s.Samples,
                ["managedSamples"] = s.Managed,
                ["threads"]        = new JsonArray(s.Threads.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                ["frames"]         = new JsonArray(s.Frames.Take(JsonFramesPerStack).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                ["framesOmitted"]  = Math.Max(0, s.Frames.Count - JsonFramesPerStack),
            }).ToArray()),

            // ── Exceptions (all — these are high-signal) ──
            ["exceptions"] = new JsonArray(Exceptions.Select(e => (JsonNode)new JsonObject
            {
                ["timestamp"] = e.Timestamp.ToString("O"),
                ["type"]      = e.Type,
                ["message"]   = e.Message,
                ["threadId"]  = e.ThreadId
            }).ToArray()),

            // ── Anomalies (the diagnosis-ready signal) ──
            ["anomalies"] = new JsonArray(Anomalies.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),

            // ── Delta prompt ──
            ["deltaPrompt"] = Anomalies.Count > 0
                ? $"Anomalies detected. Compare with hypothesis: \"{Hypothesis}\". What is the delta?"
                : $"No anomalies. Does this confirm or challenge your hypothesis: \"{Hypothesis}\"?"

        }.ToJsonString(Indented);
    }

    // Relaxed escaping: frame names are full of + < > & (nested types, generics, byrefs), which the default encoder
    // writes as \u002B and the like. The JSON goes to an MCP text channel, never into HTML.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
