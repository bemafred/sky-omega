// Resolve the RUNTIME TYPE NAME of a value (an object / array / value-type reference) and whether a
// reference value is null — the substrate source for a view to render `this` as `{Worker}` (not a bare
// placeholder) and a null reference as `null`.
//
// Chain (same primitive as MemberResolver / ExceptionInspector, probe 37):
//   value → ICorDebugValue2.GetExactType@3 → ICorDebugType → GetClass@4 → ICorDebugClass →
//   GetModule@3 / GetToken@4 → (module, mdTypeDef) → MetadataResolver.TypeNameFromToken.
// Null detection: ICorDebugReferenceValue.IsNull@7 (slot order: GetType/GetSize/GetAddress/CreateBreakpoint
// from ICorDebugValue = 3..6, then IsNull=7, GetValue=8, SetValue=9, Dereference=10). All are metadata /
// flag reads — NO value-byte copy — so this is safe for large value types (ADR-014).
//
// NameOfType is the one shared "ICorDebugType → type name" step, reused by ExceptionInspector's base-chain
// walk so there is a single implementation, not a forked copy. PRECONDITION: process synchronized.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ValueTypeInspector
{
    private static readonly Guid IID_ICorDebugValue2          = new("5E0B54E7-D88A-4626-9420-A691E0A78B49");
    private static readonly Guid IID_ICorDebugReferenceValue  = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");

    private const int ReferenceValueIsNull = 7;   // ICorDebugReferenceValue
    private const int Value2GetExactType   = 3;   // ICorDebugValue2
    private const int TypeGetClass         = 4;   // ICorDebugType
    private const int ClassGetModule       = 3;   // ICorDebugClass
    private const int ClassGetToken        = 4;

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

    /// <summary>The runtime type name + null-ness of a value. <see cref="TypeName"/> is the type of a
    /// non-null object / array / value-type reference (null when unresolved or for a null/primitive value);
    /// <see cref="IsNullReference"/> is true when the value is a reference type whose reference is null.</summary>
    internal readonly record struct TypeInfo(string? TypeName, bool IsNullReference);

    /// <summary>Resolve <paramref name="pValue"/>'s runtime type name and null-ness. Reference element types
    /// (CLASS / OBJECT / ARRAY / SZARRAY) are null-checked first — a null reference returns
    /// <c>(null, true)</c> with no type walk. A non-null reference or a value type returns its resolved type
    /// name. Cheap, fault-contained (any failed step yields a null name, never throws).</summary>
    public static TypeInfo Read(nint pValue, int elementType)
    {
        if (pValue == 0) return new TypeInfo(null, false);
        if (IsReference(elementType) && IsNull(pValue)) return new TypeInfo(null, IsNullReference: true);
        return new TypeInfo(RuntimeTypeName(pValue), false);
    }

    // Reference (heap) element types — the ones that can be null. VALUETYPE (0x11) and BYREF (0x10) are not.
    private static bool IsReference(int elementType)
        => elementType is 0x12 /* CLASS */ or 0x1C /* OBJECT */ or 0x14 /* ARRAY */ or 0x1D /* SZARRAY */;

    private static bool IsNull(nint pValue)
    {
        nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
        if (reference == 0) return false; // not a reference value (shouldn't happen for IsReference types) — treat as non-null
        try
        {
            int isNull;
            return ((delegate* unmanaged[Cdecl]<nint, int*, int>)Slot(reference, ReferenceValueIsNull))(reference, &isNull) >= 0
                   && isNull != 0;
        }
        finally { RuntimeNavigation.Release(reference); }
    }

    private static string? RuntimeTypeName(nint pValue)
    {
        nint value2 = QueryInterface(pValue, IID_ICorDebugValue2);
        if (value2 == 0) return null;
        nint type;
        try { type = Out(value2, Value2GetExactType); }
        finally { RuntimeNavigation.Release(value2); }
        if (type == 0) return null;
        try { return NameOfType(type); }
        finally { RuntimeNavigation.Release(type); }
    }

    /// <summary>The metadata type name of an <c>ICorDebugType</c>: GetClass → (module, mdTypeDef) →
    /// <see cref="MetadataResolver.TypeNameFromToken"/>. Null if any step fails. The single shared
    /// "type → name" step (also used by <see cref="ExceptionInspector"/>'s base-chain walk).</summary>
    internal static string? NameOfType(nint type)
    {
        nint klass = Out(type, TypeGetClass);
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
}
