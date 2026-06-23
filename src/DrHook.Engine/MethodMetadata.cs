// Read a method's ARGUMENT names from a module's PE metadata — the assembly-metadata counterpart
// to SymbolReader's PDB local names. Parameter names and instance-vs-static (the HasThis bit) are
// NOT in the Portable PDB; they live in the main assembly metadata (the Param table + the method
// signature's calling convention). PURE MANAGED: System.Reflection.Metadata over the module FILE on
// disk (no ICorDebug, no native interop, no third-party package — ships in the .NET ref pack, so
// DrHook.Engine stays BCL-only). Because it reads a file, not a live process, it is deterministically
// unit-testable against any assembly's own metadata — the same testability bar as SymbolReader.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SkyOmega.DrHook.Engine;

/// <summary>Resolves a method's argument names from its module's PE metadata, so a stack frame's
/// arguments carry their real source names (<c>this</c> + declared parameters) instead of positional
/// placeholders. The argument-side counterpart to <see cref="SymbolReader.GetLocalNames"/>.</summary>
public static class MethodMetadata
{
    /// <summary>The ordered argument names of the method <paramref name="methodToken"/> (an
    /// <c>mdMethodDef</c>, high byte 0x06), aligned to <c>ICorDebugILFrame.GetArgument(i)</c> indices:
    /// index 0 is <c>this</c> for an instance method, then the declared parameters in signature order.
    /// An entry is empty (<c>""</c>) for a parameter with no name in metadata; the caller supplies a
    /// positional fallback. Empty list when <paramref name="modulePath"/> can't be read as a PE, it has
    /// no metadata, or the token is not a method.</summary>
    public static IReadOnlyList<string> ArgumentNames(string modulePath, int methodToken)
    {
        if (string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath)) return Array.Empty<string>();
        if ((methodToken >> 24) != 0x06) return Array.Empty<string>(); // not an mdMethodDef
        try
        {
            using PEReader pe = new(File.OpenRead(modulePath));
            if (!pe.HasMetadata) return Array.Empty<string>();
            MetadataReader md = pe.GetMetadataReader();

            MethodDefinition method = md.GetMethodDefinition(
                MetadataTokens.MethodDefinitionHandle(methodToken & 0x00FFFFFF));

            // HasThis lives in the signature's calling-convention header, NOT the Param table — a
            // static method (every top-level-program local function, every static helper) has no
            // receiver, so its argument 0 is the first declared parameter, not "this".
            bool hasThis = md.GetBlobReader(method.Signature).ReadSignatureHeader().IsInstance;

            // Parameter names by sequence number (1..N; sequence 0 is the return value). A parameter
            // may have no Param row / no name — those slots stay absent and fall back positionally.
            Dictionary<int, string> bySequence = new();
            int maxSequence = 0;
            foreach (ParameterHandle ph in method.GetParameters())
            {
                Parameter p = md.GetParameter(ph);
                if (p.SequenceNumber == 0) continue; // return value, not an argument
                bySequence[p.SequenceNumber] = p.Name.IsNil ? "" : md.GetString(p.Name);
                if (p.SequenceNumber > maxSequence) maxSequence = p.SequenceNumber;
            }

            List<string> ordered = new();
            if (hasThis) ordered.Add("this");
            for (int sequence = 1; sequence <= maxSequence; sequence++)
                ordered.Add(bySequence.TryGetValue(sequence, out string? name) ? name : "");
            return ordered;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
