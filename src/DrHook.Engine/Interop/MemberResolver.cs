// Resolve a member (property getter) on the RUNTIME TYPE of a value, walking inheritance
// across MODULES — so an expression like `box.Size` resolves `get_Size` on box's actual class,
// and `ex.Message` resolves `get_Message` on System.Exception in CoreLib even though the runtime
// type lives in the user's module.
//
// Chain: value → ICorDebugValue2.GetExactType@3 → ICorDebugType; then a loop:
//   GetClass@4 → ICorDebugClass → GetModule@3 / GetToken@4 → (module, mdTypeDef);
//   MetadataResolver.FindMethodInType (handles same-module extends walking, probe 36) →
//     if found, get an ICorDebugFunction and return;
//   else ICorDebugType.GetBase@7 → next Type (may live in a DIFFERENT module, handled by the
//     runtime — no manual mdTypeRef resolution needed); loop.
//
// Slots + IIDs verified from cordebug.idl (probes 36, 37). This GetExactType-based approach
// supersedes the previous Reference→Dereference→ObjectValue chain (which couldn't reach strings
// or arrays and didn't cross module boundaries). Probes 24/25/27/30/35 still pass through this
// path — GetExactType returns the same Class for plain-object cases.
//
// PRECONDITION: process synchronized (called at a stop).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class MemberResolver
{
    private static readonly Guid IID_ICorDebugValue2 = new("5E0B54E7-D88A-4626-9420-A691E0A78B49");

    private const int Value2GetExactType = 3;   // ICorDebugValue2
    private const int TypeGetClass       = 4;   // ICorDebugType
    private const int TypeGetBase        = 7;
    private const int ClassGetModule     = 3;   // ICorDebugClass
    private const int ClassGetToken      = 4;

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
    /// type of <paramref name="pThisValue"/> (walking inherited members across modules), as an owned
    /// <c>ICorDebugFunction</c> (caller releases). Returns 0 if the value has no exact type or no
    /// ancestor declares the member.</summary>
    public static nint ResolveGetter(nint pThisValue, string memberName)
    {
        if (pThisValue == 0) return 0;
        string getterName = "get_" + memberName;

        nint value2 = QueryInterface(pThisValue, IID_ICorDebugValue2);
        if (value2 == 0) return 0;
        nint type;
        try { type = Out(value2, Value2GetExactType); }
        finally { RuntimeNavigation.Release(value2); }
        if (type == 0) return 0;

        try
        {
            while (type != 0)
            {
                nint function = TryFindOnTypeLevel(type, getterName);
                if (function != 0) return function;

                nint baseType = Out(type, TypeGetBase);
                RuntimeNavigation.Release(type);
                type = baseType;
            }
            return 0;
        }
        finally
        {
            if (type != 0) RuntimeNavigation.Release(type);
        }
    }

    /// <summary>One level of the GetBase walk: get the ICorDebugClass for this type, find the
    /// (module, mdTypeDef) it lives in, search via <see cref="MetadataResolver.FindMethodInType"/>
    /// (which itself walks same-module extends). Returns an owned <c>ICorDebugFunction</c> on hit,
    /// 0 on miss.</summary>
    private static nint TryFindOnTypeLevel(nint type, string getterName)
    {
        nint klass = Out(type, TypeGetClass);
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

                uint methodToken = MetadataResolver.FindMethodInType(module, typeToken, getterName);
                if (methodToken == 0) return 0;

                return Eval.GetFunction(module, methodToken); // owned ICorDebugFunction
            }
            finally { RuntimeNavigation.Release(module); }
        }
        finally { RuntimeNavigation.Release(klass); }
    }
}
