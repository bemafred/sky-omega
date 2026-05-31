// Enumerate the instance fields of an object value, with optional recursive depth — the
// engine half of drhook_locals. Chain: QI ICorDebugValue2 → GetExactType@3 → ICorDebugType
// loop via GetBase@7. At each Type level: GetClass@4 → (module, mdTypeDef); enumerate fields
// via IMetaDataImport.EnumFields@20 + GetFieldProps@57 (name only); read each field value via
// ICorDebugObjectValue.GetFieldValue@8 with the level's class; render via Variables.ReadValue.
//
// Inherited fields surface because GetBase walks the hierarchy — same primitive as
// MemberResolver (probe 37) and ExceptionInspector (probe 38). System.Object's internal fields
// (m_handle, etc.) ARE enumerated here; consumers can filter by name if they want a cleaner view.
//
// Recursion: when depth > 1 and a field is itself an object reference (ElementType = 0x12
// CLASS or 0x1C OBJECT), the field's own Fields list is populated by recursing with depth - 1.
// Strings (0x0E) and arrays (0x14 / 0x1D) are NOT recursed into here — strings are rendered via
// StringValue (finding 44); arrays are a separate slice.
//
// Slots verified from cordebug.idl (ICorDebug*) and cor.h (IMetaDataImport — probe 39 cross-check).
// PRECONDITION: process synchronized (called at a stop).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class FieldEnumerator
{
    private static readonly Guid IID_ICorDebugValue2          = new("5E0B54E7-D88A-4626-9420-A691E0A78B49");
    private static readonly Guid IID_ICorDebugReferenceValue  = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");
    private static readonly Guid IID_ICorDebugObjectValue     = new("18AD3D6E-B7D2-11D2-BD04-0000F80849BD");
    private static readonly Guid IID_IMetaDataImport          = new("7DAC8207-D3AE-4C75-9B67-92801A497D44");

    private const int Value2GetExactType         = 3;   // ICorDebugValue2
    private const int TypeGetClass               = 4;   // ICorDebugType
    private const int TypeGetBase                = 7;
    private const int ClassGetModule             = 3;   // ICorDebugClass
    private const int ClassGetToken              = 4;
    private const int ReferenceValueDereference  = 10;  // ICorDebugReferenceValue
    private const int ObjectValueGetFieldValue   = 8;   // ICorDebugObjectValue
    private const int ModuleGetMetaDataInterface = 14;  // ICorDebugModule
    // IMetaDataImport (cor.h vtable order)
    private const int CloseEnum     = 3;
    private const int EnumFields    = 20;
    private const int GetFieldProps = 57;

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

    /// <summary>Enumerate the instance fields of <paramref name="pValue"/> (a reference to an
    /// object), walking inherited fields up the type chain via <c>GetBase</c>. Recurses into
    /// object-typed fields when <paramref name="depth"/> &gt; 1. Returns an empty list if the
    /// value is not an object (e.g. a string, an array, or a non-reference) — caller can rely
    /// on this being safe to call on any value.</summary>
    public static IReadOnlyList<FieldValue> GetFields(nint pValue, int depth)
    {
        if (pValue == 0 || depth <= 0) return Array.Empty<FieldValue>();

        nint value2 = QueryInterface(pValue, IID_ICorDebugValue2);
        if (value2 == 0) return Array.Empty<FieldValue>();
        nint type;
        try { type = Out(value2, Value2GetExactType); }
        finally { RuntimeNavigation.Release(value2); }
        if (type == 0) return Array.Empty<FieldValue>();

        // ICorDebugObjectValue lives on the dereferenced HEAP value, not on the reference.
        // If pValue is a ReferenceValue (locals/args of an object type usually are), Dereference
        // first; if pValue is already a heap value (e.g. a dereferenced field), the QI succeeds
        // without the indirection.
        nint objectValue = QueryInterface(pValue, IID_ICorDebugObjectValue);
        nint dereferenced = 0;
        if (objectValue == 0)
        {
            nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
            if (reference == 0) { RuntimeNavigation.Release(type); return Array.Empty<FieldValue>(); }
            try { dereferenced = Out(reference, ReferenceValueDereference); }
            finally { RuntimeNavigation.Release(reference); }
            if (dereferenced == 0) { RuntimeNavigation.Release(type); return Array.Empty<FieldValue>(); }
            objectValue = QueryInterface(dereferenced, IID_ICorDebugObjectValue);
            if (objectValue == 0)
            {
                RuntimeNavigation.Release(dereferenced);
                RuntimeNavigation.Release(type);
                return Array.Empty<FieldValue>();
            }
        }

        List<FieldValue> fields = new();
        try
        {
            while (type != 0)
            {
                AppendFieldsAtLevel(objectValue, type, depth, fields);
                nint baseType = Out(type, TypeGetBase);
                RuntimeNavigation.Release(type);
                type = baseType;
            }
        }
        finally
        {
            if (type != 0) RuntimeNavigation.Release(type);
            RuntimeNavigation.Release(objectValue);
            if (dereferenced != 0) RuntimeNavigation.Release(dereferenced);
        }

        return fields;
    }

    private static void AppendFieldsAtLevel(nint objectValue, nint type, int depth, List<FieldValue> output)
    {
        nint klass = Out(type, TypeGetClass);
        if (klass == 0) return;
        try
        {
            nint module = Out(klass, ClassGetModule);
            if (module == 0) return;
            try
            {
                uint typeToken;
                if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(klass, ClassGetToken))(klass, &typeToken) < 0)
                    return;

                nint pImport = GetMetaDataImport(module);
                if (pImport == 0) return;
                try
                {
                    EnumAndReadFields(pImport, klass, typeToken, objectValue, depth, output);
                }
                finally { ReleaseImport(pImport); }
            }
            finally { RuntimeNavigation.Release(module); }
        }
        finally { RuntimeNavigation.Release(klass); }
    }

    private static void EnumAndReadFields(nint pImport, nint klass, uint typeToken, nint objectValue, int depth, List<FieldValue> output)
    {
        var enumFields = (delegate* unmanaged[Cdecl]<nint, nint*, uint, uint*, uint, uint*, int>)Slot(pImport, EnumFields);
        var closeEnum  = (delegate* unmanaged[Cdecl]<nint, nint, void>)Slot(pImport, CloseEnum);
        var getFieldValue = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint*, int>)Slot(objectValue, ObjectValueGetFieldValue);

        nint hEnum = 0;
        uint[] tokens = new uint[16];
        try
        {
            while (true)
            {
                uint fetched = 0;
                fixed (uint* pTokens = tokens)
                {
                    if (enumFields(pImport, &hEnum, typeToken, pTokens, (uint)tokens.Length, &fetched) < 0 || fetched == 0)
                        break;
                }

                for (int i = 0; i < fetched; i++)
                {
                    uint fieldToken = tokens[i];
                    string name = GetFieldName(pImport, fieldToken);
                    if (name.Length == 0) continue;

                    nint fieldValue;
                    if (getFieldValue(objectValue, klass, fieldToken, &fieldValue) < 0 || fieldValue == 0)
                        continue; // static field, or otherwise unavailable on this instance

                    try
                    {
                        ArgumentValue v = Variables.ReadValue(fieldValue);
                        // Recurse via the shared dispatcher so a field whose value is itself an
                        // ARRAY (not just an object) gets expanded too — composes probe 39 + 40.
                        IReadOnlyList<FieldValue>? nested = depth > 1
                            ? Variables.GetChildren(fieldValue, v.ElementType, depth - 1)
                            : null;
                        output.Add(new FieldValue(name, v.ElementType, v.RawValue, v.StringValue, nested));
                    }
                    finally { RuntimeNavigation.Release(fieldValue); }
                }
            }
        }
        finally { closeEnum(pImport, hEnum); }
    }

    private static string GetFieldName(nint pImport, uint fieldToken)
    {
        // GetFieldProps(mb, &class, name, cchName, &pchName, &attr, &sig, &cbSig, &cplusType, &value, &pcchValue)
        var getFieldProps = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, uint, uint*, uint*, nint*, uint*, uint*, nint*, uint*, int>)Slot(pImport, GetFieldProps);
        uint declClass = 0, pchName = 0, attr = 0, cbSig = 0, cplusType = 0, pcchValue = 0;
        nint sig = 0, value = 0;
        char* nameBuf = stackalloc char[256];
        if (getFieldProps(pImport, fieldToken, &declClass, nameBuf, 256, &pchName, &attr, &sig, &cbSig, &cplusType, &value, &pcchValue) < 0)
            return "";
        int len = pchName > 0 ? (int)pchName - 1 : 0; // -1 strips the trailing nul written by the API
        return len > 0 ? new string(nameBuf, 0, len) : "";
    }

    private static nint GetMetaDataImport(nint pModule)
    {
        var getMetaData = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pModule, ModuleGetMetaDataInterface);
        Guid iid = IID_IMetaDataImport;
        nint pImport;
        return getMetaData(pModule, &iid, &pImport) < 0 ? 0 : pImport;
    }

    private static void ReleaseImport(nint pUnk)
        => ((delegate* unmanaged[Cdecl]<nint, uint>)Slot(pUnk, 2))(pUnk);
}
