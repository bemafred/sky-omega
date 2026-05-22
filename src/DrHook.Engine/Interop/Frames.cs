// Walk the managed call stack of a stopped thread → structured frame data: method name (from
// metadata), the module file path (for symbol lookup), the method token, and the current IL
// offset (for source-line mapping). Raw V-table (slots from cordebug.idl order), validated by
// probes 14/16. From the active frame, chase ICorDebugFrame.GetCaller up the stack; each frame's
// IL offset comes from QI to ICorDebugILFrame → GetIP. Line resolution itself lives in
// DebugSession (it owns the SymbolReader cache) — Frames stays pure interop.
//
// PRECONDITION: process synchronized. Frames whose token isn't an mdMethodDef are "[external]".

namespace SkyOmega.DrHook.Engine.Interop;

/// <summary>One walked frame: its "Type.Method" name, the module file path (for the PDB), the
/// <c>mdMethodDef</c> token, and the current IL offset (-1 if unavailable).</summary>
internal readonly record struct FrameInfo(string Method, string ModulePath, int Token, int IlOffset);

internal static unsafe class Frames
{
    private const int ThreadGetActiveFrame = 15; // ICorDebugThread
    private const int FrameGetFunction = 5;       // ICorDebugFrame
    private const int FrameGetFunctionToken = 6;
    private const int FrameGetCaller = 8;
    private const int FunctionGetModule = 3;      // ICorDebugFunction
    private const int ILFrameGetIP = 11;          // ICorDebugILFrame

    private static readonly Guid IID_ICorDebugILFrame = new("03E26311-4F76-11D3-88C6-006097945418");

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    /// <summary>Walk the managed call stack (top frame first).</summary>
    public static List<FrameInfo> WalkManagedFrames(nint pThread)
    {
        List<FrameInfo> frames = new();
        if (pThread == 0) return frames;

        nint frame = OutPtr(pThread, ThreadGetActiveFrame);
        for (int guard = 0; frame != 0 && guard < 256; guard++)
        {
            frames.Add(Describe(frame));
            nint caller = OutPtr(frame, FrameGetCaller);
            RuntimeNavigation.Release(frame);
            frame = caller;
        }
        return frames;
    }

    private static FrameInfo Describe(nint pFrame)
    {
        uint token = OutUint(pFrame, FrameGetFunctionToken);
        if ((token >> 24) != 0x06) return new FrameInfo("[external]", "", 0, -1); // native/internal frame

        nint function = OutPtr(pFrame, FrameGetFunction);
        if (function == 0) return new FrameInfo("[external]", "", (int)token, -1);
        try
        {
            nint module = OutPtr(function, FunctionGetModule);
            if (module == 0) return new FrameInfo("[external]", "", (int)token, -1);
            try
            {
                string method = MetadataResolver.MethodName(module, token);
                string modulePath = RuntimeNavigation.ModuleName(module);
                return new FrameInfo(method, modulePath, (int)token, CurrentIlOffset(pFrame));
            }
            finally { RuntimeNavigation.Release(module); }
        }
        finally { RuntimeNavigation.Release(function); }
    }

    private static int CurrentIlOffset(nint pFrame)
    {
        // QI to ICorDebugILFrame, then GetIP(ULONG32* pnOffset, CorDebugMappingResult* pMapping).
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pFrame, 0);
        Guid iid = IID_ICorDebugILFrame;
        nint ilFrame;
        if (qi(pFrame, &iid, &ilFrame) < 0 || ilFrame == 0) return -1;
        try
        {
            var getIp = (delegate* unmanaged[Cdecl]<nint, uint*, int*, int>)Slot(ilFrame, ILFrameGetIP);
            uint offset;
            int mapping;
            return getIp(ilFrame, &offset, &mapping) < 0 ? -1 : (int)offset;
        }
        finally { RuntimeNavigation.Release(ilFrame); }
    }

    private static nint OutPtr(nint pUnk, int slot)
    {
        nint outPtr;
        return ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pUnk, slot))(pUnk, &outPtr) < 0 ? 0 : outPtr;
    }

    private static uint OutUint(nint pUnk, int slot)
    {
        uint value;
        return ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(pUnk, slot))(pUnk, &value) < 0 ? 0 : value;
    }
}
