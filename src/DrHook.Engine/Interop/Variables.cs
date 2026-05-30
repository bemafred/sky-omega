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
    private const int ILFrameGetLocalVariable = 14; // ICorDebugILFrame (GetIP11, SetIP12, EnumLocals13, GetLocalVariable14)
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
    /// frame (arg 0 is <c>this</c> for an instance method). When <paramref name="depth"/> &gt; 0,
    /// object-typed args have their <see cref="ArgumentValue.Fields"/> populated by walking
    /// instance fields (finding 48 / probe 39).</summary>
    public static List<ArgumentValue> ReadActiveFrameArguments(nint pThread, int maxArgs, int depth = 0)
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
                    try
                    {
                        ArgumentValue v = ReadValue(value);
                        IReadOnlyList<FieldValue>? children = GetChildren(value, v.ElementType, depth);
                        args.Add(new ArgumentValue(v.ElementType, v.RawValue, v.StringValue, children));
                    }
                    finally { RuntimeNavigation.Release(value); }
                }
            }
            finally { RuntimeNavigation.Release(ilFrame); }
        }
        finally { RuntimeNavigation.Release(frame); }
        return args;
    }

    /// <summary>Read the named locals of the stopped thread's active frame. <paramref name="names"/>
    /// (slot → name, from the PDB) drives which slots are read; each is fetched via
    /// <c>ICorDebugILFrame.GetLocalVariable</c>@14 and its value decoded like an argument. A local
    /// not available at the current IL offset surfaces with a null raw value. When
    /// <paramref name="depth"/> &gt; 0, object-typed locals have their <see cref="LocalValue.Fields"/>
    /// populated by walking instance fields (finding 48 / probe 39).</summary>
    public static List<LocalValue> ReadActiveFrameLocals(nint pThread, IReadOnlyList<LocalName> names, int depth = 0)
    {
        List<LocalValue> locals = new();
        if (pThread == 0 || names.Count == 0) return locals;

        nint frame = OutPtr(pThread, ThreadGetActiveFrame);
        if (frame == 0) return locals;
        try
        {
            nint ilFrame = QueryInterface(frame, IID_ICorDebugILFrame);
            if (ilFrame == 0) return locals;
            try
            {
                var getLocal = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(ilFrame, ILFrameGetLocalVariable);
                foreach (LocalName name in names)
                {
                    nint value;
                    if (getLocal(ilFrame, (uint)name.Slot, &value) < 0 || value == 0)
                    {
                        locals.Add(new LocalValue(name.Name, 0, null)); // not available at this offset
                        continue;
                    }
                    try
                    {
                        ArgumentValue v = ReadValue(value);
                        IReadOnlyList<FieldValue>? children = GetChildren(value, v.ElementType, depth);
                        locals.Add(new LocalValue(name.Name, v.ElementType, v.RawValue, v.StringValue, children));
                    }
                    finally { RuntimeNavigation.Release(value); }
                }
            }
            finally { RuntimeNavigation.Release(ilFrame); }
        }
        finally { RuntimeNavigation.Release(frame); }
        return locals;
    }

    /// <summary>Dispatch by element type to the right child-rendering inspector:
    /// CLASS/OBJECT → <see cref="FieldEnumerator"/>; SZARRAY/ARRAY → <see cref="ArrayInspector"/>;
    /// strings render via <c>StringValue</c> (no children); other kinds return null.
    /// Centralizes recursion so a field whose value is an array (or an element whose value is an
    /// object) Just Works without each inspector knowing about the others.</summary>
    internal static IReadOnlyList<FieldValue>? GetChildren(nint pValue, int elementType, int depth)
    {
        if (pValue == 0 || depth <= 0) return null;
        if (elementType == 0x12 /* CLASS */ || elementType == 0x1C /* OBJECT */)
            return FieldEnumerator.GetFields(pValue, depth);
        if (elementType == 0x14 /* ARRAY */ || elementType == 0x1D /* SZARRAY */)
            return ArrayInspector.TryReadElements(pValue, depth);
        return null;
    }

    /// <summary>The active frame's local at <paramref name="slot"/> as an OWNED value pointer the
    /// caller releases — for passing as a func-eval argument (e.g. <c>this</c>). 0 if unavailable.</summary>
    public static nint GetActiveFrameLocalValue(nint pThread, int slot)
    {
        if (pThread == 0) return 0;
        nint frame = OutPtr(pThread, ThreadGetActiveFrame);
        if (frame == 0) return 0;
        try
        {
            nint ilFrame = QueryInterface(frame, IID_ICorDebugILFrame);
            if (ilFrame == 0) return 0;
            try
            {
                var getLocal = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(ilFrame, ILFrameGetLocalVariable);
                nint value;
                return getLocal(ilFrame, (uint)slot, &value) < 0 ? 0 : value; // kept — caller releases
            }
            finally { RuntimeNavigation.Release(ilFrame); }
        }
        finally { RuntimeNavigation.Release(frame); }
    }

    internal static ArgumentValue ReadValue(nint pValue)
    {
        int elementType = OutInt(pValue, ValueGetType);

        object? raw = null;
        nint generic = QueryInterface(pValue, IID_ICorDebugGenericValue);
        if (generic != 0)
        {
            try
            {
                long buffer = 0; // pre-zeroed: a 4-byte value lands in the low half on little-endian
                var getValue = (delegate* unmanaged[Cdecl]<nint, void*, int>)Slot(generic, GenericValueGetValue);
                if (getValue(generic, &buffer) >= 0) raw = ReifyPrimitive(elementType, buffer);
            }
            finally { RuntimeNavigation.Release(generic); }
        }

        // Reference-string rendering (finding 44 / probe 35) — cheap on misses (one or two QIs).
        string? stringValue = StringInspector.TryRead(pValue, out string? text) ? text : null;

        return new ArgumentValue(elementType, raw, stringValue);
    }

    /// <summary>Reify the raw 8-byte buffer returned by <c>ICorDebugGenericValue.GetValue</c> as a
    /// boxed CLR primitive whose runtime type matches the CorElementType. The expression compiler
    /// in <see cref="SkyOmega.DrHook.Engine.Expressions.CSharpCondition"/> reads
    /// <see cref="ArgumentValue.RawValue"/> as the typed source for <c>Expression.Constant</c>
    /// and operator nodes, so this is the single point where the substrate enforces type
    /// fidelity end-to-end — instead of flattening every primitive to <c>long</c> at the leaf
    /// and re-typing it under interpretation. Unknown / non-primitive element types fall back
    /// to boxed <see cref="long"/> so the raw bit pattern remains observable.</summary>
    internal static object ReifyPrimitive(int elementType, long buffer) => elementType switch
    {
        0x02 /* BOOLEAN */ => buffer != 0,
        0x03 /* CHAR    */ => (char)buffer,
        0x04 /* I1      */ => (sbyte)buffer,
        0x05 /* U1      */ => (byte)buffer,
        0x06 /* I2      */ => (short)buffer,
        0x07 /* U2      */ => (ushort)buffer,
        0x08 /* I4      */ => (int)buffer,
        0x09 /* U4      */ => (uint)buffer,
        0x0A /* I8      */ => buffer,
        0x0B /* U8      */ => (ulong)buffer,
        0x0C /* R4      */ => BitConverter.Int32BitsToSingle((int)buffer),
        0x0D /* R8      */ => BitConverter.Int64BitsToDouble(buffer),
        0x18 /* I       */ => (IntPtr)buffer,
        0x19 /* U       */ => (UIntPtr)(ulong)buffer,
        _                  => buffer
    };

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
