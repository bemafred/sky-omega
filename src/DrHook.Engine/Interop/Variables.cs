// Read a stack frame's argument values (ADR-006 Phase 2, inspection 5b). At a stop, QI the
// active frame to ICorDebugILFrame, read each argument as an ICorDebugValue, get its
// CorElementType, and — for a generic (primitive) value — copy its bits via
// ICorDebugGenericValue.GetValue. Raw V-table on the slots; QI is used for the two derived
// interfaces (ILFrame, GenericValue), which fails GRACEFULLY (null) if an IID is wrong — unlike
// a raw slot call on the wrong vtable, so no crash risk. Validated by probe 15.
//
// PRECONDITION: process synchronized. v1 reads ARGUMENTS (live at method entry); locals need PDB
// names (deferred). Object references report their element type with a null raw value (no
// dereference yet).

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class Variables
{
    // IIDs verified against dotnet/runtime src/coreclr/inc/cordebug.idl (not guessed — a wrong
    // value fails QI gracefully, but these are authoritative).
    private static readonly Guid IID_ICorDebugILFrame = new("03E26311-4F76-11D3-88C6-006097945418");
    private static readonly Guid IID_ICorDebugGenericValue = new("CC7BCAF8-8A68-11D2-983C-0000F808342D");

    private const int ThreadGetActiveFrame = 15; // ICorDebugThread
    private const int ILFrameGetArgument = 16;   // ICorDebugILFrame (ILFrame methods after ICorDebugFrame 3-10: GetIP11..GetArgument16)
    private const int ValueGetType = 3;          // ICorDebugValue
    private const int GenericValueGetValue = 7;  // ICorDebugGenericValue (after ICorDebugValue 3-6)

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static nint QueryInterface(nint pUnk, Guid iid)
    {
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pUnk, 0);
        nint result;
        return qi(pUnk, &iid, &result) < 0 ? 0 : result;
    }

    /// <summary>Read up to <paramref name="maxArgs"/> arguments of the stopped thread's active
    /// frame (arg 0 is <c>this</c> for an instance method).</summary>
    public static List<ArgumentValue> ReadActiveFrameArguments(nint pThread, int maxArgs)
    {
        List<ArgumentValue> args = new();
        if (pThread == 0) return args;

        nint frame = OutPtr(pThread, ThreadGetActiveFrame);
        if (frame == 0) return args;
        try
        {
            nint ilFrame = QueryInterface(frame, IID_ICorDebugILFrame);
            if (ilFrame == 0) return args; // not an IL frame
            try
            {
                var getArgument = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(ilFrame, ILFrameGetArgument);
                for (uint i = 0; i < (uint)maxArgs; i++)
                {
                    nint value;
                    if (getArgument(ilFrame, i, &value) < 0 || value == 0) break; // past the last argument
                    try { args.Add(ReadValue(value)); }
                    finally { RuntimeNavigation.Release(value); }
                }
            }
            finally { RuntimeNavigation.Release(ilFrame); }
        }
        finally { RuntimeNavigation.Release(frame); }
        return args;
    }

    private static ArgumentValue ReadValue(nint pValue)
    {
        int elementType = OutInt(pValue, ValueGetType);

        long? raw = null;
        nint generic = QueryInterface(pValue, IID_ICorDebugGenericValue);
        if (generic != 0)
        {
            try
            {
                long buffer = 0; // pre-zeroed: a 4-byte value lands in the low half on little-endian
                var getValue = (delegate* unmanaged[Cdecl]<nint, void*, int>)Slot(generic, GenericValueGetValue);
                if (getValue(generic, &buffer) >= 0) raw = buffer;
            }
            finally { RuntimeNavigation.Release(generic); }
        }
        return new ArgumentValue(elementType, raw);
    }

    private static nint OutPtr(nint pUnk, int slot)
    {
        nint outPtr;
        return ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pUnk, slot))(pUnk, &outPtr) < 0 ? 0 : outPtr;
    }

    private static int OutInt(nint pUnk, int slot)
    {
        int value;
        return ((delegate* unmanaged[Cdecl]<nint, int*, int>)Slot(pUnk, slot))(pUnk, &value) < 0 ? 0 : value;
    }
}
