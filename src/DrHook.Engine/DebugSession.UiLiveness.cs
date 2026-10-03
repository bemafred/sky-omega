using System;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Engine;

public sealed partial class DebugSession
{
    private const byte ELEMENT_TYPE_CLASS = 0x12;  // CorElementType — a reference-type parameter (Type, MethodInfo, Action)
    private const byte ELEMENT_TYPE_OBJECT = 0x1C; // CorElementType — System.Object

    /// <summary>Queue the UI-liveness job on the debuggee's UI dispatcher, by func-eval at the current stop:
    /// <c>Dispatcher.InvokeAsync((Action)Delegate.CreateDelegate(typeof(Action), sentinelPath,
    /// typeof(File).GetMethod("Delete")))</c> — an <c>Action</c> closed over its first argument, i.e.
    /// <c>() =&gt; File.Delete(sentinelPath)</c>. Nothing runs yet: the job executes only when the target resumes and
    /// its UI thread drains the dispatcher queue, which <see cref="DispatcherDrainSentinel.WaitForDrain"/> observes
    /// as the file disappearing.
    ///
    /// Why this shape: a hung UI thread stuck in NATIVE framework code (the suspected site of the 1-in-41
    /// post-capture hang) shows the same managed stack as an idle one, so no stack-shape signal can tell them apart;
    /// a job the UI thread must EXECUTE can. The file outlives the debugger, so the verdict covers the window AFTER
    /// detach — when that hang appeared. Cost to the target: one queued job that deletes the debugger's own temp file.
    ///
    /// Valid only while stopped (any thread works for the evals; the job is queued, not run inline).
    /// <paramref name="trace"/> reports the furthest step reached ("strings" → "method" → "action" → "dispatcher" →
    /// "posted").</summary>
    public EvalStatus TryEvalPostLivenessJob(in UiLivenessPlan plan, string sentinelPath, TimeSpan timeout, out string trace)
    {
        trace = "start";
        nint coreLib = RuntimeNavigation.FindModule(_pProcess, "System.Private.CoreLib");
        nint uiModule = RuntimeNavigation.FindModule(_pProcess, plan.DispatcherModule);
        // Every intermediate value stays rooted by its producing eval until the step that consumes it has run.
        nint fileName = 0, fileNameEval = 0, deleteName = 0, deleteNameEval = 0, actionName = 0, actionNameEval = 0;
        nint path = 0, pathEval = 0, fileType = 0, fileTypeEval = 0, actionType = 0, actionTypeEval = 0;
        nint delete = 0, deleteEval = 0, action = 0, actionEval = 0, dispatcher = 0, dispatcherEval = 0;
        try
        {
            if (coreLib == 0) { trace = "module-not-found:System.Private.CoreLib"; return EvalStatus.SetupFailed; }
            if (uiModule == 0) { trace = $"module-not-found:{plan.DispatcherModule}"; return EvalStatus.SetupFailed; }

            // ── Step 1 — the names and the sentinel path, as debuggee strings. ──
            EvalStatus s = NewDebuggeeString("System.IO.File", timeout, out fileName, out fileNameEval);
            if (s != EvalStatus.Completed) { trace = $"string:System.IO.File:{s}"; return s; }
            s = NewDebuggeeString("Delete", timeout, out deleteName, out deleteNameEval);
            if (s != EvalStatus.Completed) { trace = $"string:Delete:{s}"; return s; }
            s = NewDebuggeeString("System.Action", timeout, out actionName, out actionNameEval);
            if (s != EvalStatus.Completed) { trace = $"string:System.Action:{s}"; return s; }
            s = NewDebuggeeString(sentinelPath, timeout, out path, out pathEval);
            if (s != EvalStatus.Completed) { trace = $"string:sentinel:{s}"; return s; }
            trace = "strings";

            // ── Step 2 — Type.GetType(string) for System.IO.File and System.Action (both CoreLib types). ──
            uint getTypeTok = MetadataResolver.ResolveOverload(coreLib, "System.Type", "GetType", paramCount: 1, [ELEMENT_TYPE_STRING]);
            if (getTypeTok == 0) { trace = "unresolved:System.Type.GetType(string)"; return EvalStatus.SetupFailed; }
            nint getTypeFn = Eval.GetFunction(coreLib, getTypeTok);
            if (getTypeFn == 0) { trace = "no-function:Type.GetType"; return EvalStatus.SetupFailed; }
            try
            {
                Span<nint> nameArg = stackalloc nint[1];
                nameArg[0] = fileName;
                s = RunEvalForRaw(getTypeFn, nameArg, isCtor: false, timeout, out fileType, out fileTypeEval);
                if (s != EvalStatus.Completed) { trace = $"gettype:System.IO.File:{s}"; return s; }
                nameArg[0] = actionName;
                s = RunEvalForRaw(getTypeFn, nameArg, isCtor: false, timeout, out actionType, out actionTypeEval);
                if (s != EvalStatus.Completed) { trace = $"gettype:System.Action:{s}"; return s; }
            }
            finally { RuntimeNavigation.Release(getTypeFn); }
            if (fileType == 0 || actionType == 0) { trace = "gettype:null"; return EvalStatus.SetupFailed; }

            // ── Step 3 — typeof(File).GetMethod("Delete") — File.Delete has the single (string) overload. ──
            uint getMethodTok = MetadataResolver.ResolveOverload(coreLib, "System.Type", "GetMethod", paramCount: 1, [ELEMENT_TYPE_STRING]);
            if (getMethodTok == 0) { trace = "unresolved:System.Type.GetMethod(string)"; return EvalStatus.SetupFailed; }
            nint getMethodFn = Eval.GetFunction(coreLib, getMethodTok);
            if (getMethodFn == 0) { trace = "no-function:Type.GetMethod"; return EvalStatus.SetupFailed; }
            try
            {
                Span<nint> getMethodArgs = stackalloc nint[2];
                getMethodArgs[0] = fileType; getMethodArgs[1] = deleteName;
                s = RunEvalForRaw(getMethodFn, getMethodArgs, isCtor: false, timeout, out delete, out deleteEval);
            }
            finally { RuntimeNavigation.Release(getMethodFn); }
            if (s != EvalStatus.Completed) { trace = $"getmethod:{s}"; return s; }
            if (delete == 0) { trace = "getmethod:null"; return EvalStatus.SetupFailed; }
            trace = "method";

            // ── Step 4 — Delegate.CreateDelegate(Type type, object firstArgument, MethodInfo method): an Action closed
            //    over the sentinel path. Told apart from (Type, object, string) by the THIRD parameter (CLASS vs
            //    STRING) and from (Type, Type, string) / (Type, MethodInfo, bool) by the second (OBJECT). ──
            uint createTok = MetadataResolver.ResolveOverload(coreLib, "System.Delegate", "CreateDelegate", paramCount: 3,
                [ELEMENT_TYPE_CLASS, ELEMENT_TYPE_OBJECT, ELEMENT_TYPE_CLASS]);
            if (createTok == 0) { trace = "unresolved:Delegate.CreateDelegate(Type,object,MethodInfo)"; return EvalStatus.SetupFailed; }
            nint createFn = Eval.GetFunction(coreLib, createTok);
            if (createFn == 0) { trace = "no-function:CreateDelegate"; return EvalStatus.SetupFailed; }
            try
            {
                Span<nint> createArgs = stackalloc nint[3];
                createArgs[0] = actionType; createArgs[1] = path; createArgs[2] = delete;
                s = RunEvalForRaw(createFn, createArgs, isCtor: false, timeout, out action, out actionEval);
            }
            finally { RuntimeNavigation.Release(createFn); }
            if (s != EvalStatus.Completed) { trace = $"createdelegate:{s}"; return s; }
            if (action == 0) { trace = "createdelegate:null"; return EvalStatus.SetupFailed; }
            trace = "action";

            // ── Step 5 — the UI dispatcher (a static, no-arg getter, e.g. Dispatcher.UIThread). ──
            uint getterTok = MetadataResolver.ResolveMethodToken(uiModule, plan.DispatcherType, plan.DispatcherGetter);
            if (getterTok == 0) { trace = $"unresolved:{plan.DispatcherType}.{plan.DispatcherGetter}"; return EvalStatus.SetupFailed; }
            nint getterFn = Eval.GetFunction(uiModule, getterTok);
            if (getterFn == 0) { trace = "no-function:dispatcher-getter"; return EvalStatus.SetupFailed; }
            try { s = RunEvalForRaw(getterFn, ReadOnlySpan<nint>.Empty, isCtor: false, timeout, out dispatcher, out dispatcherEval); }
            finally { RuntimeNavigation.Release(getterFn); }
            if (s != EvalStatus.Completed) { trace = $"dispatcher:{s}"; return s; }
            if (dispatcher == 0) { trace = "dispatcher:null"; return EvalStatus.SetupFailed; }
            trace = "dispatcher";

            // ── Step 6 — dispatcher.InvokeAsync(action): the single-parameter overload taking a class (Action) —
            //    not the generic Func<T> overloads (GENERICINST, which the resolver skips). args[0] = `this`. ──
            uint postTok = MetadataResolver.ResolveOverload(uiModule, plan.DispatcherType, plan.PostMethod, paramCount: 1, [ELEMENT_TYPE_CLASS]);
            if (postTok == 0) { trace = $"unresolved:{plan.DispatcherType}.{plan.PostMethod}(Action)"; return EvalStatus.SetupFailed; }
            nint postFn = Eval.GetFunction(uiModule, postTok);
            if (postFn == 0) { trace = "no-function:post"; return EvalStatus.SetupFailed; }
            try
            {
                Span<nint> postArgs = stackalloc nint[2];
                postArgs[0] = dispatcher; postArgs[1] = action;
                s = RunEvalForRaw(postFn, postArgs, isCtor: false, timeout, out nint operation, out nint postEval);
                if (operation != 0) RuntimeNavigation.Release(operation);
                if (postEval != 0) RuntimeNavigation.Release(postEval);
            }
            finally { RuntimeNavigation.Release(postFn); }
            if (s != EvalStatus.Completed) { trace = $"post:{s}"; return s; }

            trace = "posted";
            return EvalStatus.Completed;
        }
        finally
        {
            foreach (nint p in (ReadOnlySpan<nint>)[fileName, fileNameEval, deleteName, deleteNameEval, actionName, actionNameEval,
                                                     path, pathEval, fileType, fileTypeEval, actionType, actionTypeEval,
                                                     delete, deleteEval, action, actionEval, dispatcher, dispatcherEval,
                                                     coreLib, uiModule])
                if (p != 0) RuntimeNavigation.Release(p);
        }
    }

    // A debuggee string (ICorDebugEval2.NewString) plus the eval rooting it — the caller releases both once the
    // string has been consumed as an argument.
    private EvalStatus NewDebuggeeString(string value, TimeSpan timeout, out nint stringValue, out nint evalHandle)
    {
        stringValue = 0; evalHandle = 0;
        nint eval = Eval.CreateEval(_pump.StopThread);
        if (eval == 0) return EvalStatus.SetupFailed;
        if (!Eval.NewString(eval, value)) { RuntimeNavigation.Release(eval); return EvalStatus.SetupFailed; }
        EvalStatus status = RunToComplete(eval, timeout);
        if (status != EvalStatus.Completed) { RuntimeNavigation.Release(eval); return status; }
        stringValue = Eval.GetResultRaw(eval);
        evalHandle = eval;
        return EvalStatus.Completed;
    }
}
