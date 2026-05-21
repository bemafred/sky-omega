// Resolve a method name to an mdMethodDef token via IMetaDataImport — the metadata API behind
// a module. Raw V-table calls (slot numbers from cor.h order), validated by probe 11. The one
// unavoidable GUID is IID_IMetaDataImport, which GetMetaDataInterface takes as INPUT; it is a
// stable, well-known metadata IID and a wrong value fails cleanly with E_NOINTERFACE.
//
// PRECONDITION: process synchronized (called at a stop). Metadata itself is static (present
// whether or not the type is JIT-loaded), so a type can be resolved before it is instantiated.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class MetadataResolver
{
    private static readonly Guid IID_IMetaDataImport = new("7DAC8207-D3AE-4C75-9B67-92801A497D44");

    private const int ModuleGetMetaDataInterface = 14; // ICorDebugModule (IUnknown 0-2, own 3-)

    // IMetaDataImport slots (IUnknown 0-2 + cor.h order): CloseEnum(3), CountEnum(4), ResetEnum(5),
    // EnumTypeDefs(6), …, FindTypeDefByName(9), …, GetTypeDefProps(12), …, EnumMethods(18),
    // EnumMethodsWithName(19), …, GetMethodProps(30), …
    private const int CloseEnum = 3;
    private const int FindTypeDefByName = 9;
    private const int EnumMethodsWithName = 19;

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static uint Release(nint pUnk)
        => ((delegate* unmanaged[Cdecl]<nint, uint>)Slot(pUnk, 2))(pUnk);

    /// <summary>Resolve <paramref name="typeName"/>.<paramref name="methodName"/> in
    /// <paramref name="pModule"/>'s metadata to an <c>mdMethodDef</c> token; 0 if not found.</summary>
    public static uint ResolveMethodToken(nint pModule, string typeName, string methodName)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return 0;
        try
        {
            // IMetaDataImport.FindTypeDefByName(LPCWSTR szTypeDef, mdToken tkEnclosing, mdTypeDef* ptd)
            var findType = (delegate* unmanaged[Cdecl]<nint, char*, uint, uint*, int>)Slot(pImport, FindTypeDefByName);
            uint typeToken;
            fixed (char* pType = typeName)
            {
                if (findType(pImport, pType, 0, &typeToken) < 0) return 0;
            }

            // IMetaDataImport.EnumMethodsWithName(HCORENUM* phEnum, mdTypeDef cl, LPCWSTR szName,
            //   mdMethodDef rMethods[], ULONG cMax, ULONG* pcTokens)
            var enumWithName = (delegate* unmanaged[Cdecl]<nint, nint*, uint, char*, uint*, uint, uint*, int>)Slot(pImport, EnumMethodsWithName);
            var closeEnum = (delegate* unmanaged[Cdecl]<nint, nint, void>)Slot(pImport, CloseEnum);

            nint hEnum = 0;
            uint token = 0;
            uint fetched = 0;
            fixed (char* pName = methodName)
            {
                if (enumWithName(pImport, &hEnum, typeToken, pName, &token, 1, &fetched) < 0 || fetched == 0)
                    token = 0;
            }
            closeEnum(pImport, hEnum);
            return token;
        }
        finally { Release(pImport); }
    }

    private static nint GetMetaDataImport(nint pModule)
    {
        // ICorDebugModule.GetMetaDataInterface(REFIID riid, IUnknown** ppObj)
        var getMetaData = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pModule, ModuleGetMetaDataInterface);
        Guid iid = IID_IMetaDataImport;
        nint pImport;
        return getMetaData(pModule, &iid, &pImport) < 0 ? 0 : pImport;
    }
}
