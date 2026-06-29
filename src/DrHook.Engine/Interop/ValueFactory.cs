// Construct eval-time VALUES that CreateInt32 / NewString / NewObject cannot: a default-initialized value of a
// generic value type — specifically Nullable<int> (= default(int?) = null, HasValue=false), the value an
// optional `int? quality = null` parameter (Avalonia's Bitmap.Save(string, int?)) needs. A plain I4 passed where a
// Nullable<int> is expected does NOT fail cleanly — it size-mismatches the managed call and WEDGES mscordbi (the
// eval resumes the target, never re-stops, and WaitForStop's timeout does not fire). A correctly-typed value
// avoids that. Built via ICorDebugEval2.CreateValueForType over an ICorDebugType assembled from the generic class
// and its type argument (ICorDebugClass2.GetParameterizedType). Slots from cordebug.idl; a wrong slot/IID fails
// cleanly here (0) rather than corrupting a call.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ValueFactory
{
    private const int ModuleGetClassFromToken    = 11; // ICorDebugModule (… GetFunctionFromToken9, GetClassFromToken11, …, GetMetaDataInterface14)
    private const int Class2GetParameterizedType = 3;  // ICorDebugClass2 (IUnknown 0-2, GetParameterizedType3)
    private const int Eval2CreateValueForType    = 4;  // ICorDebugEval2 (CallParameterizedFunction3, CreateValueForType4, …)
    private const int ELEMENT_TYPE_VALUETYPE     = 0x11;

    private static readonly Guid IID_ICorDebugClass2 = new("B008EA8D-7AB1-43F7-BB20-FBB5A04038AE");
    private static readonly Guid IID_ICorDebugEval2  = new("FB0D9CE7-BE66-4683-9D32-A42A04E2FD91");

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static nint QueryInterface(nint pUnk, Guid iid)
    {
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pUnk, 0);
        nint result;
        return qi(pUnk, &iid, &result) < 0 ? 0 : result;
    }

    private static nint ClassOf(nint pModule, uint typeToken)
    {
        var get = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(pModule, ModuleGetClassFromToken);
        nint pClass;
        return get(pModule, typeToken, &pClass) < 0 ? 0 : pClass;
    }

    // The ICorDebugType for a class: GetParameterizedType(elementType, typeArgs). A non-generic value type
    // (System.Int32) takes 0 args; a generic one (Nullable`1) takes its instantiation (e.g. [Int32-type]).
    private static nint ParameterizedType(nint pClass, ReadOnlySpan<nint> typeArgs)
    {
        nint pClass2 = QueryInterface(pClass, IID_ICorDebugClass2);
        if (pClass2 == 0) return 0;
        try
        {
            var get = (delegate* unmanaged[Cdecl]<nint, int, uint, nint*, nint*, int>)Slot(pClass2, Class2GetParameterizedType);
            nint type;
            fixed (nint* args = typeArgs)
                return get(pClass2, ELEMENT_TYPE_VALUETYPE, (uint)typeArgs.Length, args, &type) < 0 ? 0 : type;
        }
        finally { RuntimeNavigation.Release(pClass2); }
    }

    /// <summary>Create a default-initialized <c>System.Nullable&lt;int&gt;</c> value (= <c>default(int?)</c> —
    /// HasValue false) on <paramref name="pEval"/>, the value an optional <c>int? quality = null</c> argument
    /// needs. Resolves <c>System.Int32</c> and <c>System.Nullable`1</c> in <paramref name="pProcess"/>'s CoreLib,
    /// builds the <c>Nullable&lt;int&gt;</c> <c>ICorDebugType</c>, and calls <c>ICorDebugEval2.CreateValueForType</c>
    /// (a value type yields a zero-initialized value — exactly the empty Nullable). 0 on any failure. Owned; the
    /// caller releases.</summary>
    public static nint CreateDefaultNullableInt32(nint pEval, nint pProcess)
    {
        nint pCore = RuntimeNavigation.FindModule(pProcess, "System.Private.CoreLib");
        if (pCore == 0) return 0;
        nint int32Class = 0, nullableClass = 0, int32Type = 0, nullableType = 0, pEval2 = 0;
        try
        {
            uint int32Token = MetadataResolver.ResolveTypeToken(pCore, "System.Int32");
            uint nullableToken = MetadataResolver.ResolveTypeToken(pCore, "System.Nullable`1");
            if (int32Token == 0 || nullableToken == 0) return 0;

            int32Class = ClassOf(pCore, int32Token);
            nullableClass = ClassOf(pCore, nullableToken);
            if (int32Class == 0 || nullableClass == 0) return 0;

            int32Type = ParameterizedType(int32Class, ReadOnlySpan<nint>.Empty);
            if (int32Type == 0) return 0;

            Span<nint> typeArg = stackalloc nint[1];
            typeArg[0] = int32Type;
            nullableType = ParameterizedType(nullableClass, typeArg);
            if (nullableType == 0) return 0;

            pEval2 = QueryInterface(pEval, IID_ICorDebugEval2);
            if (pEval2 == 0) return 0;
            var create = (delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)Slot(pEval2, Eval2CreateValueForType);
            nint value;
            return create(pEval2, nullableType, &value) < 0 ? 0 : value;
        }
        finally
        {
            if (pEval2 != 0) RuntimeNavigation.Release(pEval2);
            if (nullableType != 0) RuntimeNavigation.Release(nullableType);
            if (int32Type != 0) RuntimeNavigation.Release(int32Type);
            if (nullableClass != 0) RuntimeNavigation.Release(nullableClass);
            if (int32Class != 0) RuntimeNavigation.Release(int32Class);
            RuntimeNavigation.Release(pCore);
        }
    }
}
