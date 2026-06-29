using System;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Engine;

public sealed partial class DebugSession
{
    private const int ELEMENT_TYPE_STRING = 0x0E; // CorElementType — picks Save(string) over Save(Stream)

    /// <summary>ADR-012 Q8 (a) mechanic 7 — the CAPTURE ORCHESTRATION the six probe mechanics (chaining /
    /// getter-chain / N-arg call / NewObject / NewString / value-type) each prove a part of, here threaded into
    /// ONE cooperation-free in-process render: walk a static-then-instance getter chain to the live top-level
    /// visual (e.g. <c>Application.Current.ApplicationLifetime.MainWindow</c>), construct the framework's
    /// pixel-size value type and a render-target bitmap over it, invoke the bitmap's render on the visual, then
    /// save it to a file the debugger reads back — all by func-evaluating the DEBUGGEE'S OWN framework APIs, with
    /// no debuggee cooperation. Framework-agnostic: every name comes from <paramref name="plan"/>.
    ///
    /// Each intermediate object's <c>ICorDebugValue</c> is held live (its producing eval kept alive) across the
    /// steps that consume it — the proven getter-chain handle-threading discipline (only as many evals as a step
    /// needs are live at once). Valid only while STOPPED on the thread that should run the render (typically the
    /// UI thread — break at a UI-thread site first). <paramref name="trace"/> reports the furthest step reached
    /// ("window-resolved" → "pixelsize-built" → "bitmap-built" → "render-complete" → "saved"), so a partial run
    /// still says exactly where it stopped — in particular whether <c>render-complete</c> was reached (the open
    /// question: does the framework's render run during a func-eval).</summary>
    public EvalStatus TryEvalRenderCapture(in RenderCapturePlan plan, TimeSpan timeout, out string trace)
    {
        trace = "start";
        nint appModule = RuntimeNavigation.FindModule(_pProcess, plan.AppModule);
        nint gfxModule = RuntimeNavigation.FindModule(_pProcess, plan.GfxModule);
        nint windowEval = 0, window = 0, sizeEval = 0, size = 0, rtbEval = 0, rtb = 0, pathEval = 0, path = 0;
        try
        {
            if (appModule == 0) { trace = $"module-not-found:{plan.AppModule}"; return EvalStatus.SetupFailed; }
            if (gfxModule == 0) { trace = $"module-not-found:{plan.GfxModule}"; return EvalStatus.SetupFailed; }

            // ── Step 1 — the static getter root: e.g. Application.Current (a static, no-arg property). ──
            uint currentTok = MetadataResolver.ResolveMethodToken(appModule, plan.AppType, plan.CurrentGetter);
            if (currentTok == 0) { trace = $"unresolved:{plan.AppType}.{plan.CurrentGetter}"; return EvalStatus.SetupFailed; }
            nint currentFn = Eval.GetFunction(appModule, currentTok);
            if (currentFn == 0) { trace = "no-function:current"; return EvalStatus.SetupFailed; }
            EvalStatus s;
            try { s = RunEvalForRaw(currentFn, ReadOnlySpan<nint>.Empty, isCtor: false, timeout, out window, out windowEval); }
            finally { RuntimeNavigation.Release(currentFn); }
            if (s != EvalStatus.Completed) { trace = $"current:{s}"; return s; }
            if (window == 0) { trace = "current:null"; return EvalStatus.SetupFailed; }

            // ── Step 1b — instance getter chain (e.g. .ApplicationLifetime → .MainWindow): each getter is
            //    resolved on the PRIOR result's runtime type and called with that result as `this`. ──
            Span<nint> oneArg = stackalloc nint[1];
            foreach (string member in plan.WindowGetterChain)
            {
                nint getter = MemberResolver.ResolveGetter(window, member);
                if (getter == 0) { trace = $"getter-unresolved:{member}"; return EvalStatus.SetupFailed; }
                nint hopEval = 0, next = 0;
                EvalStatus hs;
                try
                {
                    oneArg[0] = window;
                    hs = RunEvalForRaw(getter, oneArg, isCtor: false, timeout, out next, out hopEval);
                }
                finally { RuntimeNavigation.Release(getter); }
                if (hs != EvalStatus.Completed) { trace = $"getter:{member}:{hs}"; return hs; }
                if (next == 0) { trace = $"getter:{member}:null"; if (hopEval != 0) RuntimeNavigation.Release(hopEval); return EvalStatus.SetupFailed; }
                // The prior hop is consumed (its value was just the receiver); swap the new result in.
                RuntimeNavigation.Release(window);
                RuntimeNavigation.Release(windowEval);
                window = next; windowEval = hopEval;
            }
            trace = "window-resolved";

            // ── Step 2 — construct the pixel-size value type: new PixelSize(Width, Height) (2-arg ctor). ──
            uint sizeCtor = MetadataResolver.ResolveOverload(gfxModule, plan.PixelSizeType, ".ctor", paramCount: 2, firstParamElementType: 0);
            if (sizeCtor == 0) { trace = $"unresolved-ctor:{plan.PixelSizeType}(2)"; return EvalStatus.SetupFailed; }
            nint sizeCtorFn = Eval.GetFunction(gfxModule, sizeCtor);
            if (sizeCtorFn == 0) { trace = "no-function:pixelsize-ctor"; return EvalStatus.SetupFailed; }
            try
            {
                nint sizeEvalLocal = Eval.CreateEval(_pump.StopThread);
                if (sizeEvalLocal == 0) { trace = "no-eval:pixelsize"; return EvalStatus.SetupFailed; }
                nint argW = Eval.CreateInt32(sizeEvalLocal, plan.Width);
                nint argH = Eval.CreateInt32(sizeEvalLocal, plan.Height);
                if (argW == 0 || argH == 0)
                {
                    if (argW != 0) RuntimeNavigation.Release(argW);
                    if (argH != 0) RuntimeNavigation.Release(argH);
                    RuntimeNavigation.Release(sizeEvalLocal);
                    trace = "arg-create:pixelsize"; return EvalStatus.SetupFailed;
                }
                Span<nint> ctorArgs = stackalloc nint[2];
                ctorArgs[0] = argW; ctorArgs[1] = argH;
                bool ok = Eval.NewObject(sizeEvalLocal, sizeCtorFn, ctorArgs);
                RuntimeNavigation.Release(argW); RuntimeNavigation.Release(argH);
                if (!ok) { RuntimeNavigation.Release(sizeEvalLocal); trace = "setup:pixelsize"; return EvalStatus.SetupFailed; }
                EvalStatus ps = RunToComplete(sizeEvalLocal, timeout);
                if (ps != EvalStatus.Completed) { RuntimeNavigation.Release(sizeEvalLocal); trace = $"pixelsize:{ps}"; return ps; }
                size = Eval.GetResultRaw(sizeEvalLocal);
                sizeEval = sizeEvalLocal;
            }
            finally { RuntimeNavigation.Release(sizeCtorFn); }
            if (size == 0) { trace = "pixelsize:null"; return EvalStatus.SetupFailed; }
            trace = "pixelsize-built";

            // ── Step 3 — construct the render target: new RenderTargetBitmap(pixelSize) (the 1-arg ctor). ──
            uint rtbCtor = MetadataResolver.ResolveOverload(gfxModule, plan.BitmapType, ".ctor", paramCount: 1, firstParamElementType: 0);
            if (rtbCtor == 0) { trace = $"unresolved-ctor:{plan.BitmapType}(1)"; return EvalStatus.SetupFailed; }
            nint rtbCtorFn = Eval.GetFunction(gfxModule, rtbCtor);
            if (rtbCtorFn == 0) { trace = "no-function:bitmap-ctor"; return EvalStatus.SetupFailed; }
            try
            {
                oneArg[0] = size;
                s = RunEvalForRaw(rtbCtorFn, oneArg, isCtor: true, timeout, out rtb, out rtbEval);
            }
            finally { RuntimeNavigation.Release(rtbCtorFn); }
            if (s != EvalStatus.Completed) { trace = $"bitmap:{s}"; return s; }
            if (rtb == 0) { trace = "bitmap:null"; return EvalStatus.SetupFailed; }
            // The size value is consumed by the ctor — release it now.
            RuntimeNavigation.Release(size); RuntimeNavigation.Release(sizeEval); size = 0; sizeEval = 0;
            trace = "bitmap-built";

            // ── Step 4 — THE OPEN QUESTION: rtb.Render(window). Does the framework's render run during a
            //    func-eval Continue at a stop? args[0] = `this` (rtb), args[1] = the visual. ──
            uint renderTok = MetadataResolver.ResolveOverload(gfxModule, plan.BitmapType, plan.RenderMethod, paramCount: 1, firstParamElementType: 0);
            if (renderTok == 0) { trace = $"unresolved:{plan.BitmapType}.{plan.RenderMethod}"; return EvalStatus.SetupFailed; }
            nint renderFn = Eval.GetFunction(gfxModule, renderTok);
            if (renderFn == 0) { trace = "no-function:render"; return EvalStatus.SetupFailed; }
            try
            {
                Span<nint> renderArgs = stackalloc nint[2];
                renderArgs[0] = rtb; renderArgs[1] = window;
                s = RunEvalForRaw(renderFn, renderArgs, isCtor: false, timeout, out _, out nint renderEval);
                if (renderEval != 0) RuntimeNavigation.Release(renderEval);
            }
            finally { RuntimeNavigation.Release(renderFn); }
            if (s != EvalStatus.Completed) { trace = $"render:{s}"; return s; }
            // The window is no longer needed once rendered.
            RuntimeNavigation.Release(window); RuntimeNavigation.Release(windowEval); window = 0; windowEval = 0;
            trace = "render-complete"; // ← the open question is answered YES if we reach here

            // ── Step 5 — create the output path string in the debuggee (NewString). ──
            nint pathEvalLocal = Eval.CreateEval(_pump.StopThread);
            if (pathEvalLocal == 0) { trace = "no-eval:path"; return EvalStatus.SetupFailed; }
            if (!Eval.NewString(pathEvalLocal, plan.OutputPath)) { RuntimeNavigation.Release(pathEvalLocal); trace = "setup:path"; return EvalStatus.SetupFailed; }
            EvalStatus pst = RunToComplete(pathEvalLocal, timeout);
            if (pst != EvalStatus.Completed) { RuntimeNavigation.Release(pathEvalLocal); trace = $"path:{pst}"; return pst; }
            path = Eval.GetResultRaw(pathEvalLocal);
            pathEval = pathEvalLocal;
            if (path == 0) { trace = "path:null"; return EvalStatus.SetupFailed; }

            // ── Step 6 — rtb.Save(path, quality). The framework's Save is Save(string fileName, int? quality)
            //    (2 params); pick the string overload (vs Save(Stream, int?)) by the STRING first-param element
            //    type. quality is a Nullable<int> — pass its DEFAULT (null / HasValue=false), the param's own
            //    default, built correctly-typed via CreateValueForType (a plain I4 here WEDGES the eval). args[0]
            //    = `this` (rtb), args[1] = path, args[2] = quality. ──
            uint saveTok = MetadataResolver.ResolveOverload(gfxModule, plan.BitmapType, plan.SaveMethod, paramCount: 2, firstParamElementType: ELEMENT_TYPE_STRING);
            if (saveTok == 0) { trace = $"unresolved:{plan.BitmapType}.{plan.SaveMethod}(string,..)"; return EvalStatus.SetupFailed; }
            nint saveFn = Eval.GetFunction(gfxModule, saveTok);
            if (saveFn == 0) { trace = "no-function:save"; return EvalStatus.SetupFailed; }
            try
            {
                nint saveEval = Eval.CreateEval(_pump.StopThread);
                if (saveEval == 0) { trace = "no-eval:save"; return EvalStatus.SetupFailed; }
                nint quality = ValueFactory.CreateDefaultNullableInt32(saveEval, _pProcess);
                if (quality == 0) { RuntimeNavigation.Release(saveEval); trace = "arg-create:save-quality(nullable)"; return EvalStatus.SetupFailed; }
                Span<nint> saveArgs = stackalloc nint[3];
                saveArgs[0] = rtb; saveArgs[1] = path; saveArgs[2] = quality;
                bool ok = Eval.CallFunction(saveEval, saveFn, saveArgs);
                RuntimeNavigation.Release(quality);
                if (!ok) { RuntimeNavigation.Release(saveEval); trace = "setup:save"; return EvalStatus.SetupFailed; }
                s = RunToComplete(saveEval, timeout);
                RuntimeNavigation.Release(saveEval);
            }
            finally { RuntimeNavigation.Release(saveFn); }
            if (s != EvalStatus.Completed) { trace = $"save:{s}"; return s; }

            trace = "saved";
            return EvalStatus.Completed;
        }
        finally
        {
            if (window != 0) RuntimeNavigation.Release(window);
            if (windowEval != 0) RuntimeNavigation.Release(windowEval);
            if (size != 0) RuntimeNavigation.Release(size);
            if (sizeEval != 0) RuntimeNavigation.Release(sizeEval);
            if (rtb != 0) RuntimeNavigation.Release(rtb);
            if (rtbEval != 0) RuntimeNavigation.Release(rtbEval);
            if (path != 0) RuntimeNavigation.Release(path);
            if (pathEval != 0) RuntimeNavigation.Release(pathEval);
            if (appModule != 0) RuntimeNavigation.Release(appModule);
            if (gfxModule != 0) RuntimeNavigation.Release(gfxModule);
        }
    }

    // Create an eval on the stop thread, set up the call (NewObject when isCtor — ctor params only; else
    // CallFunction — for an instance method args[0] is `this`), run it to completion, and hand back BOTH the raw
    // result handle (0 for a void call, e.g. Render/Save) and the eval that ROOTS that result. The caller holds
    // the eval while the result is a downstream argument, then releases both; the function handle stays the
    // caller's to release. On any failure the eval is released here and the status is non-Completed. Factors the
    // per-step boilerplate (the same five lines the getter chain and the single-shot evals repeat) so the
    // orchestration reads as the render chain it is. RunToComplete aborts a timed-out eval before returning.
    private EvalStatus RunEvalForRaw(nint function, ReadOnlySpan<nint> args, bool isCtor, TimeSpan timeout,
                                     out nint resultRaw, out nint evalHandle)
    {
        resultRaw = 0; evalHandle = 0;
        nint eval = Eval.CreateEval(_pump.StopThread);
        if (eval == 0) return EvalStatus.SetupFailed;
        bool ok = isCtor ? Eval.NewObject(eval, function, args) : Eval.CallFunction(eval, function, args);
        if (!ok) { RuntimeNavigation.Release(eval); return EvalStatus.SetupFailed; }
        EvalStatus status = RunToComplete(eval, timeout);
        if (status != EvalStatus.Completed) { RuntimeNavigation.Release(eval); return status; }
        resultRaw = Eval.GetResultRaw(eval);
        evalHandle = eval;
        return EvalStatus.Completed;
    }
}
