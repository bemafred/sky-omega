// Walk the managed call stack of a stopped thread → method names. Raw V-table (slots from
// cordebug.idl order), validated by probe 14. From the active frame, chase ICorDebugFrame.GetCaller
// up the stack (simpler than the chain/frame enumerators); each frame's function token + module
// feed MetadataResolver.MethodName for a "Type.Method" label.
//
// PRECONDITION: process synchronized (called at a stop). Frames whose token isn't an mdMethodDef
// (native/internal transitions) are labelled "[external]". IL offset per frame (GetIP via
// ICorDebugILFrame) is a later refinement; method names are the core "where did we stop" signal.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class Frames
{
    private const int ThreadGetActiveFrame = 15; // ICorDebugThread (IUnknown 0-2, own 3-)
    private const int FrameGetFunction = 5;       // ICorDebugFrame (GetChain3, GetCode4, GetFunction5, GetFunctionToken6, …, GetCaller8)
    private const int FrameGetFunctionToken = 6;
    private const int FrameGetCaller = 8;
    private const int FunctionGetModule = 3;      // ICorDebugFunction (GetModule3, GetClass4, GetToken5, …)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Method names of the managed call stack (top frame first), as "Type.Method".</summary>
    public static List<string> WalkManagedFrames(nint pThread)
    {
        List<string> frames = new();
        if (pThread == 0) return frames;

        nint frame = ActiveFrame(pThread);
        for (int guard = 0; frame != 0 && guard < 256; guard++)
        {
            frames.Add(FrameName(frame));
            nint caller = Caller(frame);
            RuntimeNavigation.Release(frame);
            frame = caller;
        }
        return frames;
    }

    private static string FrameName(nint pFrame)
    {
        uint token = OutUint(pFrame, FrameGetFunctionToken);
        if ((token >> 24) != 0x06) return "[external]"; // not an mdMethodDef (native/internal frame)

        nint function = OutPtr(pFrame, FrameGetFunction);
        if (function == 0) return "[external]";
        try
        {
            nint module = OutPtr(function, FunctionGetModule);
            if (module == 0) return "[external]";
            try { return MetadataResolver.MethodName(module, token); }
            finally { RuntimeNavigation.Release(module); }
        }
        finally { RuntimeNavigation.Release(function); }
    }

    private static nint ActiveFrame(nint pThread) => OutPtr(pThread, ThreadGetActiveFrame);
    private static nint Caller(nint pFrame) => OutPtr(pFrame, FrameGetCaller);

    /// <summary>Call a <c>HRESULT Foo(T** ppOut)</c> slot; 0 on failure (or S_FALSE null).</summary>
    private static nint OutPtr(nint pUnk, int slot)
    {
        nint outPtr;
        return ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pUnk, slot))(pUnk, &outPtr) < 0 ? 0 : outPtr;
    }

    /// <summary>Call a <c>HRESULT Foo(ULONG* pOut)</c> slot; 0 on failure.</summary>
    private static uint OutUint(nint pUnk, int slot)
    {
        uint value;
        return ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(pUnk, slot))(pUnk, &value) < 0 ? 0 : value;
    }
}
