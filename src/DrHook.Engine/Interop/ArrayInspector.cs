// Enumerate the elements of an array value — the array half of object inspection (the field
// half is FieldEnumerator, probe 39). This slice covers SZARRAY (rank-1 zero-based, the common
// case for `T[]`); multi-dim arrays are a small follow-on (needs GetDimensions / GetElement).
//
// Chain (mirrors FieldEnumerator): try direct QI of ICorDebugArrayValue; if pValue is a
// ReferenceValue (locals/args of an array type usually are), Dereference first then re-QI on
// the heap value. Then GetRank@10 (require 1), GetCount@11, GetElementAtPosition@16 per index.
// Each element value is rendered via Variables.ReadValue (primitives + string) and recursed
// through Variables.GetChildren when depth > 1 (which dispatches back to FieldEnumerator or
// ArrayInspector based on the element's own kind — arrays of objects and objects with array
// fields both compose).
//
// Cap on element count: 64 entries per array, with a trailing FieldValue marker if truncated
// ("[…M more]"). Keeps step_vars output bounded for huge collections; the cap is a render-side
// truncation, not a substrate limit.
//
// Slots + IIDs verified from cordebug.idl. PRECONDITION: process synchronized (called at a stop).

using System.Globalization;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ArrayInspector
{
    private static readonly Guid IID_ICorDebugReferenceValue = new("CC7BCAF9-8A68-11D2-983C-0000F808342D");
    private static readonly Guid IID_ICorDebugArrayValue     = new("0405B0DF-A660-11D2-BD02-0000F80849BD");

    private const int ReferenceValueDereference     = 10;  // ICorDebugReferenceValue
    private const int ArrayValueGetRank             = 10;  // ICorDebugArrayValue (IUnknown 0-2, Value 3-6, HeapValue 7-8, own 9+)
    private const int ArrayValueGetCount            = 11;
    private const int ArrayValueGetElementAtPosition = 16;

    private const int MaxElements = 64;

    private static nint Slot(nint pUnk, int index) => ((nint*)*(nint*)pUnk)[index];

    private static nint QueryInterface(nint pUnk, Guid iid)
    {
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(pUnk, 0);
        nint result;
        return qi(pUnk, &iid, &result) < 0 ? 0 : result;
    }

    private static nint Out(nint pUnk, int slot)
    {
        nint outPtr;
        return ((delegate* unmanaged[Cdecl]<nint, nint*, int>)Slot(pUnk, slot))(pUnk, &outPtr) < 0 ? 0 : outPtr;
    }

    /// <summary>If <paramref name="pValue"/> is a rank-1 array (SZARRAY), reads its elements as
    /// <see cref="FieldValue"/> entries named <c>"[0]"</c>, <c>"[1]"</c>, etc., and returns the
    /// list (recursive at <paramref name="depth"/> &gt; 1 via <see cref="Variables.GetChildren"/>).
    /// Returns <c>null</c> for non-arrays or multi-dimensional arrays (deferred to a future slice).
    /// Capped at <see cref="MaxElements"/> entries with a trailing marker if truncated.</summary>
    public static IReadOnlyList<FieldValue>? TryReadElements(nint pValue, int depth)
    {
        if (pValue == 0 || depth <= 0) return null;

        nint arrayValue = QueryInterface(pValue, IID_ICorDebugArrayValue);
        nint dereferenced = 0;
        if (arrayValue == 0)
        {
            nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
            if (reference == 0) return null;
            try { dereferenced = Out(reference, ReferenceValueDereference); }
            finally { RuntimeNavigation.Release(reference); }
            if (dereferenced == 0) return null;
            arrayValue = QueryInterface(dereferenced, IID_ICorDebugArrayValue);
            if (arrayValue == 0) { RuntimeNavigation.Release(dereferenced); return null; }
        }

        try
        {
            uint rank;
            var getRank = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(arrayValue, ArrayValueGetRank);
            if (getRank(arrayValue, &rank) < 0) return null;
            if (rank != 1) return null;   // multi-dim → future slice

            uint count;
            var getCount = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(arrayValue, ArrayValueGetCount);
            if (getCount(arrayValue, &count) < 0) return null;
            if (count == 0) return Array.Empty<FieldValue>();

            uint visible = count > MaxElements ? MaxElements : count;
            var elements = new List<FieldValue>((int)visible);
            var getElement = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(arrayValue, ArrayValueGetElementAtPosition);

            for (uint i = 0; i < visible; i++)
            {
                nint elementValue;
                if (getElement(arrayValue, i, &elementValue) < 0 || elementValue == 0)
                {
                    elements.Add(new FieldValue($"[{i.ToString(CultureInfo.InvariantCulture)}]", 0, null));
                    continue;
                }
                try
                {
                    ArgumentValue v = Variables.ReadValue(elementValue);
                    // Lazy: ONE level only — never recurse. HasChildren marks an object/array element
                    // as expandable so a caller navigates deeper on demand (DebugSession.ExpandLocal),
                    // not the eager multi-level walk that faulted at scale (ADR-007).
                    elements.Add(new FieldValue($"[{i.ToString(CultureInfo.InvariantCulture)}]",
                        v.ElementType, v.RawValue, v.StringValue, null, v.HasChildren, v.TypeName));
                }
                finally { RuntimeNavigation.Release(elementValue); }
            }

            if (count > MaxElements)
                elements.Add(new FieldValue($"[…{(count - MaxElements).ToString(CultureInfo.InvariantCulture)} more]", 0, null));

            return elements;
        }
        finally
        {
            RuntimeNavigation.Release(arrayValue);
            if (dereferenced != 0) RuntimeNavigation.Release(dereferenced);
        }
    }

    /// <summary>Return element <paramref name="index"/> of a rank-1 array as an OWNED value the
    /// caller releases — the lazy-expansion primitive for arrays (one element, one level). 0 if the
    /// value is not a rank-1 array or the index is out of range. PRECONDITION: process synchronized.</summary>
    public static nint GetElementValueByIndex(nint pValue, uint index)
    {
        if (pValue == 0) return 0;

        nint arrayValue = QueryInterface(pValue, IID_ICorDebugArrayValue);
        nint dereferenced = 0;
        if (arrayValue == 0)
        {
            nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
            if (reference == 0) return 0;
            try { dereferenced = Out(reference, ReferenceValueDereference); }
            finally { RuntimeNavigation.Release(reference); }
            if (dereferenced == 0) return 0;
            arrayValue = QueryInterface(dereferenced, IID_ICorDebugArrayValue);
            if (arrayValue == 0) { RuntimeNavigation.Release(dereferenced); return 0; }
        }

        try
        {
            uint count;
            var getCount = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Slot(arrayValue, ArrayValueGetCount);
            if (getCount(arrayValue, &count) < 0 || index >= count) return 0;

            nint element;
            var getElement = (delegate* unmanaged[Cdecl]<nint, uint, nint*, int>)Slot(arrayValue, ArrayValueGetElementAtPosition);
            return getElement(arrayValue, index, &element) < 0 ? 0 : element; // owned — caller releases
        }
        finally
        {
            RuntimeNavigation.Release(arrayValue);
            if (dereferenced != 0) RuntimeNavigation.Release(dereferenced);
        }
    }
}
