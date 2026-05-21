# Finding 08: COM ↔ .NET Type Mapping — Verified Against the CoreCLR PAL

**Status:**   Verified reference. Probe 04's mappings confirmed correct; no change needed. This table governs every native-type mapping in the engine.
**Date:**     2026-05-21
**Question (raised during probe 04 review):** C/C++ native integer types have platform-variable sizes. Windows headers lock them via typedefs, but Windows is LLP64 while macOS/Linux are LP64 — where native `long` is **64-bit**. Are we mapping `LONG`/`ULONG`/`DWORD` correctly between .NET's fixed sizes and the COM/Windows sizes, or did we misread?
**Answer:** Correct — *because* the CoreCLR PAL deliberately re-typedefs the Windows integer types to fixed sizes under LP64, keeping the COM ABI byte-identical across platforms. Verified against `dotnet/runtime/src/coreclr/pal/inc/pal_mstypes.h`.

## The trap, and why we avoid it

On Windows (LLP64), `windows.h` defines `typedef long LONG;` where `long` is 32-bit. On macOS/Linux (LP64), a native `long` is **64-bit**. If the PAL had used native `long`, then `LONG` would be 64-bit on Unix and the entire ICorDebug ABI would diverge from Windows. It does not — the PAL forces 32-bit. From `pal_mstypes.h`, verbatim:

```c
typedef int          LONG;     // NOTE: diff from windows.h, for LP64 compat   (line 118)
typedef unsigned int ULONG;    // NOTE: diff from windows.h, for LP64 compat   (line 119)
typedef unsigned int DWORD;    // NOTE: diff from windows.h, for LP64 compat   (line 137)
typedef int          BOOL;     //                                              (line 139)
```

The "NOTE: diff from windows.h, for LP64 compat" comments are the lock-down: the PAL maps `LONG`/`ULONG`/`DWORD` to `int`/`unsigned int` (32-bit) precisely so they stay 32-bit on LP64 platforms. **The rule: never map `LONG` → C# `long`. `LONG` is 32-bit on every platform CoreCLR runs on; the correct C# type is `int`.**

## Verified mapping table (source: `pal_mstypes.h`, line numbers cited)

| Windows / PAL type | PAL typedef (Unix) | line | bits | C# |
|---|---|---|---|---|
| `BYTE` | `unsigned char` | 140 | 8 | `byte` |
| `CHAR` | `char` | 228 | 8 | `sbyte` (ANSI; rare — ICorDebug uses WCHAR) |
| `UCHAR` | `unsigned char` | 133 | 8 | `byte` |
| `BOOL` | `int` | 139 | 32 | `int` (**not** `bool` — Win32 BOOL is 4 bytes) |
| `SHORT` | `short` | 129 | 16 | `short` |
| `USHORT` / `WORD` | `unsigned short` | 131 / 141 | 16 | `ushort` |
| `WCHAR` | `char16_t` | 216 | 16 | `char` (UTF-16; finding 04) |
| `INT` / `LONG` | `int` | 160 / 118 | 32 | `int` |
| `UINT` / `ULONG` / `DWORD` | `unsigned int` | 161 / 119 / 137 | 32 | `uint` |
| `LONG32` / `ULONG32` | `int` / `unsigned int` | — | 32 | `int` / `uint` |
| `LONG64` / `ULONG64` | `int64_t` / `uint64_t` | 178 / 177 | 64 | `long` / `ulong` |
| `HRESULT` | `LONG` (= `int`) | 263 | 32 | `int` |
| `CONNID` | `DWORD` (= `uint`) | cordebug.idl:47 | 32 | `uint` |
| `INT_PTR` | `intptr_t` | 180 | ptr | `nint` |
| `UINT_PTR` | `uintptr_t` | 181 | ptr | `nuint` |
| `LONG_PTR` | `int64_t` (64-bit) / `int32_t` (32-bit) | 187 / 193 | ptr | `nint` |
| `ULONG_PTR` | `uint64_t` / `uint32_t` | 188 / 194 | ptr | `nuint` |
| `SIZE_T` | `uintptr_t` | 199 | ptr | `nuint` |
| `CORDB_ADDRESS` | `ULONG64` | cordebug.idl:197 | 64 | `ulong` |
| any `T*` (interface / `WCHAR*` / `BYTE*` / `IStream*`) | pointer | — | ptr | `nint` (raw; wrap lazily, finding 05) |
| enums (`CorDebugStepReason`, `CorDebugExceptionCallbackType`, …) | `int` underlying | — | 32 | `int` |

**Two size classes to keep distinct:**
- **Fixed 32-bit regardless of platform:** `LONG`, `ULONG`, `DWORD`, `INT`, `UINT`, `BOOL`, `HRESULT`, `LONG32`, `ULONG32`, `CONNID`, enums. → `int` / `uint`. The PAL locks these; the LP64 trap is avoided here.
- **Genuinely pointer-sized (64-bit on our targets):** `INT_PTR`, `UINT_PTR`, `LONG_PTR`, `ULONG_PTR`, `SIZE_T`. → `nint` / `nuint`. These *are* platform-variable, correctly.
- **Explicitly 64-bit:** `LONG64`, `ULONG64`, `CORDB_ADDRESS`. → `long` / `ulong`.

## Probe 04 verification

Every scalar in probe 04's 38 callback methods maps from the fixed-32-bit class:
- `LONG lLevel` (LogMessage, LogSwitch) → `int` ✓
- `ULONG ulReason` (LogSwitch) → `uint` ✓
- `ULONG32 oldILOffset` / `nOffset` / `contextSize` (FunctionRemapOpportunity, Exception, DataBreakpoint) → `uint` ✓
- `DWORD errorCode` / `dwError` / `dwFlags` and `CONNID dwConnectionId` → `uint` ✓
- `BOOL unhandled` / `fAccurate` → `int` ✓
- enums (`reason`, `dwEventType`) → `int` ✓
- all interface / `WCHAR*` / `BYTE*` / `IStream*` params → `nint` ✓

No 64-bit or pointer-sized scalar appears in the callback surface, so **probe 04 is correct as committed — no edit to the signatures needed.**

## Discipline for the engine

1. **Consult this table for every native type.** Never infer a size from the C type name's feel — `LONG` is 32-bit, not 64-bit, on every CoreCLR platform.
2. **The one trap to remember:** `LONG`/`ULONG` look like they'd follow native `long` (64-bit on LP64) but the PAL forces 32-bit. Map to `int`/`uint`, never `long`/`ulong`.
3. **Pointer-sized vs fixed:** `*_PTR` and `SIZE_T` are `nint`/`nuint`; everything else integer is fixed-width per the table.
4. **Probe 05 will hit the 64-bit cases:** `ICorDebugProcess::ReadMemory`/`WriteMemory` take `CORDB_ADDRESS` (= `ULONG64` → `ulong`) and a `SIZE_T`-ish size. `GetThreadContext` takes a platform `CONTEXT` blob (opaque `nint` + size). Map per this table when those signatures are transcribed.

## References

- `dotnet/runtime/src/coreclr/pal/inc/pal_mstypes.h` (read 2026-05-21) — the authoritative PAL fixed-size typedefs
- `dotnet/runtime/src/coreclr/inc/cordebug.idl` — `CONNID` (line 47), `CORDB_ADDRESS` (line 197)
- Finding 04 — `WCHAR`/`LPWSTR` is UTF-16 (16-bit) on the Unix PAL (`char16_t`, line 216)
- Finding 05 — interface-pointer params as `nint`, wrap lazily
- Probe 04 — `04-managedcallback-vtable-probe.cs` (mappings verified by this finding)
- Mercury session 2026-05-21 finding `com-type-mapping-verified`
