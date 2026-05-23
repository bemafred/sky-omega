// Read the text of a string ICorDebugValue — the first slice of reference-typed-result rendering
// (the long-standing gap from findings 32/37/39). String is the most common reference result
// (ex.Message, return values of get_String, etc.). Chain: QI ICorDebugStringValue directly first
// (works if pValue is already a dereferenced heap value); else QI ICorDebugReferenceValue ->
// Dereference -> QI ICorDebugStringValue. Then GetLength@9 -> GetString@10 to copy the chars.
//
// Slots + IIDs verified from cordebug.idl: ICorDebugStringValue inherits ICorDebugHeapValue
// inherits ICorDebugValue, so its vtable is IUnknown(0-2) + ICorDebugValue(3-6) +
// ICorDebugHeapValue(7-8) + own (9 GetLength, 10 GetString).
//
// PRECONDITION: process synchronized (called at a stop). The dereferenced heap value is valid
// only until the next Continue.

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class StringInspector
{
    private static readonly Guid IID_ICorDebugReferenceValue = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");
    private static readonly Guid IID_ICorDebugStringValue    = new("CC7BCAFD-8A68-11D2-983C-0000F808342D");

    private const int ReferenceValueDereference = 10;
    private const int StringValueGetLength      = 9;
    private const int StringValueGetString      = 10;

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static nint QueryInterface(nint pUnk, Guid iid)
    {
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pUnk, 0);
        nint result;
        return qi(pUnk, &iid, &result) < 0 ? 0 : result;
    }

    /// <summary>If <paramref name="pValue"/> is a string (directly or via a reference to one),
    /// reads its content into <paramref name="text"/> and returns <c>true</c>; otherwise returns
    /// <c>false</c> with <paramref name="text"/> = null. Cheap on misses (one or two QIs return
    /// E_NOINTERFACE) so it's safe to call on every value read.</summary>
    public static bool TryRead(nint pValue, out string? text)
    {
        text = null;
        if (pValue == 0) return false;

        // Either pValue is already a string heap value (e.g., a dereferenced result), OR it's a
        // reference value that we must Dereference first. Try direct first; fall back via reference.
        nint stringValue = QueryInterface(pValue, IID_ICorDebugStringValue);
        nint dereferenced = 0;
        try
        {
            if (stringValue == 0)
            {
                nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
                if (reference == 0) return false;
                try
                {
                    nint outPtr;
                    var deref = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(reference, ReferenceValueDereference);
                    if (deref(reference, &outPtr) < 0 || outPtr == 0) return false;
                    dereferenced = outPtr;
                }
                finally { RuntimeNavigation.Release(reference); }

                stringValue = QueryInterface(dereferenced, IID_ICorDebugStringValue);
                if (stringValue == 0) return false;
            }

            uint length;
            var getLength = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(stringValue, StringValueGetLength);
            if (getLength(stringValue, &length) < 0) return false;
            if (length == 0) { text = string.Empty; return true; }

            // GetString: cchString = capacity, pcchString = actual chars written. Allocate +1 as
            // belt-and-braces; truncate to the returned count.
            uint capacity = length + 1;
            char[] buffer = new char[capacity];
            uint actual = 0;
            fixed (char* pBuf = buffer)
            {
                var getString = (delegate* unmanaged[Cdecl]<nint, uint, uint*, char*, int>)Slot(stringValue, StringValueGetString);
                if (getString(stringValue, capacity, &actual, pBuf) < 0) return false;
            }
            text = new string(buffer, 0, (int)Math.Min(actual, length));
            return true;
        }
        finally
        {
            if (stringValue != 0) RuntimeNavigation.Release(stringValue);
            if (dereferenced != 0) RuntimeNavigation.Release(dereferenced);
        }
    }
}
