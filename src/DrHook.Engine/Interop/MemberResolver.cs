// Resolve a member (property getter) on the RUNTIME TYPE of a value — so a C# expression like
// `box.Size` resolves `get_Size` on box's actual class with no hardcoded type/module. Chain:
// value → ICorDebugReferenceValue.Dereference → ICorDebugObjectValue.GetClass → ICorDebugClass
// .GetModule/.GetToken → metadata FindMethodInType("get_<member>") → ICorDebugFunction. Raw V-table
// (slots + IIDs from cordebug.idl), validated by probe 24.
//
// SCOPE: plain reference objects. Strings (ICorDebugStringValue) and other non-ICorDebugObjectValue
// kinds aren't reachable via ObjectValue.GetClass — they need ICorDebugValue2.GetExactType /
// ICorDebugType (a follow-on). Returns 0 for those, so the caller can fall back / report.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class MemberResolver
{
    private static readonly Guid IID_ICorDebugReferenceValue = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");
    private static readonly Guid IID_ICorDebugObjectValue = new("18AD3D6E-B7D2-11D2-BD04-0000F80849BD");

    private const int ReferenceValueDereference = 10; // ICorDebugReferenceValue
    private const int ObjectValueGetClass = 7;        // ICorDebugObjectValue
    private const int ClassGetModule = 3;             // ICorDebugClass
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

    /// <summary>Resolve the property getter <c>get_<paramref name="memberName"/></c> on the runtime
    /// type of <paramref name="pThisValue"/>, as an owned <c>ICorDebugFunction</c> (caller releases).
    /// 0 if the value isn't a resolvable reference object or the member isn't found.</summary>
    public static nint ResolveGetter(nint pThisValue, string memberName)
    {
        nint reference = QueryInterface(pThisValue, IID_ICorDebugReferenceValue);
        if (reference == 0) return 0; // not a reference value
        nint obj;
        try { obj = Out(reference, ReferenceValueDereference); }
        finally { RuntimeNavigation.Release(reference); }
        if (obj == 0) return 0;

        try
        {
            nint objectValue = QueryInterface(obj, IID_ICorDebugObjectValue);
            if (objectValue == 0) return 0; // not a plain object (e.g. string) — needs ICorDebugType
            try
            {
                nint klass = Out(objectValue, ObjectValueGetClass);
                if (klass == 0) return 0;
                try
                {
                    nint module = Out(klass, ClassGetModule);
                    if (module == 0) return 0;
                    try
                    {
                        uint typeToken;
                        if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(klass, ClassGetToken))(klass, &typeToken) < 0)
                            return 0;

                        uint methodToken = MetadataResolver.FindMethodInType(module, typeToken, "get_" + memberName);
                        if (methodToken == 0) return 0;

                        return Eval.GetFunction(module, methodToken); // owned ICorDebugFunction
                    }
                    finally { RuntimeNavigation.Release(module); }
                }
                finally { RuntimeNavigation.Release(klass); }
            }
            finally { RuntimeNavigation.Release(objectValue); }
        }
        finally { RuntimeNavigation.Release(obj); }
    }
}
