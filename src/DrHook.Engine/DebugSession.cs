// Attach/detach lifecycle, ported from PoC probes 05/06 as engine code. Composes the
// validated pieces: DbgShim (native attach flow) -> ICorDebug RCW (consume, source-gen COM)
// -> ManagedCallbackHost (receive, [UnmanagedCallersOnly] vtable) -> DebugActiveProcess ->
// ICorDebugController (Continue/Detach). The StrategyBasedComWrappers is held as a substrate
// singleton (finding 13). Phase 1 validates attach + callback delivery + clean teardown;
// the continue-loop and stepping are Phase 2 (ADR-006).

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Engine;

/// <summary>An attached managed-debugging session over a target .NET process. Dispose detaches
/// and releases all native resources.</summary>
public sealed class DebugSession : IDisposable
{
    /// <summary>Maximum recursion depth for object/array inspection (ENG-STK-1).
    /// Mercury-aligned with <c>SparqlParser.DefaultMaxDepth</c>. At ~2 KB per level across
    /// mutually-recursive <c>FieldEnumerator</c> / <c>ArrayInspector</c> / <c>Variables.GetChildren</c>,
    /// 10 levels = ~20 KB stack — well under macOS-secondary 512 KB and Windows 1 MB defaults.
    /// Callers requesting deeper are clamped to this value. (Future: emit <c>EngineAnomaly</c>
    /// when clamping — pending the anomaly-capture infrastructure landing in Phase 1.)
    /// Inheritance-chain walking (<c>GetBase</c> within one level) is iterative and does NOT
    /// count against this budget. See finding 55.</summary>
    public const int MaxInspectionDepth = 10;

    private static readonly ComWrappers Wrappers = new StrategyBasedComWrappers();

    private readonly DbgShim _dbgShim;
    private readonly CallbackPump _pump;
    private readonly ManagedCallbackHost _callback;
    private readonly ICorDebug _cordbg;
    private readonly ICorDebugController _controller;
    private readonly IDebugEventSink _sink;
    private nint _pUnknown;
    private nint _pProcess;
    private int _detached;  // 0 = attached, 1 = detached; atomic via Interlocked.Exchange (ENG-DS-1)
    private int _disposed;  // 0 = live, 1 = disposed; atomic via Interlocked.Exchange (ENG-DS-1)

    // Active breakpoints: owned (module, function, breakpoint) ICorDebug pointers kept alive so
    // the breakpoint stays bound, alongside a public BreakpointInfo carrying the assigned id and
    // descriptor for listing/removal. Released on Dispose or via Remove/ClearBreakpoints.
    private sealed record BreakpointEntry(BreakpointInfo Info, nint Module, nint Function, nint Breakpoint);
    private readonly List<BreakpointEntry> _breakpoints = new();
    private int _nextBreakpointId;

    // Armed exception filters: consulted by WaitForStop on Exception stops. No native resources to
    // release; purely consumer-side state.
    private readonly List<ExceptionFilterInfo> _exceptionFilters = new();
    private int _nextExceptionFilterId;

    // Per-module Portable PDB readers, opened on demand for source mapping; disposed on Dispose.
    private readonly Dictionary<string, SymbolReader?> _symbols = new(StringComparer.Ordinal);

    private DebugSession(int processId, DbgShim dbgShim, CallbackPump pump, ManagedCallbackHost callback,
                         ICorDebug cordbg, ICorDebugController controller, IDebugEventSink sink,
                         nint pUnknown, nint pProcess)
    {
        ProcessId = processId;
        _dbgShim = dbgShim;
        _pump = pump;
        _callback = callback;
        _cordbg = cordbg;
        _controller = controller;
        _sink = sink;
        _pUnknown = pUnknown;
        _pProcess = pProcess;
    }

    /// <summary>OS process id of the attached target.</summary>
    public int ProcessId { get; }

    /// <summary>Attach the native ICorDebug engine to a running .NET process and register the
    /// managed callback. On macOS/ARM64 this needs no debug entitlement (finding 13).</summary>
    /// <exception cref="DebugEngineException">An ICorDebug step failed.</exception>
    public static DebugSession Attach(int processId, IDebugEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        DbgShim dbgShim = DbgShim.Load();
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), "CreateDebuggingInterfaceFromVersion");
            return FromCordbg(dbgShim, sink, processId, pUnknown);
        }
        catch
        {
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            throw;
        }
    }

    /// <summary>Launch a .NET process under debug control via dbgshim's RegisterForRuntimeStartup
    /// flow: spawn suspended → register a startup callback → resume → await the callback. On return,
    /// the debugger is attached BEFORE managed code has run, so a <c>Debugger.Break()</c> at the top
    /// of <c>Main</c> surfaces as the first stop (finding 42). Backs <c>drhook_step_run</c> in the
    /// MCP rewrite. <paramref name="program"/> is typically the <c>dotnet</c> host with the target
    /// DLL as an argument.</summary>
    /// <exception cref="DebugEngineException">The launch failed or the runtime didn't initialize.</exception>
    public static DebugSession Launch(string program, IReadOnlyList<string> args, string? workingDirectory, IDebugEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(sink);
        string commandLine = BuildCommandLine(program, args);
        DbgShim dbgShim = DbgShim.Load();
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(
                dbgShim.LaunchWithDebugger(commandLine, workingDirectory, TimeSpan.FromSeconds(30), out uint pid, out pUnknown),
                "DbgShim.LaunchWithDebugger");
            return FromCordbg(dbgShim, sink, (int)pid, pUnknown);
        }
        catch
        {
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            throw;
        }
    }

    /// <summary>Shared post-cordbg setup: cast to <see cref="ICorDebug"/>, build the pump and
    /// callback vtable, register the handler, <c>DebugActiveProcess</c>, start the continue-loop.
    /// Same shape used by Attach and Launch.</summary>
    private static DebugSession FromCordbg(DbgShim dbgShim, IDebugEventSink sink, int processId, nint pUnknown)
    {
        CallbackPump? pump = null;
        ManagedCallbackHost? callback = null;
        try
        {
            var cordbg = (ICorDebug)Wrappers.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
            ThrowIfFailed(cordbg.Initialize(), "ICorDebug.Initialize");

            pump = new CallbackPump(sink);
            callback = new ManagedCallbackHost(pump);
            ThrowIfFailed(cordbg.SetManagedHandler(callback.NativePointer), "ICorDebug.SetManagedHandler");

            ThrowIfFailed(cordbg.DebugActiveProcess((uint)processId, 0, out nint pProcess), "ICorDebug.DebugActiveProcess");
            var controller = (ICorDebugController)Wrappers.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);

            // Drive the continue-loop now that the process controller exists. The resume handler
            // arms a stepper on the stopped thread for step resumes (no-op for a plain continue),
            // then Continues. The pause handler synchronizes the running debuggee for an AsyncBreak
            // (DebugSession.Pause). Both are routed through the worker so the controller has a single
            // caller. Callbacks enqueued since SetManagedHandler (if any) drain immediately.
            pump.Start(
                (kind, thread) =>
                {
                    Stepping.Arm(thread, kind);
                    return controller.Continue(0);
                },
                () => controller.Stop(0));

            return new DebugSession(processId, dbgShim, pump, callback, cordbg, controller, sink, pUnknown, pProcess);
        }
        catch
        {
            pump?.Dispose();
            callback?.Dispose();
            throw;
        }
    }

    /// <summary>Quote a token if it contains spaces/quotes; dbgshim's command-line parser splits on
    /// whitespace with shell-style quoting.</summary>
    private static string BuildCommandLine(string program, IReadOnlyList<string> args)
    {
        System.Text.StringBuilder sb = new();
        sb.Append(Quote(program));
        foreach (string arg in args) { sb.Append(' '); sb.Append(Quote(arg)); }
        return sb.ToString();
    }

    private static string Quote(string s)
        => s.Length == 0 || s.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;

    /// <summary>Block until the debuggee next stops (breakpoint, step complete, exception, or
    /// <c>Debugger.Break</c>), up to <paramref name="timeout"/>. Returns null on timeout (still
    /// running); a <see cref="StopReason.ProcessExited"/> result means the session is over.
    /// While stopped the debuggee is frozen — inspect, then <see cref="Resume"/>.
    ///
    /// When at least one exception filter has been armed via <see cref="ArmExceptionFilter"/>,
    /// non-matching <see cref="StopReason.Exception"/> stops are auto-resumed inside the wait
    /// (the deadline budget is preserved across resumes). With no filters armed every stop surfaces
    /// — the legacy behavior probes 26/27 rely on.</summary>
    public StopInfo? WaitForStop(TimeSpan timeout)
    {
        if (_exceptionFilters.Count == 0) return _pump.WaitForStop(timeout);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Exception) return stop;
            if (ExceptionMatchesAnyFilter(stop.ExceptionKind)) return stop;
            _pump.Resume();
        }
    }

    private bool ExceptionMatchesAnyFilter(ExceptionStopKind actualPhase)
    {
        // Walk the exception's full type chain (across modules via ICorDebugType.GetBase, probe 37)
        // so a filter on a base class (e.g. "System.Exception") matches any subclass — finding 47.
        IReadOnlyList<string> chain = Interop.ExceptionInspector.CurrentExceptionTypeChain(_pump.StopThread);
        if (chain.Count == 0) return false;
        foreach (ExceptionFilterInfo f in _exceptionFilters)
            if (f.MatchesChain(chain, actualPhase)) return true;
        return false;
    }

    /// <summary>Resume a stopped debuggee so it runs to the next stop or exit.</summary>
    public void Resume() => _pump.Resume();

    /// <summary>Interrupt a RUNNING debuggee (AsyncBreak). Synchronizes the process via
    /// <c>ICorDebugController.Stop</c>; the next <see cref="WaitForStop"/> returns a
    /// <see cref="StopReason.Pause"/> stop. Resume via <see cref="Resume"/> like any other stop.
    /// If a callback-driven stop is already in flight, that stop surfaces first and the requested
    /// pause queues behind it (will fire after the user resumes the prior stop).</summary>
    public void Pause() => _pump.RequestPause();

    /// <summary>Run until a breakpoint hit where <paramref name="condition"/> holds (or a
    /// non-breakpoint stop, or timeout). At each breakpoint hit the condition is evaluated against
    /// a snapshot of the frame's locals/arguments; if false, the debuggee is resumed and the wait
    /// continues. This is the conditional-breakpoint mechanism: the breakpoint marks WHERE, the
    /// predicate decides WHETHER. The predicate is a plain delegate — the C#-expression front end
    /// (Roslyn) lives above the engine. Returns null on timeout.</summary>
    public StopInfo? WaitForConditionalStop(Func<IEvalContext, bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Breakpoint) return stop; // non-breakpoint stops surface as-is
            IEvalContext context = new EvalContext(GetLocals(), GetArguments());
            if (condition(context)) return stop;
            _pump.Resume(); // condition false — keep running to the next hit (within the deadline)
        }
    }

    /// <summary>Drive a breakpoint with a <see cref="BreakpointPolicy"/> — the unified surface from
    /// findings 33/35. At each breakpoint hit: increment a hit counter, evaluate the gates
    /// (<see cref="BreakpointPolicy.HitCount"/>, then <see cref="BreakpointPolicy.Condition"/>), run
    /// the <see cref="BreakpointPolicy.LogMessage"/> action if present (rendered + emitted as a
    /// <see cref="LogRecord"/> via <see cref="IDebugEventSink.OnLog"/>), and surface or auto-resume per
    /// <see cref="BreakpointPolicy.Suspend"/>. Conditional breakpoint, logpoint, hit-count gate, and
    /// log-and-break all fall out as configurations of the same policy. A condition that THROWS is a
    /// FAULT — never silently false — and surfaces as <see cref="StopReason.ConditionError"/> with a
    /// fault <see cref="LogRecord"/>. Non-breakpoint stops surface as-is. Returns null on timeout (a
    /// pure logpoint, by design, never returns a non-null value — call with a bounded timeout to
    /// drain logs).</summary>
    public StopInfo? WaitForPolicyStop(BreakpointPolicy policy, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        int hitCount = 0;
        while (true)
        {
            // Deadline-based: total wall-clock budget, not per-stop. A pure logpoint (Suspend.None,
            // no condition that ever returns) only terminates via this deadline; without it a
            // fast-hitting breakpoint resets the per-call timeout each iteration and the loop never exits.
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Breakpoint) return stop;

            switch (EvaluatePolicy(policy, ref hitCount))
            {
                case PolicyOutcome.Resume: _pump.Resume(); continue;
                case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                default: return stop; // Surface
            }
        }
    }

    /// <summary>Drive an exception "breakpoint" with a <see cref="BreakpointPolicy"/> — the
    /// exception-location surface from findings 33/35. Exception stops whose type does not match
    /// <paramref name="exceptionTypeName"/> are auto-resumed; matching stops run the policy
    /// (gates, log action, suspend decision), reusing the SAME policy evaluation as
    /// <see cref="WaitForPolicyStop"/>. A condition like <c>"ex.Code == 42"</c> uses the in-flight
    /// exception object (resolve via <see cref="TryEvalCurrentExceptionMember"/>) — the walker
    /// special-cases the <c>ex</c> operand. Returns null on timeout; non-exception stops surface
    /// as-is. The hit counter only counts matching exceptions, not stray first-chance noise.</summary>
    public StopInfo? WaitForExceptionPolicyStop(string exceptionTypeName, BreakpointPolicy policy, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(exceptionTypeName);
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        int hitCount = 0;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Exception) return stop;

            // Type filter — non-matching exceptions auto-resume without polluting the hit counter.
            if (GetCurrentExceptionTypeName() != exceptionTypeName) { _pump.Resume(); continue; }

            switch (EvaluatePolicy(policy, ref hitCount))
            {
                case PolicyOutcome.Resume: _pump.Resume(); continue;
                case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                default: return stop;
            }
        }
    }

    private enum PolicyOutcome { Resume, Surface, ConditionFault }

    /// <summary>Run a stop's policy: gates (hit count + condition) → action (log) → suspend decision.
    /// Shared by <see cref="WaitForPolicyStop"/> and <see cref="WaitForExceptionPolicyStop"/> so the
    /// fault path and best-effort logging behave identically across location kinds.</summary>
    private PolicyOutcome EvaluatePolicy(BreakpointPolicy policy, ref int hitCount)
    {
        hitCount++;
        IEvalContext context = new EvalContext(GetLocals(), GetArguments());

        if (policy.HitCount is { } gate && !gate.Admits(hitCount)) return PolicyOutcome.Resume;

        if (policy.Condition is { } condition)
        {
            bool holds;
            try { holds = condition(context); }
            catch (Exception ex)
            {
                _sink.OnLog(new LogRecord(DateTimeOffset.UtcNow, $"condition fault: {ex.Message}", IsFault: true));
                return PolicyOutcome.ConditionFault;
            }
            if (!holds) return PolicyOutcome.Resume;
        }

        if (policy.LogMessage is { } render)
        {
            string message;
            try { message = render(context); }
            catch (Exception ex) { message = $"<log fault: {ex.Message}>"; }
            _sink.OnLog(new LogRecord(DateTimeOffset.UtcNow, message));
        }

        return policy.Suspend == SuspendPolicy.None ? PolicyOutcome.Resume : PolicyOutcome.Surface;
    }

    /// <summary>Step into calls from the current stop. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepInto() => _pump.StepInto();

    /// <summary>Step over calls from the current stop. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepOver() => _pump.StepOver();

    /// <summary>Step out of the current frame. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepOut() => _pump.StepOut();

    /// <summary>Names of the modules loaded in the target (process → app domains → assemblies →
    /// modules). Valid only while the debuggee is stopped (after <see cref="WaitForStop"/>) —
    /// ICorDebug enumeration requires the process to be synchronized.</summary>
    public IReadOnlyList<string> EnumerateModules() => RuntimeNavigation.ModuleNames(_pProcess);

    /// <summary>The managed call stack at the current stop, top frame first, as
    /// "Type.Method @ file:line" when a Portable PDB is available (else "Type.Method";
    /// "[external]" for native/internal frames). Valid only while the debuggee is stopped
    /// (after <see cref="WaitForStop"/>).</summary>
    public IReadOnlyList<string> GetStackFrames()
    {
        List<string> result = new();
        foreach (Interop.FrameInfo frame in Frames.WalkManagedFrames(_pump.StopThread))
        {
            SourceLocation? loc = frame.IlOffset >= 0 && frame.ModulePath.Length > 0
                ? SymbolsFor(frame.ModulePath)?.TryGetLine(frame.Token, frame.IlOffset)
                : null;
            result.Add(loc is { } l ? $"{frame.Method} @ {Path.GetFileName(l.File)}:{l.Line}" : frame.Method);
        }
        return result;
    }

    /// <summary>Cached <see cref="SymbolReader"/> for a module (opened once; null if no PDB).</summary>
    private SymbolReader? SymbolsFor(string modulePath)
    {
        if (!_symbols.TryGetValue(modulePath, out SymbolReader? reader))
        {
            reader = SymbolReader.TryOpen(modulePath);
            _symbols[modulePath] = reader;
        }
        return reader;
    }

    /// <summary>Argument values of the active (top) frame at the current stop. Arg 0 is
    /// <c>this</c> for an instance method; <see cref="ArgumentValue.RawValue"/> holds the
    /// primitive bits for generic values, null for object references. When <paramref name="depth"/>
    /// &gt; 0, object args have their <see cref="ArgumentValue.Fields"/> populated by walking
    /// instance fields up the type chain (finding 48); recursive into object-typed fields when
    /// depth &gt; 1. Valid only while stopped.</summary>
    public IReadOnlyList<ArgumentValue> GetArguments(int depth = 0)
    {
        if (depth > MaxInspectionDepth)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.DepthClamped, "mcp-request", "GetArguments",
                Observed: $"depth={depth} requested",
                Expected: $"depth <= {MaxInspectionDepth}",
                Context: new Dictionary<string, string> { ["requested"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture), ["clamped"] = MaxInspectionDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            depth = MaxInspectionDepth;
        }
        return Variables.ReadActiveFrameArguments(_pump.StopThread, 16, depth);
    }

    /// <summary>Named local variables of the active (top) frame at the current stop — PDB names
    /// (via the module's Portable PDB) paired with values read from the frame. Empty if no PDB.
    /// A local not yet in scope/assigned at the current line surfaces with a null
    /// <see cref="LocalValue.RawValue"/>. When <paramref name="depth"/> &gt; 0, object locals have
    /// their <see cref="LocalValue.Fields"/> populated by walking instance fields up the type
    /// chain (finding 48); recursive into object-typed fields when depth &gt; 1. Valid only while
    /// stopped.</summary>
    public IReadOnlyList<LocalValue> GetLocals(int depth = 0)
    {
        if (depth > MaxInspectionDepth)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.DepthClamped, "mcp-request", "GetLocals",
                Observed: $"depth={depth} requested",
                Expected: $"depth <= {MaxInspectionDepth}",
                Context: new Dictionary<string, string> { ["requested"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture), ["clamped"] = MaxInspectionDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            depth = MaxInspectionDepth;
        }
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return Array.Empty<LocalValue>();

        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return Array.Empty<LocalValue>();

        IReadOnlyList<LocalName> names = SymbolsFor(top.ModulePath)?.GetLocalNames(top.Token) ?? Array.Empty<LocalName>();
        return Variables.ReadActiveFrameLocals(_pump.StopThread, names, depth);
    }

    /// <summary>The fully-qualified type name of the exception in flight at the current stop (e.g.
    /// "System.InvalidOperationException"), or null if none. Meaningful at a
    /// <see cref="StopReason.Exception"/> stop; reads <c>ICorDebugThread.GetCurrentException</c> on
    /// the stop thread. Valid only while stopped.</summary>
    public string? GetCurrentExceptionTypeName() => ExceptionInspector.CurrentExceptionTypeName(_pump.StopThread);

    /// <summary>EXPERIMENT (func-eval): evaluate a static, parameterless method in the debuggee at
    /// the current stop and return its value. Creates an eval on the stopped thread, calls the
    /// function, resumes to run it, and waits up to <paramref name="timeout"/> for the
    /// EvalComplete stop. A timeout is the func-eval-deadlock signal. Valid only while stopped.</summary>
    public EvalStatus TryEvalStaticCall(string moduleNameSubstring, string typeName, string methodName,
                                        TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return EvalStatus.SetupFailed;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return EvalStatus.SetupFailed;

            nint function = Eval.GetFunction(pModule, token);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallStaticNoArgs(eval, function)) return EvalStatus.SetupFailed;

                    _pump.Resume(); // worker Continues → the eval runs managed code in the debuggee
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) return EvalStatus.TimedOut; // func-eval deadlock (the netcoredbg failure)
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(pModule); }
    }

    /// <summary>EXPERIMENT (func-eval breadth): evaluate a static, single-<c>int</c>-argument method
    /// in the debuggee at the current stop. The argument is built as an eval value
    /// (<c>CreateValue</c> + <c>SetValue</c>). On a timeout the eval is aborted
    /// (<c>ICorDebugEval.Abort</c>) before returning. Valid only while stopped.</summary>
    public EvalStatus TryEvalStaticCallInt(string moduleNameSubstring, string typeName, string methodName,
                                           int argument, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return EvalStatus.SetupFailed;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return EvalStatus.SetupFailed;

            nint function = Eval.GetFunction(pModule, token);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;

                nint arg = 0;
                try
                {
                    arg = Eval.CreateInt32(eval, argument);
                    if (arg == 0) return EvalStatus.SetupFailed;
                    if (!Eval.CallWithOneArg(eval, function, arg)) return EvalStatus.SetupFailed;

                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; } // hung eval — abort it
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally
                {
                    if (arg != 0) RuntimeNavigation.Release(arg);
                    RuntimeNavigation.Release(eval);
                }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(pModule); }
    }

    /// <summary>EXPERIMENT (func-eval breadth): call an instance method/property
    /// <paramref name="declaringTypeName"/>.<paramref name="methodName"/> (resolved in the module
    /// matching <paramref name="declaringModuleSubstring"/>) on the local named
    /// <paramref name="thisLocalName"/> as <c>this</c>. E.g. <c>s.Length</c> = String.get_Length on
    /// the local <c>s</c>. Valid only while stopped.</summary>
    public EvalStatus TryEvalInstanceCall(string thisLocalName, string declaringModuleSubstring,
                                          string declaringTypeName, string methodName,
                                          TimeSpan timeout, out ArgumentValue result)
    {
        result = default;

        // 1. Resolve `this`: find the named local's slot (top frame's PDB) and read its value.
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return EvalStatus.SetupFailed;
        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return EvalStatus.SetupFailed;

        SymbolReader? symbols = SymbolsFor(top.ModulePath);
        if (symbols is null) return EvalStatus.SetupFailed;
        int slot = -1;
        foreach (LocalName local in symbols.GetLocalNames(top.Token))
            if (local.Name == thisLocalName) { slot = local.Slot; break; }
        if (slot < 0) return EvalStatus.SetupFailed;

        nint thisValue = Variables.GetActiveFrameLocalValue(_pump.StopThread, slot);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            // 2. Resolve the method on its declaring module (may differ from the frame's module).
            nint declModule = RuntimeNavigation.FindModule(_pProcess, declaringModuleSubstring);
            if (declModule == 0) return EvalStatus.SetupFailed;
            try
            {
                uint token = MetadataResolver.ResolveMethodToken(declModule, declaringTypeName, methodName);
                if (token == 0) return EvalStatus.SetupFailed;
                nint function = Eval.GetFunction(declModule, token);
                if (function == 0) return EvalStatus.SetupFailed;
                try
                {
                    nint eval = Eval.CreateEval(_pump.StopThread);
                    if (eval == 0) return EvalStatus.SetupFailed;
                    try
                    {
                        if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed; // args[0] = this
                        _pump.Resume();
                        StopInfo? stop = _pump.WaitForStop(timeout);
                        if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                        if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                        if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                        result = Eval.GetResultValue(eval);
                        return EvalStatus.Completed;
                    }
                    finally { RuntimeNavigation.Release(eval); }
                }
                finally { RuntimeNavigation.Release(function); }
            }
            finally { RuntimeNavigation.Release(declModule); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>EXPERIMENT (general member resolution): call the property getter
    /// <c>thisLocal.member</c> by resolving <c>get_&lt;member&gt;</c> on the local's RUNTIME type —
    /// no hardcoded declaring type/module — and func-evaluating it. Works for plain reference
    /// objects; strings/non-object kinds return SetupFailed (the ICorDebugType path is a follow-on).
    /// Valid only while stopped.</summary>
    public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        int slot = ResolveLocalSlot(thisLocalName);
        if (slot < 0) return EvalStatus.SetupFailed;

        nint thisValue = Variables.GetActiveFrameLocalValue(_pump.StopThread, slot);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            nint function = MemberResolver.ResolveGetter(thisValue, memberName);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed;
                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>EXPERIMENT (func-eval at an exception stop): at a <see cref="StopReason.Exception"/>
    /// stop, call the property getter <c>&lt;member&gt;</c> on the in-flight exception object — its
    /// value comes from <c>ICorDebugThread.GetCurrentException</c>, not a local — by resolving
    /// <c>get_&lt;member&gt;</c> on the exception's RUNTIME type and func-evaluating it. This composes
    /// the exception stop (probe 26) with general member resolution (probe 24); the runtime preserves
    /// the exception across the eval (cordebug.idl). Powers conditional exception breakpoints
    /// (<c>ex.Code == 42</c>). Valid only at an exception stop.</summary>
    public EvalStatus TryEvalCurrentExceptionMember(string memberName, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint thisValue = Interop.ExceptionInspector.CurrentExceptionValue(_pump.StopThread);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            nint function = MemberResolver.ResolveGetter(thisValue, memberName);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed;
                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>PDB slot of the named local in the active (top) frame, or -1 if not found.</summary>
    private int ResolveLocalSlot(string localName)
    {
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return -1;

        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return -1;

        SymbolReader? symbols = SymbolsFor(top.ModulePath);
        if (symbols is null) return -1;
        foreach (LocalName local in symbols.GetLocalNames(top.Token))
            if (local.Name == localName) return local.Slot;
        return -1;
    }

    /// <summary>Resolve <paramref name="typeName"/>.<paramref name="methodName"/> in the module
    /// whose name contains <paramref name="moduleNameSubstring"/> to an <c>mdMethodDef</c> token
    /// (0 if not found). Valid only while the debuggee is stopped. The token feeds breakpoint
    /// creation (<c>GetFunctionFromToken</c>).</summary>
    public uint ResolveMethodToken(string moduleNameSubstring, string typeName, string methodName)
        => RuntimeNavigation.ResolveMethodToken(_pProcess, moduleNameSubstring, typeName, methodName);

    /// <summary>Set an active breakpoint at the entry of
    /// <paramref name="typeName"/>.<paramref name="methodName"/> in the module whose name
    /// contains <paramref name="moduleNameSubstring"/>. Returns the new breakpoint's id (positive)
    /// on success, <c>0</c> if the method can't be resolved or the breakpoint can't be created.
    /// Pass the id to <see cref="RemoveBreakpoint"/>; <see cref="ListBreakpoints"/> returns it
    /// alongside the <see cref="FunctionBreakpointInfo"/> descriptor. Valid only while the debuggee
    /// is stopped; a hit later surfaces as <see cref="StopReason.Breakpoint"/> from
    /// <see cref="WaitForStop"/>.</summary>
    public int SetBreakpoint(string moduleNameSubstring, string typeName, string methodName)
    {
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return 0;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return 0;
            if (!Breakpoints.TryCreate(pModule, token, out nint function, out nint breakpoint)) return 0;

            int id = ++_nextBreakpointId;
            _breakpoints.Add(new BreakpointEntry(
                new FunctionBreakpointInfo(id, moduleNameSubstring, typeName, methodName),
                pModule, function, breakpoint));
            pModule = 0; // ownership moved into _breakpoints — don't release below
            return id;
        }
        finally { if (pModule != 0) RuntimeNavigation.Release(pModule); }
    }

    /// <summary>Set an active breakpoint at a source <paramref name="line"/> in a document whose
    /// name contains <paramref name="fileHint"/>, within the module matching
    /// <paramref name="moduleNameSubstring"/>. Binds via the module's Portable PDB to the nearest
    /// sequence point at or after the line. Returns the new breakpoint's id (positive) on success,
    /// <c>0</c> if no PDB, no matching line, or the breakpoint can't be created. Pass the id to
    /// <see cref="RemoveBreakpoint"/>; <see cref="ListBreakpoints"/> returns it alongside the
    /// <see cref="LineBreakpointInfo"/> descriptor. Valid only while stopped; a hit surfaces as
    /// <see cref="StopReason.Breakpoint"/>.</summary>
    public int SetBreakpointAtLine(string moduleNameSubstring, string fileHint, int line)
    {
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return 0;
        try
        {
            SymbolReader? symbols = SymbolsFor(RuntimeNavigation.ModuleName(pModule));
            if (symbols is null || !symbols.TryFindLine(fileHint, line, out int token, out int ilOffset))
                return 0;
            if (!Breakpoints.TryCreateAtOffset(pModule, (uint)token, (uint)ilOffset, out nint function, out nint breakpoint))
                return 0;

            int id = ++_nextBreakpointId;
            _breakpoints.Add(new BreakpointEntry(
                new LineBreakpointInfo(id, moduleNameSubstring, fileHint, line),
                pModule, function, breakpoint));
            pModule = 0; // ownership moved into _breakpoints
            return id;
        }
        finally { if (pModule != 0) RuntimeNavigation.Release(pModule); }
    }

    /// <summary>The active breakpoints in registration order — id + descriptor (a
    /// <see cref="LineBreakpointInfo"/> or <see cref="FunctionBreakpointInfo"/>). The MCP list /
    /// remove-by-natural-key flows pattern-match on the concrete subtype to recover file/line or
    /// type/method.</summary>
    public IReadOnlyList<BreakpointInfo> ListBreakpoints()
    {
        BreakpointInfo[] result = new BreakpointInfo[_breakpoints.Count];
        for (int i = 0; i < _breakpoints.Count; i++) result[i] = _breakpoints[i].Info;
        return result;
    }

    /// <summary>Deactivate (via <c>ICorDebugBreakpoint.Activate(FALSE)</c>) and release the
    /// breakpoint with <paramref name="id"/>. Returns <c>true</c> if a matching entry was found;
    /// <c>false</c> otherwise. Valid only while stopped.</summary>
    public bool RemoveBreakpoint(int id)
    {
        for (int i = 0; i < _breakpoints.Count; i++)
        {
            if (_breakpoints[i].Info.Id == id)
            {
                BreakpointEntry e = _breakpoints[i];
                Breakpoints.Deactivate(e.Breakpoint);
                RuntimeNavigation.Release(e.Breakpoint);
                RuntimeNavigation.Release(e.Function);
                RuntimeNavigation.Release(e.Module);
                _breakpoints.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Deactivate and release ALL active breakpoints; returns how many were cleared. Valid
    /// only while stopped. <see cref="Dispose"/> releases the refs separately (after the runtime
    /// has been terminated, so deactivation is moot).</summary>
    public int ClearBreakpoints()
    {
        int count = _breakpoints.Count;
        foreach (BreakpointEntry e in _breakpoints)
        {
            Breakpoints.Deactivate(e.Breakpoint);
            RuntimeNavigation.Release(e.Breakpoint);
            RuntimeNavigation.Release(e.Function);
            RuntimeNavigation.Release(e.Module);
        }
        _breakpoints.Clear();
        return count;
    }

    /// <summary>Arm a persistent exception filter. Once armed, <see cref="WaitForStop"/> only
    /// surfaces matching exception stops; non-matching ones are auto-resumed. <paramref name="typeName"/>
    /// is an exact match (or <see cref="ExceptionFilterInfo.AnyType"/> <c>"*"</c> for any type;
    /// subclass walking is a future refinement). <paramref name="phaseFilter"/> defaults to
    /// <see cref="ExceptionStopKind.None"/> meaning "any phase". Returns a positive id; pass to
    /// <see cref="RemoveExceptionFilter"/>.</summary>
    public int ArmExceptionFilter(string typeName, ExceptionStopKind phaseFilter = ExceptionStopKind.None)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        int id = ++_nextExceptionFilterId;
        _exceptionFilters.Add(new ExceptionFilterInfo(id, typeName, phaseFilter));
        return id;
    }

    /// <summary>The armed exception filters in registration order. With at least one armed,
    /// <see cref="WaitForStop"/> auto-resumes non-matching exception stops.</summary>
    public IReadOnlyList<ExceptionFilterInfo> ListExceptionFilters()
    {
        ExceptionFilterInfo[] result = new ExceptionFilterInfo[_exceptionFilters.Count];
        for (int i = 0; i < _exceptionFilters.Count; i++) result[i] = _exceptionFilters[i];
        return result;
    }

    /// <summary>Remove the exception filter with <paramref name="id"/>. Returns <c>true</c> if a
    /// matching entry was found.</summary>
    public bool RemoveExceptionFilter(int id)
    {
        for (int i = 0; i < _exceptionFilters.Count; i++)
        {
            if (_exceptionFilters[i].Id == id)
            {
                _exceptionFilters.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Remove all armed exception filters; returns how many were cleared. After this,
    /// <see cref="WaitForStop"/> reverts to the no-filter behavior (every exception stop surfaces).</summary>
    public int ClearExceptionFilters()
    {
        int count = _exceptionFilters.Count;
        _exceptionFilters.Clear();
        return count;
    }

    /// <summary>Detach the debugger; the target resumes running without it. Idempotent.</summary>
    public void Detach()
    {
        // Atomic idempotence gate (ENG-DS-1) — concurrent Detach calls don't double-invoke
        // ICorDebugController.Detach (whose behavior on already-detached is undefined per docs).
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;
        int hr = _controller.Detach();
        if (hr < 0)
        {
            // EA capture (UnexpectedHResult): Detach failed. Engine continues teardown — failure
            // here usually means the target already exited or mscordbi is in an inconsistent state.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Detach",
                Observed: $"HRESULT 0x{hr:X8}",
                Expected: "S_OK",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{hr:X8}" }));
        }
    }

    /// <summary>Synchronize the target before Detach so mscordbi's RC event thread is not
    /// mid-flush of queued callbacks when Detach tears down the shim (finding 14). Stop blocks
    /// until the process is synchronized; best-effort (a failing HRESULT falls through to
    /// Detach rather than throwing on the dispose path).</summary>
    private void Quiesce()
    {
        int hr = _controller.Stop(0);
        if (hr < 0)
        {
            // EA capture (UnexpectedHResult): Stop failed. Engine continues to Detach as designed
            // (best-effort per the method's existing contract); the anomaly is the substrate
            // signal that the C-DRAIN-CB enforcement may be incomplete.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Quiesce (Stop)",
                Observed: $"HRESULT 0x{hr:X8}",
                Expected: "S_OK (debuggee synchronized for clean Detach)",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{hr:X8}" }));
        }
    }

    public void Dispose()
    {
        // Atomic idempotence gate (ENG-DS-1) — concurrent Dispose calls return without
        // double-Terminate, double-Marshal.Release, or racing _breakpoints iteration.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop our worker first: it drives controller.Continue, so it must be joined before
        // we touch the controller for the quiescent detach.
        _pump.Dispose();

        // Quiescent detach (ADR-006 Phase 2 increment 2; finding 14 / docs/limits/drhook-clean-detach.md).
        // Detach must not race mscordbi's RC event thread flushing queued callbacks — that
        // segfaults the shim mid-flush under load (probe 07). Stop() synchronizes the process:
        // it blocks until any in-flight dispatch completes and the debuggee is halted, so no
        // flush is in progress when Detach tears down the shim. Detach from the synchronized
        // state is the probe-05-validated safe path.
        Quiesce();
        Detach();
        int terminateHr = _cordbg.Terminate();
        if (terminateHr < 0)
        {
            // EA capture (UnexpectedHResult): Terminate failed. Continuing teardown — refs still
            // get released, but the anomaly signals mscordbi state may be inconsistent post-Dispose.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Terminate",
                Observed: $"HRESULT 0x{terminateHr:X8}",
                Expected: "S_OK (ICorDebug fully torn down)",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{terminateHr:X8}" }));
        }

        // Release our breakpoint refs now that the runtime has dropped the breakpoints. No need to
        // deactivate first — Terminate already invalidated them; ClearBreakpoints is for live removal.
        foreach (BreakpointEntry e in _breakpoints)
        {
            RuntimeNavigation.Release(e.Breakpoint);
            RuntimeNavigation.Release(e.Function);
            RuntimeNavigation.Release(e.Module);
        }
        _breakpoints.Clear();

        foreach (SymbolReader? reader in _symbols.Values) reader?.Dispose();
        _symbols.Clear();

        if (_pProcess != 0) { Marshal.Release(_pProcess); _pProcess = 0; }
        if (_pUnknown != 0) { Marshal.Release(_pUnknown); _pUnknown = 0; }

        _callback.Dispose();
        _dbgShim.Dispose();
        GC.KeepAlive(_cordbg);
        GC.KeepAlive(_controller);
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new DebugEngineException(operation, hr);
    }
}

/// <summary>Raised when an ICorDebug operation returns a failure HRESULT.</summary>
public sealed class DebugEngineException : Exception
{
    public DebugEngineException(string operation, int hresult)
        : base($"{operation} failed (HRESULT 0x{hresult:X8}).")
        => HResult = hresult;
}

/// <summary>Outcome of a function evaluation.</summary>
public enum EvalStatus
{
    /// <summary>The eval completed; the result value was returned.</summary>
    Completed,
    /// <summary>The evaluated code threw an exception.</summary>
    ThrewException,
    /// <summary>No EvalComplete arrived within the timeout — the func-eval deadlocked.</summary>
    TimedOut,
    /// <summary>The eval could not be set up (method unresolved, no stop thread, etc.).</summary>
    SetupFailed,
}

/// <summary>Immutable snapshot of a stop's locals/arguments for a conditional-breakpoint predicate.</summary>
internal sealed class EvalContext : IEvalContext
{
    public EvalContext(IReadOnlyList<LocalValue> locals, IReadOnlyList<ArgumentValue> arguments)
    {
        Locals = locals;
        Arguments = arguments;
    }

    public IReadOnlyList<LocalValue> Locals { get; }
    public IReadOnlyList<ArgumentValue> Arguments { get; }
}
