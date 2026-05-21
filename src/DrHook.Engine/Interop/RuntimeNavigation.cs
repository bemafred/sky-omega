// CONSUME-direction navigation of the live object graph: walk an attached process down to its
// modules. Done with RAW V-table calls (slot numbers only — no QueryInterface, no GUIDs), so
// this depends solely on the documented cordebug.idl slot layout (finding 03), not on
// interface IIDs we'd otherwise have to transcribe and risk getting wrong. The slots are
// validated empirically by probe 10.
//
// PRECONDITION: the debuggee must be SYNCHRONIZED (stopped) — ICorDebug inspection requires it.
// Callers reach here only at a stop (after WaitForStop). Every pointer returned by an
// enumerator's Next is an owned (AddRef'd) reference and is Released here.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class RuntimeNavigation
{
    // V-table slots (3 IUnknown + interface methods in IDL order; finding 03 / cordebug.idl).
    private const int ProcessEnumerateAppDomains = 26;   // ICorDebugProcess  (Controller 3-12, own 13-)
    private const int AppDomainEnumerateAssemblies = 14; // ICorDebugAppDomain (Controller 3-12, own 13-)
    private const int AssemblyEnumerateModules = 5;      // ICorDebugAssembly  (IUnknown 0-2, own 3-)
    private const int ModuleGetName = 6;                 // ICorDebugModule    (IUnknown 0-2, own 3-)
    private const int EnumNext = 7;                       // ICorDebug*Enum : ICorDebugEnum (Skip3,Reset4,Clone5,GetCount6,Next7)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static uint Release(nint pUnk)
        => ((delegate* unmanaged[Cdecl]<nint, uint>)Slot(pUnk, 2))(pUnk);

    /// <summary>Names of all modules loaded in the (stopped) target, walking
    /// process → app domains → assemblies → modules.</summary>
    public static List<string> ModuleNames(nint pProcess)
    {
        List<string> names = new();
        foreach (nint appDomain in Drain(pProcess, ProcessEnumerateAppDomains))
        {
            foreach (nint assembly in Drain(appDomain, AppDomainEnumerateAssemblies))
            {
                foreach (nint module in Drain(assembly, AssemblyEnumerateModules))
                {
                    names.Add(ModuleName(module));
                    Release(module);
                }
                Release(assembly);
            }
            Release(appDomain);
        }
        return names;
    }

    /// <summary>The first loaded module whose name contains <paramref name="nameSubstring"/>
    /// (case-insensitive), as an OWNED reference the caller must Release via <see cref="Release"/>.
    /// Returns 0 if none match. All other enumerated pointers are released here.</summary>
    public static nint FindModule(nint pProcess, string nameSubstring)
    {
        nint match = 0;
        foreach (nint appDomain in Drain(pProcess, ProcessEnumerateAppDomains))
        {
            foreach (nint assembly in Drain(appDomain, AppDomainEnumerateAssemblies))
            {
                foreach (nint module in Drain(assembly, AssemblyEnumerateModules))
                {
                    if (match == 0 && ModuleName(module).Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                        match = module;  // keep — owned by the caller
                    else
                        Release(module);
                }
                Release(assembly);
            }
            Release(appDomain);
        }
        return match;
    }

    /// <summary>Resolve <paramref name="typeName"/>.<paramref name="methodName"/> in the module
    /// matching <paramref name="moduleNameSubstring"/> to an <c>mdMethodDef</c> token (0 if not
    /// found). Process must be synchronized.</summary>
    public static uint ResolveMethodToken(nint pProcess, string moduleNameSubstring, string typeName, string methodName)
    {
        nint pModule = FindModule(pProcess, moduleNameSubstring);
        if (pModule == 0) return 0;
        try { return MetadataResolver.ResolveMethodToken(pModule, typeName, methodName); }
        finally { Release(pModule); }
    }

    /// <summary>Call an <c>EnumerateX(out ICorDebug*Enum)</c> at <paramref name="enumerateSlot"/>,
    /// then drain it one element at a time. Returns owned references (caller releases each);
    /// the enumerator itself is released here. Not an iterator — function-pointer locals can't
    /// coexist with <c>yield</c>.</summary>
    private static List<nint> Drain(nint pParent, int enumerateSlot)
    {
        List<nint> items = new();

        nint pEnum;
        int hr = ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pParent, enumerateSlot))(pParent, &pEnum);
        if (hr < 0 || pEnum == 0) return items;

        var next = (delegate* unmanaged[Cdecl]<nint, uint, nint*, uint*, int>)Slot(pEnum, EnumNext);
        while (true)
        {
            nint one;
            uint fetched;
            if (next(pEnum, 1, &one, &fetched) < 0 || fetched == 0) break;
            items.Add(one);
        }
        Release(pEnum);
        return items;
    }

    /// <summary>ICorDebugModule.GetName(ULONG32 cchName, ULONG32* pcchName, WCHAR szName[]).
    /// Two-call buffer pattern: size, then fill. <paramref name="pcchName"/> includes the NUL.</summary>
    private static string ModuleName(nint pModule)
    {
        var getName = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, int>)Slot(pModule, ModuleGetName);

        uint needed = 0;
        if (getName(pModule, 0, &needed, null) < 0 || needed == 0) return "";

        char[] buffer = new char[needed];
        fixed (char* p = buffer)
        {
            uint written = 0;
            if (getName(pModule, needed, &written, p) < 0) return "";
            int length = written > 0 ? (int)written - 1 : 0; // drop the trailing NUL
            return new string(buffer, 0, length);
        }
    }
}
