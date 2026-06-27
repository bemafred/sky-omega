// span-length-condition-target — validates a breakpoint CONDITION on a ReadOnlySpan<char> argument
// (ADR-013 D3). Scan(ReadOnlySpan<char> value): `value` is a ref-struct argument; the smoke arms a
// conditional breakpoint whose condition reads `.Length` on it (`value.Length > 5`). A span cannot be
// func-eval'd (it cannot be boxed as the getter's receiver), so D3 reads `_length` directly. Two calls
// with different lengths prove the condition both EVALUATES without fault and GATES correctly — it must
// skip the length-3 call and stop on the length-8 call.
//
// Built to a DLL by the smoke (build-first), launched under DebugSession via `dotnet exec`.

using System;
using System.Diagnostics;

Debugger.Break();                                    // setup stop — smoke arms the conditional breakpoint
Console.WriteLine(Scan("abc"));                      // Length=3 — condition value.Length>5 FALSE → no stop
Console.WriteLine(Scan("abcdefgh"));                 // Length=8 — condition TRUE → stop on THIS call

static int Scan(ReadOnlySpan<char> value)
{
    int seen = value.Length;                         // SPAN_MARK — value is a ReadOnlySpan<char> ARGUMENT
    return seen;
}
