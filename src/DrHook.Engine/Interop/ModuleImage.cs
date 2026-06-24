// Read a loaded module's PE image from the TARGET's memory via ICorDebug — for a single-file bundled
// assembly whose PE is not on disk but is mapped in the target. The returned bytes are the LOADED
// (section-mapped) image; SymbolReader.TryOpenEmbeddedFromImage opens it with PEStreamOptions
// .IsLoadedImage and extracts the embedded Portable PDB (DebugType=embedded single-file).
//
// Raw V-table calls (slot numbers from cor.h order; cross-checked against the known slots in
// RuntimeNavigation: ICorDebugModule.GetName=6 + GetMetaDataInterface=14 fix GetBaseAddress=4 /
// GetSize=18, and ICorDebugProcess.EnumerateAppDomains=26 fixes ReadMemory=21).
// PRECONDITION: process synchronized (called at a stop).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ModuleImage
{
    private const int ModuleGetBaseAddress = 4;  // ICorDebugModule (IUnknown 0-2, own 3-)
    private const int ModuleGetSize = 18;
    private const int ProcessReadMemory = 21;    // ICorDebugProcess (Controller 3-12, own 13-)

    private const uint MaxImageBytes = 256u * 1024 * 1024; // sanity cap — a managed assembly image

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Read the loaded PE image of <paramref name="pModule"/> from <paramref name="pProcess"/>'s
    /// memory: base address + size via <c>ICorDebugModule</c>, bytes via <c>ICorDebugProcess.ReadMemory</c>.
    /// Returns null if the address/size are unavailable, the size is implausible, or the read returns
    /// nothing. A short read returns the prefix that was read (PE headers + debug directory live early,
    /// so the embedded-PDB extraction can still succeed). PRECONDITION: process stopped.</summary>
    public static byte[]? Read(nint pProcess, nint pModule)
    {
        if (pProcess == 0 || pModule == 0) return null;

        var getBase = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)Slot(pModule, ModuleGetBaseAddress);
        var getSize = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(pModule, ModuleGetSize);
        ulong baseAddress;
        uint size;
        if (getBase(pModule, &baseAddress) < 0 || baseAddress == 0) return null;
        if (getSize(pModule, &size) < 0 || size == 0 || size > MaxImageBytes) return null;

        var readMemory = (delegate* unmanaged[Cdecl]<nint, ulong, uint, byte*, nuint*, int>)Slot(pProcess, ProcessReadMemory);
        byte[] image = new byte[size];
        nuint read = 0;
        fixed (byte* p = image)
        {
            if (readMemory(pProcess, baseAddress, size, p, &read) < 0 || read == 0) return null;
        }
        return image;
    }
}
