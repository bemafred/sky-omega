// Read the RUNTIME TYPE NAME of the exception currently in flight on a thread — so an exception
// stop (ICorDebugManagedCallback2.Exception) can report "System.InvalidOperationException" without
// any hardcoding. Chain: ICorDebugThread.GetCurrentException → ICorDebugReferenceValue.Dereference
// → ICorDebugObjectValue.GetClass → ICorDebugClass.GetModule/.GetToken → metadata type name. Raw
// V-table (slots + IIDs from cordebug.idl). Exceptions are always reference objects, so the
// ObjectValue path applies (no ICorDebugType needed). PRECONDITION: process synchronized (a stop).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ExceptionInspector
{
    private static readonly Guid IID_ICorDebugReferenceValue = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");
    private static readonly Guid IID_ICorDebugObjectValue = new("18AD3D6E-B7D2-11D2-BD04-0000F80849BD");

    private const int ThreadGetCurrentException = 10;  // ICorDebugThread (IUnknown 0-2, own 3-)
    private const int ReferenceValueDereference = 10;  // ICorDebugReferenceValue
    private const int ObjectValueGetClass = 7;         // ICorDebugObjectValue
    private const int ClassGetModule = 3;              // ICorDebugClass
    private const int ClassGetToken = 4;

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
    /// <see cref="RuntimeNavigation.Release"/>. Caller must be at a stop. Suitable as the <c>this</c>
    /// of a func-eval (the runtime preserves the exception across the eval per cordebug.idl).</summary>
    public static nint CurrentExceptionValue(nint pThread)
        => pThread == 0 ? 0 : Out(pThread, ThreadGetCurrentException);

    /// <summary>The fully-qualified type name of the exception currently being thrown on
    /// <paramref name="pThread"/> (e.g. "System.InvalidOperationException"), or null if there is no
    /// current exception or its type can't be resolved. Caller must be at a stop.</summary>
    public static string? CurrentExceptionTypeName(nint pThread)
    {
        if (pThread == 0) return null;
        nint value = Out(pThread, ThreadGetCurrentException);
        if (value == 0) return null; // no exception in flight
        try
        {
            nint reference = QueryInterface(value, IID_ICorDebugReferenceValue);
            if (reference == 0) return null;
            nint obj;
            try { obj = Out(reference, ReferenceValueDereference); }
            finally { RuntimeNavigation.Release(reference); }
            if (obj == 0) return null;

            try
            {
                nint objectValue = QueryInterface(obj, IID_ICorDebugObjectValue);
                if (objectValue == 0) return null;
                try
                {
                    nint klass = Out(objectValue, ObjectValueGetClass);
                    if (klass == 0) return null;
                    try
                    {
                        nint module = Out(klass, ClassGetModule);
                        if (module == 0) return null;
                        try
                        {
                            uint typeToken;
                            if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(klass, ClassGetToken))(klass, &typeToken) < 0)
                                return null;
                            string name = MetadataResolver.TypeNameFromToken(module, typeToken);
                            return name.Length == 0 ? null : name;
                        }
                        finally { RuntimeNavigation.Release(module); }
                    }
                    finally { RuntimeNavigation.Release(klass); }
                }
                finally { RuntimeNavigation.Release(objectValue); }
            }
            finally { RuntimeNavigation.Release(obj); }
        }
        finally { RuntimeNavigation.Release(value); }
    }
}
