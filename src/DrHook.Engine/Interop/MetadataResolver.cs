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
    private const int GetTypeDefProps = 12;
    private const int EnumMethodsWithName = 19;
    private const int EnumParams = 22;
    private const int GetMethodProps = 30;
    private const int GetParamProps = 59;
    private const uint mdStatic = 0x0010; // CorMethodAttr — a static method has no `this` receiver

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

    /// <summary>Reverse of <see cref="ResolveMethodToken"/>: an <c>mdMethodDef</c> token →
    /// "Type.Method" (or the token hex if metadata is unavailable). Used to name stack frames.</summary>
    public static string MethodName(nint pModule, uint methodToken)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return $"0x{methodToken:X8}";
        try
        {
            // IMetaDataImport.GetMethodProps(mb, mdTypeDef* pClass, LPWSTR szMethod, ULONG cch,
            //   ULONG* pch, DWORD* pdwAttr, PCCOR_SIGNATURE* ppvSig, ULONG* pcbSig, ULONG* pulRVA, DWORD* pdwImpl)
            var getMethodProps = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, uint, uint*, uint*, nint*, uint*, uint*, uint*, int>)Slot(pImport, GetMethodProps);
            uint classToken = 0, chName = 0, attr = 0, cbSig = 0, rva = 0, impl = 0;
            nint sig = 0;
            char* nameBuf = stackalloc char[512];
            if (getMethodProps(pImport, methodToken, &classToken, nameBuf, 512, &chName, &attr, &sig, &cbSig, &rva, &impl) < 0)
                return $"0x{methodToken:X8}";

            string method = ToName(nameBuf, chName);
            string type = TypeName(pImport, classToken);
            return type.Length == 0 ? method : $"{type}.{method}";
        }
        finally { Release(pImport); }
    }

    /// <summary>Ordered argument names of an <c>mdMethodDef</c> from the LOADED module's metadata via
    /// IMetaDataImport — the bundle-resident counterpart to the file-based
    /// <see cref="MethodMetadata.ArgumentNames"/>, for a single-file app whose assembly has no on-disk
    /// PE. Aligned to <c>ICorDebugILFrame.GetArgument(i)</c>: index 0 is <c>this</c> for an instance
    /// method (from the method's <c>mdStatic</c> attribute), then declared parameters by sequence
    /// number. Empty entries (no name) fall back positionally at the caller. Empty list if metadata is
    /// unavailable or the token is not a method.</summary>
    public static IReadOnlyList<string> ArgumentNames(nint pModule, uint methodToken)
    {
        if ((methodToken >> 24) != 0x06) return Array.Empty<string>();
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return Array.Empty<string>();
        try
        {
            // HasThis: the method's attributes carry mdStatic. GetMethodProps' signature matches MethodName's.
            var getMethodProps = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, uint, uint*, uint*, nint*, uint*, uint*, uint*, int>)Slot(pImport, GetMethodProps);
            uint classToken = 0, chMethod = 0, attr = 0, cbSig = 0, rva = 0, impl = 0;
            nint sig = 0;
            char* methodName = stackalloc char[512]; // name not needed here, but give the API a real buffer
            if (getMethodProps(pImport, methodToken, &classToken, methodName, 512, &chMethod, &attr, &sig, &cbSig, &rva, &impl) < 0)
                return Array.Empty<string>();
            bool hasThis = (attr & mdStatic) == 0;

            // IMetaDataImport.EnumParams(HCORENUM* phEnum, mdMethodDef mb, mdParamDef rParams[], ULONG cMax, ULONG* pcTokens)
            var enumParams = (delegate* unmanaged[Cdecl]<nint, nint*, uint, uint*, uint, uint*, int>)Slot(pImport, EnumParams);
            // IMetaDataImport.GetParamProps(mdParamDef tk, mdMethodDef* pmd, ULONG* pulSequence, LPWSTR szName,
            //   ULONG cchName, ULONG* pchName, DWORD* pdwAttr, DWORD* pdwCPlusTypeFlag, UVCP_CONSTANT* ppValue, ULONG* pcchValue)
            var getParamProps = (delegate* unmanaged[Cdecl]<nint, uint, uint*, uint*, char*, uint, uint*, uint*, uint*, nint*, uint*, int>)Slot(pImport, GetParamProps);
            var closeEnum = (delegate* unmanaged[Cdecl]<nint, nint, void>)Slot(pImport, CloseEnum);

            Dictionary<int, string> bySequence = new();
            int maxSequence = 0;
            nint hEnum = 0;
            uint* paramTokens = stackalloc uint[64];
            uint fetched = 0;
            if (enumParams(pImport, &hEnum, methodToken, paramTokens, 64, &fetched) >= 0)
            {
                char* nameBuf = stackalloc char[512];
                for (uint i = 0; i < fetched; i++)
                {
                    uint pmd = 0, seq = 0, pchName = 0, pAttr = 0, cpType = 0, pcchValue = 0;
                    nint ppValue = 0;
                    if (getParamProps(pImport, paramTokens[i], &pmd, &seq, nameBuf, 512, &pchName, &pAttr, &cpType, &ppValue, &pcchValue) < 0) continue;
                    if (seq == 0) continue; // sequence 0 is the return value, not an argument
                    bySequence[(int)seq] = ToName(nameBuf, pchName);
                    if ((int)seq > maxSequence) maxSequence = (int)seq;
                }
            }
            closeEnum(pImport, hEnum);

            List<string> ordered = new();
            if (hasThis) ordered.Add("this");
            for (int seq = 1; seq <= maxSequence; seq++)
                ordered.Add(bySequence.TryGetValue(seq, out string? name) ? name : "");
            return ordered;
        }
        finally { Release(pImport); }
    }

    /// <summary>The fully-qualified name (e.g. "System.InvalidOperationException") of the type given
    /// by <paramref name="typeToken"/> (an <c>mdTypeDef</c> from <c>ICorDebugClass.GetToken</c>) in
    /// <paramref name="pModule"/>'s metadata; empty string if unavailable. Opens its own import.</summary>
    public static string TypeNameFromToken(nint pModule, uint typeToken)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return "";
        try { return TypeName(pImport, typeToken); }
        finally { Release(pImport); }
    }

    private static string TypeName(nint pImport, uint typeToken)
    {
        if (typeToken == 0) return ""; // global/module-level method
        // IMetaDataImport.GetTypeDefProps(td, LPWSTR szTypeDef, ULONG cch, ULONG* pch, DWORD* pFlags, mdToken* ptkExtends)
        var getTypeDefProps = (delegate* unmanaged[Cdecl]<nint, uint, char*, uint, uint*, uint*, uint*, int>)Slot(pImport, GetTypeDefProps);
        uint chName = 0, flags = 0, extends = 0;
        char* nameBuf = stackalloc char[512];
        return getTypeDefProps(pImport, typeToken, nameBuf, 512, &chName, &flags, &extends) < 0
            ? ""
            : ToName(nameBuf, chName);
    }

    private static string ToName(char* buffer, uint countWithNul)
        => new string(buffer, 0, countWithNul > 0 ? (int)countWithNul - 1 : 0);

    /// <summary>Find a method named <paramref name="methodName"/> on the type given by
    /// <paramref name="typeToken"/> (an <c>mdTypeDef</c> from <c>ICorDebugClass.GetToken</c>) →
    /// <c>mdMethodDef</c>; 0 if not found. Unlike <see cref="ResolveMethodToken"/> this takes the
    /// type token directly (for runtime-class member resolution), skipping <c>FindTypeDefByName</c>.
    ///
    /// Walks the <c>extends</c> chain WITHIN the same module so inherited members on a base class
    /// declared in the same assembly are findable (finding 45). Cross-module bases (<c>mdTypeRef</c>
    /// → another assembly, e.g. <c>System.Exception</c> in CoreLib) are NOT followed — that's the
    /// next object-inspection slice. The walk stops at the first match or when the chain leaves
    /// the module.</summary>
    public static uint FindMethodInType(nint pModule, uint typeToken, string methodName)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return 0;
        try
        {
            uint currentType = typeToken;
            while (currentType != 0)
            {
                uint methodToken = FindMethodInTypeDirectly(pImport, currentType, methodName);
                if (methodToken != 0) return methodToken;
                currentType = GetBaseTypeDef(pImport, currentType);
            }
            return 0;
        }
        finally { Release(pImport); }
    }

    private static uint FindMethodInTypeDirectly(nint pImport, uint typeToken, string methodName)
    {
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

    /// <summary>Within-module base typedef token of <paramref name="typeToken"/>, or 0 when the
    /// type has no base, when the base is cross-module (<c>mdTypeRef</c>), or when it is a generic
    /// instantiation (<c>mdTypeSpec</c>). <c>System.Object</c>'s extends is nil; user-defined types
    /// whose parent lives in CoreLib bottom out here too in this slice.</summary>
    private static uint GetBaseTypeDef(nint pImport, uint typeToken)
    {
        // GetTypeDefProps with szTypeDef = null + cchTypeDef = 0 — we only want ptkExtends.
        var getTypeDefProps = (delegate* unmanaged[Cdecl]<nint, uint, char*, uint, uint*, uint*, uint*, int>)Slot(pImport, GetTypeDefProps);
        uint chName = 0, flags = 0, extends = 0;
        if (getTypeDefProps(pImport, typeToken, null, 0, &chName, &flags, &extends) < 0) return 0;
        // Walk only if the base is an mdTypeDef (high byte 0x02) — same module. mdTypeRef (0x01)
        // and mdTypeSpec (0x1B) are deferred to the cross-module / generic slices.
        return (extends >> 24) == 0x02 ? extends : 0;
    }

    /// <summary>Resolve <paramref name="typeName"/> (namespace-qualified, e.g. <c>System.Nullable`1</c>) in
    /// <paramref name="pModule"/>'s metadata to its <c>mdTypeDef</c> token; 0 if not found. The type need not be
    /// JIT-loaded — metadata is static.</summary>
    public static uint ResolveTypeToken(nint pModule, string typeName)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return 0;
        try
        {
            var findType = (delegate* unmanaged[Cdecl]<nint, char*, uint, uint*, int>)Slot(pImport, FindTypeDefByName);
            uint typeToken;
            fixed (char* pType = typeName)
                return findType(pImport, pType, 0, &typeToken) < 0 ? 0 : typeToken;
        }
        finally { Release(pImport); }
    }

    /// <summary>Resolve an OVERLOADED method/ctor on <paramref name="typeName"/> (or an inherited one,
    /// walking the within-module base chain like <see cref="FindMethodInType"/>) to the <c>mdMethodDef</c>
    /// whose parameter list matches <paramref name="paramCount"/> and — when
    /// <paramref name="firstParamElementType"/> is non-zero — whose FIRST parameter's leading
    /// <c>CorElementType</c> equals it. <see cref="ResolveMethodToken"/> returns the first by-name match only,
    /// which is ambiguous for overloads: RenderTargetBitmap's 1- vs 2-arg <c>.ctor</c> (disambiguated by count)
    /// and Bitmap's <c>Save(string)</c> vs <c>Save(Stream)</c> (both 1 param — disambiguated by the STRING
    /// element type). 0 if no candidate matches. Parses the method signature blob (ECMA-335 II.23.2.1).</summary>
    public static uint ResolveOverload(nint pModule, string typeName, string methodName, int paramCount, int firstParamElementType)
    {
        nint pImport = GetMetaDataImport(pModule);
        if (pImport == 0) return 0;
        try
        {
            var findType = (delegate* unmanaged[Cdecl]<nint, char*, uint, uint*, int>)Slot(pImport, FindTypeDefByName);
            uint typeToken;
            fixed (char* pType = typeName)
                if (findType(pImport, pType, 0, &typeToken) < 0) return 0;

            uint current = typeToken;
            while (current != 0)
            {
                uint match = MatchOverloadInType(pImport, current, methodName, paramCount, firstParamElementType);
                if (match != 0) return match;
                current = GetBaseTypeDef(pImport, current);
            }
            return 0;
        }
        finally { Release(pImport); }
    }

    private static uint MatchOverloadInType(nint pImport, uint typeToken, string methodName, int paramCount, int firstParamElementType)
    {
        var enumWithName = (delegate* unmanaged[Cdecl]<nint, nint*, uint, char*, uint*, uint, uint*, int>)Slot(pImport, EnumMethodsWithName);
        var closeEnum = (delegate* unmanaged[Cdecl]<nint, nint, void>)Slot(pImport, CloseEnum);
        var getMethodProps = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, uint, uint*, uint*, nint*, uint*, uint*, uint*, int>)Slot(pImport, GetMethodProps);

        nint hEnum = 0;
        uint* tokens = stackalloc uint[32];
        uint fetched = 0;
        uint chosen = 0;
        char* nameBuf = stackalloc char[512];
        fixed (char* pName = methodName)
        {
            if (enumWithName(pImport, &hEnum, typeToken, pName, tokens, 32, &fetched) >= 0)
            {
                for (uint i = 0; i < fetched; i++)
                {
                    uint classTok = 0, chName = 0, attr = 0, cbSig = 0, rva = 0, impl = 0;
                    nint sig = 0;
                    if (getMethodProps(pImport, tokens[i], &classTok, nameBuf, 512, &chName, &attr, &sig, &cbSig, &rva, &impl) < 0) continue;
                    if (SignatureMatches((byte*)sig, (int)cbSig, paramCount, firstParamElementType))
                    {
                        chosen = tokens[i];
                        break;
                    }
                }
            }
        }
        closeEnum(pImport, hEnum);
        return chosen;
    }

    // Match a MethodDefSig (ECMA-335 II.23.2.1) by parameter count and, when firstParamElementType != 0, by the
    // leading CorElementType of the first parameter. Best-effort: a return/parameter type the minimal SkipType
    // cannot walk (generics, etc.) yields NO match for that candidate rather than a wrong one.
    private static bool SignatureMatches(byte* sig, int len, int paramCount, int firstParamElementType)
    {
        if (sig == null || len < 2) return false;
        int pos = 0;
        byte callingConv = sig[pos++];
        if ((callingConv & 0x10) != 0) ReadCompressedUInt(sig, ref pos, len); // GENERIC: skip the generic-param count
        int count = (int)ReadCompressedUInt(sig, ref pos, len);
        if (count != paramCount) return false;
        if (!SkipType(sig, ref pos, len)) return false; // the return type
        if (firstParamElementType == 0) return true;     // arity-only match
        if (paramCount == 0 || pos >= len) return false;
        byte et = sig[pos];
        while (et == 0x1f || et == 0x20 || et == 0x45)    // CMOD_REQD / CMOD_OPT / PINNED preceding the type
        {
            pos++;
            if (et != 0x45) ReadCompressedUInt(sig, ref pos, len); // a CMOD carries a token
            if (pos >= len) return false;
            et = sig[pos];
        }
        return et == firstParamElementType;
    }

    private static uint ReadCompressedUInt(byte* p, ref int pos, int len)
    {
        if (pos >= len) return 0;
        byte b0 = p[pos++];
        if ((b0 & 0x80) == 0) return b0;
        if ((b0 & 0xC0) == 0x80)
        {
            if (pos >= len) return 0;
            return (uint)(((b0 & 0x3F) << 8) | p[pos++]);
        }
        if (pos + 3 > len) return 0;
        uint v = (uint)(((b0 & 0x1F) << 24) | (p[pos] << 16) | (p[pos + 1] << 8) | p[pos + 2]);
        pos += 3;
        return v;
    }

    // Advance pos past ONE type in a signature blob. Covers the element types the render-capture targets use
    // (void/primitive/string/object/typedbyref; CLASS/VALUETYPE + token; PTR/BYREF/SZARRAY/PINNED + inner;
    // CMOD + token + inner). Returns false on a shape it does not model (GENERICINST/VAR/MVAR/ARRAY/FNPTR) so the
    // caller skips that candidate rather than mis-parsing it.
    private static bool SkipType(byte* p, ref int pos, int len)
    {
        if (pos >= len) return false;
        byte et = p[pos++];
        switch (et)
        {
            case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
            case 0x08: case 0x09: case 0x0a: case 0x0b: case 0x0c: case 0x0d: case 0x0e:
            case 0x16: case 0x18: case 0x19: case 0x1c:
                return true;
            case 0x11: case 0x12: // VALUETYPE / CLASS <token>
                ReadCompressedUInt(p, ref pos, len);
                return true;
            case 0x0f: case 0x10: case 0x1d: case 0x45: // PTR / BYREF / SZARRAY / PINNED <type>
                return SkipType(p, ref pos, len);
            case 0x1f: case 0x20: // CMOD_REQD / CMOD_OPT <token> <type>
                ReadCompressedUInt(p, ref pos, len);
                return SkipType(p, ref pos, len);
            default:
                return false;
        }
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
