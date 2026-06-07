// DrHook isolation probe round 3 (ADR-007): framework IO / native-handle objects.
// Common shapes + raw pointer fields were all safe at depth 2. QuadStore's real fields are
// FileStream + MemoryMappedFile + MemoryMappedViewAccessor (SafeHandles + a mapped base).
// depth-2 walks INTO these objects' fields — the suspected crash.

using System.IO.MemoryMappedFiles;

Console.WriteLine("io-probe start");
FileStreamCase();
MmapCase();
Console.WriteLine("io-probe done");

static void FileStreamCase()
{
    var path = Path.Combine(Path.GetTempPath(), "drhook-io-" + Guid.NewGuid().ToString("N") + ".tmp");
    using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
    fs.WriteByte(42); fs.Flush();
    var h = new IoHolder { Stream = fs };
    Console.WriteLine($"[FILESTREAM] ready len={fs.Length}");   // <-- BP: depth-2 inspect 'h' -> FileStream + SafeFileHandle internals
    GC.KeepAlive(h);
}

static void MmapCase()
{
    var path = Path.Combine(Path.GetTempPath(), "drhook-mmap-" + Guid.NewGuid().ToString("N") + ".tmp");
    using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Create, null, 4096);
    using var acc = mmf.CreateViewAccessor(0, 4096);
    acc.Write(0, (byte)7);
    var h = new MmapHolder { Accessor = acc };
    Console.WriteLine($"[MMAP] ready cap={acc.Capacity}");      // <-- BP: depth-2 inspect 'h' -> MemoryMappedViewAccessor + SafeHandle + base ptr
    GC.KeepAlive(h);
}

class IoHolder { public FileStream? Stream; }
class MmapHolder { public MemoryMappedViewAccessor? Accessor; }
