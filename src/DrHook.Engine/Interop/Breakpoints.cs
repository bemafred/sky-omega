// Create + activate a function-entry breakpoint from a module + method token. Raw V-table
// (slots from cordebug.idl order), validated by probe 12. A hit later arrives as the
// ICorDebugManagedCallback::Breakpoint callback, which the pump classifies as a stopping event
// (StopReason.Breakpoint) — so this composes with the probe-09 stopping model with no new
// callback wiring.
//
// PRECONDITION: process synchronized (set at a stop). The breakpoint binds when the function is
// JIT-compiled, so it can be set before the method is ever called.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class Breakpoints
{
    private const int ModuleGetFunctionFromToken = 9; // ICorDebugModule (IUnknown 0-2, own 3-)
    private const int FunctionGetILCode = 6;          // ICorDebugFunction (GetModule3, GetClass4, GetToken5, GetILCode6, GetNativeCode7, CreateBreakpoint8)
    private const int FunctionCreateBreakpoint = 8;
    private const int CodeCreateBreakpoint = 7;        // ICorDebugCode (IsIL3, GetFunction4, GetAddress5, GetSize6, CreateBreakpoint7)
    private const int BreakpointActivate = 3;          // ICorDebugBreakpoint (Activate3, IsActive4)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Resolve the function for <paramref name="methodToken"/> in
    /// <paramref name="pModule"/>, create a breakpoint at its entry, and activate it. On success
    /// returns owned <paramref name="pFunction"/> + <paramref name="pBreakpoint"/> references the
    /// caller must keep alive while the breakpoint is set (and Release to clear).</summary>
    public static bool TryCreate(nint pModule, uint methodToken, out nint pFunction, out nint pBreakpoint)
    {
        pFunction = 0;
        pBreakpoint = 0;

        // ICorDebugModule.GetFunctionFromToken(mdMethodDef, ICorDebugFunction**)
        var getFunction = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(pModule, ModuleGetFunctionFromToken);
        nint function;
        if (getFunction(pModule, methodToken, &function) < 0 || function == 0) return false;

        // ICorDebugFunction.CreateBreakpoint(ICorDebugFunctionBreakpoint**) — at function entry.
        var createBreakpoint = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(function, FunctionCreateBreakpoint);
        nint breakpoint;
        if (createBreakpoint(function, &breakpoint) < 0 || breakpoint == 0)
        {
            RuntimeNavigation.Release(function);
            return false;
        }

        // ICorDebugBreakpoint.Activate(BOOL)
        var activate = (delegate* unmanaged[Cdecl]<nint, int, int>)Slot(breakpoint, BreakpointActivate);
        if (activate(breakpoint, 1) < 0)
        {
            RuntimeNavigation.Release(breakpoint);
            RuntimeNavigation.Release(function);
            return false;
        }

        pFunction = function;
        pBreakpoint = breakpoint;
        return true;
    }

    /// <summary>Like <see cref="TryCreate"/> but bind at a specific IL <paramref name="ilOffset"/>
    /// (for source-line breakpoints) via <c>ICorDebugCode.CreateBreakpoint(offset)</c> instead of
    /// the function-entry overload.</summary>
    public static bool TryCreateAtOffset(nint pModule, uint methodToken, uint ilOffset, out nint pFunction, out nint pBreakpoint)
    {
        pFunction = 0;
        pBreakpoint = 0;

        var getFunction = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(pModule, ModuleGetFunctionFromToken);
        nint function;
        if (getFunction(pModule, methodToken, &function) < 0 || function == 0) return false;

        // ICorDebugFunction.GetILCode(ICorDebugCode**) → the IL code object that offsets address into.
        var getILCode = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(function, FunctionGetILCode);
        nint code;
        if (getILCode(function, &code) < 0 || code == 0) { RuntimeNavigation.Release(function); return false; }

        bool ok = false;
        try
        {
            // ICorDebugCode.CreateBreakpoint(ULONG32 offset, ICorDebugFunctionBreakpoint**)
            var createBreakpoint = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(code, CodeCreateBreakpoint);
            nint breakpoint;
            if (createBreakpoint(code, ilOffset, &breakpoint) < 0 || breakpoint == 0) return false;

            var activate = (delegate* unmanaged[Cdecl]<nint, int, int>)Slot(breakpoint, BreakpointActivate);
            if (activate(breakpoint, 1) < 0) { RuntimeNavigation.Release(breakpoint); return false; }

            pFunction = function;
            pBreakpoint = breakpoint;
            ok = true;
            return true;
        }
        finally
        {
            RuntimeNavigation.Release(code);
            if (!ok) RuntimeNavigation.Release(function);
        }
    }
}
