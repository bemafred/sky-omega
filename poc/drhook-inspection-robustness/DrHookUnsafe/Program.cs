// DrHook isolation probe round 2 (ADR-007): does a walked reference type holding an
// unsafe pointer field crash the depth-2 inspection? This is the QuadStore shape (its
// mmap base pointer). List<int> already showed class-walking is safe, so the pointer
// field is the isolated variable.

Console.WriteLine("unsafe-probe start");
NativePtrCase();
Console.WriteLine("unsafe-probe done");

static unsafe void NativePtrCase()
{
    byte* p = stackalloc byte[16];
    p[0] = 42;
    var h = new NativeHolder { Ptr = p, Len = 16 };
    Console.WriteLine($"[NATIVEPTR] ready len={h.Len} p0={p[0]}");  // <-- BP: depth-2 inspect 'h' (class with a byte* field)
    GC.KeepAlive(h);
}

// A reference type (class) holding a raw pointer field — the QuadStore/mmap shape.
unsafe class NativeHolder
{
    public byte* Ptr;
    public int Len;
}
