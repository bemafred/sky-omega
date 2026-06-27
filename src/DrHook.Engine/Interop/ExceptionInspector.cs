// Inspect the exception currently in flight on a thread — raw value (for func-eval as `this`),
// runtime type name, and the full inheritance chain of type names (across modules).
//
// The chain walk uses ICorDebugValue2.GetExactType@3 → ICorDebugType.GetBase@7 (same primitive as
// MemberResolver after the probe-37 refactor): the runtime handles mdTypeRef resolution so the
// chain spans modules without manual typeref work. CurrentExceptionTypeName is a thin convenience
// over chain[0] (the runtime type).
//
// Slots + IIDs verified from cordebug.idl. PRECONDITION: process synchronized (called at a stop).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ExceptionInspector
{
    private static readonly Guid IID_ICorDebugValue2 = new("5E0B54E7-D88A-4626-9420-A691E0A78B49");

    private const int ThreadGetCurrentException = 10;  // ICorDebugThread
    private const int Value2GetExactType        = 3;   // ICorDebugValue2
    private const int TypeGetBase               = 7;   // ICorDebugType (GetClass→name now via ValueTypeInspector.NameOfType)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static nint QueryInterface(nint pUnk, Guid iid)
    {
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pUnk, 0);
        nint result;
        return qi(pUnk, &iid, &result) < 0 ? 0 : result;
    }

    private static nint Out(nint pUnk, int slot)
    {
        nint outPtr;
        return ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pUnk, slot))(pUnk, &outPtr) < 0 ? 0 : outPtr;
    }

    /// <summary>The raw <c>ICorDebugValue</c> (an object reference) for the exception currently in
    /// flight on <paramref name="pThread"/>, or 0 if none. OWNED — the caller releases it via
    /// <see cref="RuntimeNavigation.Release"/>. Suitable as the <c>this</c> of a func-eval (the
    /// runtime preserves the exception across the eval per cordebug.idl).</summary>
    public static nint CurrentExceptionValue(nint pThread)
        => pThread == 0 ? 0 : Out(pThread, ThreadGetCurrentException);

    /// <summary>The fully-qualified type name of the exception currently being thrown on
    /// <paramref name="pThread"/> (e.g. "System.InvalidOperationException"), or null if there is no
    /// current exception. The runtime type — equivalent to <c>CurrentExceptionTypeChain[0]</c>.</summary>
    public static string? CurrentExceptionTypeName(nint pThread)
    {
        IReadOnlyList<string> chain = CurrentExceptionTypeChain(pThread);
        return chain.Count > 0 ? chain[0] : null;
    }

    /// <summary>The full inheritance chain of type names for the exception currently in flight on
    /// <paramref name="pThread"/>: index 0 is the runtime type, then each base in order up to (but
    /// not always including) <c>System.Object</c>. Walks via <c>ICorDebugType.GetBase</c>, so the
    /// chain spans modules (probe 37). Empty if there is no exception or the value isn't reachable.
    /// Used by subclass-aware exception filters (probe 38).</summary>
    public static IReadOnlyList<string> CurrentExceptionTypeChain(nint pThread)
    {
        if (pThread == 0) return Array.Empty<string>();
        nint value = Out(pThread, ThreadGetCurrentException);
        if (value == 0) return Array.Empty<string>();

        List<string> chain = new();
        try
        {
            nint value2 = QueryInterface(value, IID_ICorDebugValue2);
            if (value2 == 0) return chain;
            nint type;
            try { type = Out(value2, Value2GetExactType); }
            finally { RuntimeNavigation.Release(value2); }

            try
            {
                while (type != 0)
                {
                    string? name = ValueTypeInspector.NameOfType(type);
                    if (name is not null) chain.Add(name);

                    nint baseType = Out(type, TypeGetBase);
                    RuntimeNavigation.Release(type);
                    type = baseType;
                }
            }
            finally { if (type != 0) RuntimeNavigation.Release(type); }
        }
        finally { RuntimeNavigation.Release(value); }

        return chain;
    }
}
