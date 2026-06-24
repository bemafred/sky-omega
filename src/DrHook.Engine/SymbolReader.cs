// Read a module's Portable PDB for source mapping — IL-offset → (file, line) and local-slot →
// name. PURE MANAGED: System.Reflection.Metadata over the module FILE on disk (no ICorDebug, no
// native interop, no third-party package — System.Reflection.Metadata ships in the .NET ref pack,
// so this keeps DrHook.Engine BCL-only). Because it reads files, not a live process, it is
// deterministically unit-testable against any assembly's own PDB.
//
// This is the symbol layer that turns "Worker.Tick, IL offset 7" into "Worker.cs:42" and unnamed
// local slots into names — load-bearing for source-line breakpoints, named locals, and the
// client-side condition evaluation that conditional breakpoints need (the netcoredbg gap).

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SkyOmega.DrHook.Engine;

/// <summary>A source line + file for an IL location.</summary>
public readonly record struct SourceLocation(string File, int Line);

/// <summary>A local variable's PDB slot index and name.</summary>
public readonly record struct LocalName(int Slot, string Name);

/// <summary>Reads a module's Portable PDB (embedded in the PE or a sidecar <c>.pdb</c>) for
/// IL↔source mapping. Dispose releases the underlying file streams.</summary>
public sealed class SymbolReader : IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _pdb;
    private readonly PEReader? _pe; // held open when the PDB is embedded in the PE

    private SymbolReader(MetadataReaderProvider provider, MetadataReader pdb, PEReader? pe)
    {
        _provider = provider;
        _pdb = pdb;
        _pe = pe;
    }

    /// <summary>Open the Portable PDB for <paramref name="modulePath"/> — embedded in the PE, else
    /// a sidecar <c>&lt;module&gt;.pdb</c> next to it. Returns null if no Portable PDB is found.</summary>
    public static SymbolReader? TryOpen(string modulePath)
    {
        if (string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath)) return null;

        PEReader? pe = null;
        try
        {
            pe = new PEReader(File.OpenRead(modulePath));
            foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb) continue;
                MetadataReaderProvider embedded = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                return new SymbolReader(embedded, embedded.GetMetadataReader(), pe); // keep pe open
            }
            pe.Dispose();
            pe = null;

            string sidecar = Path.ChangeExtension(modulePath, ".pdb");
            if (File.Exists(sidecar))
            {
                MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(sidecar));
                return new SymbolReader(provider, provider.GetMetadataReader(), null);
            }
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            pe?.Dispose();
            return null;
        }
    }

    /// <summary>Open a sidecar Portable PDB by its OWN path (no PE). For a bundled single-file module
    /// whose assembly has no standalone PE on disk — it loads from the bundle, so its reported module
    /// path is a bare name — but whose <c>.pdb</c> sits next to the apphost (the PublishSingleFile
    /// <c>DebugType=portable</c> shape). Returns null if absent or not a Portable PDB.</summary>
    public static SymbolReader? TryOpenPdb(string pdbPath)
    {
        if (string.IsNullOrEmpty(pdbPath) || !File.Exists(pdbPath)) return null;
        try
        {
            MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
            return new SymbolReader(provider, provider.GetMetadataReader(), null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Map an <c>mdMethodDef</c> token + IL offset to its source file and line (the line
    /// of the nearest sequence point at or before the offset). Null if unmapped.</summary>
    public SourceLocation? TryGetLine(int methodToken, int ilOffset)
    {
        if ((methodToken >> 24) != 0x06) return null; // not an mdMethodDef
        try
        {
            MethodDebugInformationHandle handle =
                MetadataTokens.MethodDefinitionHandle(methodToken & 0x00FFFFFF).ToDebugInformationHandle();
            MethodDebugInformation info = _pdb.GetMethodDebugInformation(handle);

            SourceLocation? best = null;
            int bestOffset = -1;
            foreach (SequencePoint sp in info.GetSequencePoints())
            {
                if (sp.IsHidden || sp.Offset > ilOffset || sp.Offset <= bestOffset) continue;
                bestOffset = sp.Offset;
                best = new SourceLocation(_pdb.GetString(_pdb.GetDocument(sp.Document).Name), sp.StartLine);
            }
            return best;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Reverse of <see cref="TryGetLine"/>: find the method + IL offset for a source
    /// <paramref name="line"/> in a document whose name contains <paramref name="fileHint"/>. Binds
    /// to the nearest sequence point at or after the line (debugger convention). False if none.</summary>
    public bool TryFindLine(string fileHint, int line, out int methodToken, out int ilOffset)
    {
        methodToken = 0;
        ilOffset = 0;
        int bestLine = int.MaxValue;
        foreach (MethodDebugInformationHandle handle in _pdb.MethodDebugInformation)
        {
            MethodDebugInformation info;
            try { info = _pdb.GetMethodDebugInformation(handle); }
            catch (BadImageFormatException) { continue; }
            if (info.SequencePointsBlob.IsNil) continue;

            foreach (SequencePoint sp in info.GetSequencePoints())
            {
                if (sp.IsHidden || sp.StartLine < line || sp.StartLine >= bestLine) continue;
                string doc = _pdb.GetString(_pdb.GetDocument(sp.Document).Name);
                if (!doc.Contains(fileHint, StringComparison.OrdinalIgnoreCase)) continue;
                bestLine = sp.StartLine;
                methodToken = MetadataTokens.GetToken(handle.ToDefinitionHandle());
                ilOffset = sp.Offset;
            }
        }
        return methodToken != 0;
    }

    /// <summary>Local variable names (slot → name) for an <c>mdMethodDef</c> token. Empty if the
    /// method has no PDB local scopes.</summary>
    public IReadOnlyList<LocalName> GetLocalNames(int methodToken)
    {
        List<LocalName> names = new();
        if ((methodToken >> 24) != 0x06) return names;
        try
        {
            MethodDefinitionHandle method = MetadataTokens.MethodDefinitionHandle(methodToken & 0x00FFFFFF);
            foreach (LocalScopeHandle scopeHandle in _pdb.GetLocalScopes(method))
            {
                LocalScope scope = _pdb.GetLocalScope(scopeHandle);
                foreach (LocalVariableHandle lvHandle in scope.GetLocalVariables())
                {
                    LocalVariable lv = _pdb.GetLocalVariable(lvHandle);
                    if ((lv.Attributes & LocalVariableAttributes.DebuggerHidden) != 0) continue;
                    names.Add(new LocalName(lv.Index, _pdb.GetString(lv.Name)));
                }
            }
        }
        catch (BadImageFormatException)
        {
            // malformed PDB scope — return what we have
        }
        return names;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pe?.Dispose();
    }
}
