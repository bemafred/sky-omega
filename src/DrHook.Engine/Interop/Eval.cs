// Function evaluation (func-eval) via ICorDebugEval — execute managed code IN the debuggee at a
// stop. Raw V-table (slots from cordebug.idl, verified). This is the EXPERIMENT path: netcoredbg's
// func-eval deadlocks on macOS/ARM64, but that was characterized in netcoredbg, not the platform —
// vsdbg/Rider eval fine, so the surface is reachable. Whether OUR ICorDebug usage deadlocks is the
// open question probe 19 answers. If it works, this is the foundation for conditional breakpoints
// and expression evaluation (the netcoredbg gap); if it deadlocks, this scaffold is reverted.
//
// Protocol: CreateEval on the stopped thread → CallFunction (sets up the call) → the caller
// Continues (runs the eval) → an EvalComplete (or EvalException) callback fires → GetResult reads
// the return value while still synchronized. A Continue that never produces EvalComplete is the
// deadlock signal (detected as a WaitForStop timeout in DebugSession).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class Eval
{
    private const int ThreadCreateEval = 17;          // ICorDebugThread (… CreateStepper12 … CreateEval17)
    private const int EvalCallFunction = 3;           // ICorDebugEval (CallFunction3, …, IsActive8, Abort9, GetResult10, …, CreateValue12)
    private const int EvalAbort = 9;
    private const int EvalGetResult = 10;
    private const int EvalCreateValue = 12;
    private const int GenericValueSetValue = 8;       // ICorDebugGenericValue (after ICorDebugValue 3-6, GetValue7, SetValue8)
    private const int ModuleGetFunctionFromToken = 9; // ICorDebugModule

    private static readonly Guid IID_ICorDebugGenericValue = new("CC7BCAF8-8A68-11D2-983C-0000F808342D");
    private const int ELEMENT_TYPE_I4 = 0x08;

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Resolve an <c>mdMethodDef</c> to an <c>ICorDebugFunction</c> (owned; caller releases).</summary>
    public static nint GetFunction(nint pModule, uint methodToken)
    {
        var get = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(pModule, ModuleGetFunctionFromToken);
        nint function;
        return get(pModule, methodToken, &function) < 0 ? 0 : function;
    }

    /// <summary>Create an evaluation on the (stopped) thread. Owned; caller releases.</summary>
    public static nint CreateEval(nint pThread)
    {
        var create = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pThread, ThreadCreateEval);
        nint eval;
        return create(pThread, &eval) < 0 ? 0 : eval;
    }

    /// <summary>Set up a call to a static, parameterless <paramref name="pFunction"/>. The eval
    /// runs when the caller Continues; completion arrives as an EvalComplete/EvalException callback.</summary>
    public static bool CallStaticNoArgs(nint pEval, nint pFunction)
    {
        // ICorDebugEval.CallFunction(ICorDebugFunction*, ULONG32 nArgs, ICorDebugValue* ppArgs[])
        var call = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, int>)Slot(pEval, EvalCallFunction);
        return call(pEval, pFunction, 0, 0) >= 0;
    }

    /// <summary>Create an I4 eval value set to <paramref name="value"/> (an owned arg the caller
    /// passes to a call and releases). 0 on failure.</summary>
    public static nint CreateInt32(nint pEval, int value)
    {
        var createValue = (delegate* unmanaged[Cdecl]<nint, int, nint, nint*, int>)Slot(pEval, EvalCreateValue);
        nint pValue;
        if (createValue(pEval, ELEMENT_TYPE_I4, 0, &pValue) < 0 || pValue == 0) return 0;

        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pValue, 0);
        Guid iid = IID_ICorDebugGenericValue;
        nint generic;
        if (qi(pValue, &iid, &generic) < 0 || generic == 0) { RuntimeNavigation.Release(pValue); return 0; }
        try
        {
            int local = value;
            var setValue = (delegate* unmanaged[Cdecl]<nint, void*, int>)Slot(generic, GenericValueSetValue);
            if (setValue(generic, &local) < 0) { RuntimeNavigation.Release(pValue); return 0; }
        }
        finally { RuntimeNavigation.Release(generic); }
        return pValue;
    }

    /// <summary>Set up a call to <paramref name="pFunction"/> with one argument value (for an
    /// instance method, that single argument is <c>this</c>). Runs on the next Continue; completion
    /// arrives as EvalComplete/EvalException.</summary>
    public static bool CallWithOneArg(nint pEval, nint pFunction, nint pArg)
    {
        var call = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint*, int>)Slot(pEval, EvalCallFunction);
        nint args = pArg; // single-element ICorDebugValue*[]
        return call(pEval, pFunction, 1, &args) >= 0;
    }

    /// <summary>Abort a running/hung evaluation (ICorDebugEval.Abort) — the safety net for an eval
    /// that deadlocks on a target-side lock. Best-effort.</summary>
    public static void Abort(nint pEval)
        => ((delegate* unmanaged[Cdecl]<nint, int>)Slot(pEval, EvalAbort))(pEval);

    /// <summary>Read the eval's result value (after EvalComplete) as an <see cref="ArgumentValue"/>.</summary>
    public static ArgumentValue GetResultValue(nint pEval)
    {
        var getResult = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pEval, EvalGetResult);
        nint value;
        if (getResult(pEval, &value) < 0 || value == 0) return new ArgumentValue(0, null);
        try { return Variables.ReadValue(value); }
        finally { RuntimeNavigation.Release(value); }
    }
}
