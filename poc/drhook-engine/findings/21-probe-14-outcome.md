# Finding 21: Probe 14 Outcome — PASSED: stack frame walk (inspection 5a)

**Status:**   **PASSED, 2/2.** The stop LOCATION is now observable: at a stop, the engine walks the
managed call stack of the stopped thread → method names. Probe 14 stopped at the `Worker.Tick`
breakpoint and read the stack — top frame `Worker.Tick`, caller `Program.<Main>$`. All frame and
metadata slots correct on the first functional run.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/14-stackframes-smoke.cs` + `11-bp-target.cs`
**Target:**   `Worker.Tick()` loop + setup `Debugger.Break()`; baseline dbgshim.

## Approach: GetCaller chain + reverse metadata

From the active frame, chase `ICorDebugFrame.GetCaller` up the stack (simpler than the chain/frame
enumerators). Each frame is named from its function token + module via the REVERSE of 4b's
resolution. All raw V-table; the only GUID is the reused `IID_IMetaDataImport`. Validated slots:

| Call | Interface | Slot |
|---|---|---|
| `GetActiveFrame` | ICorDebugThread | 15 |
| `GetFunctionToken` | ICorDebugFrame | 6 |
| `GetFunction` | ICorDebugFrame | 5 |
| `GetCaller` | ICorDebugFrame | 8 |
| `GetModule` | ICorDebugFunction | 3 |
| `GetMethodProps` | IMetaDataImport | 30 |
| `GetTypeDefProps` | IMetaDataImport | 12 |

`Frames.WalkManagedFrames` chases `GetCaller` (guarded at 256), releasing each frame. `FrameName`
takes the frame's `GetFunctionToken`; if it isn't an `mdMethodDef` (`0x06xxxxxx` — native/internal
frames), labels it `[external]`; otherwise `GetFunction`→`GetModule` feeds
`MetadataResolver.MethodName` (`GetMethodProps` → method name + declaring class token →
`GetTypeDefProps` → type name) → "Type.Method". The stop thread is surfaced from the pump
(`CallbackPump.StopThread`), captured before the stop is published.

## Run result (2/2)

```
stopped at Worker.Tick — call stack (2 frames):
  #0  Worker.Tick          ← where the breakpoint stopped us
  #1  Program.<Main>$      ← the caller (the target's loop)
PROBE 14 PASSED — top frame is Worker.Tick with 1 caller frame above it.
```

`Program.<Main>$` is the synthesized top-level-statements method — so metadata naming handles
compiler-generated names correctly, not just hand-written ones.

## What this proves

1. **The frame walk is correct** on CoreCLR 10 / macOS-arm64 — `GetActiveFrame` + `GetCaller`
   reaches every managed frame; `GetFunctionToken`/`GetFunction`/`GetModule` resolve each.
2. **Reverse metadata naming works** — `GetMethodProps` + `GetTypeDefProps` turn a token into
   "Type.Method", complementing 4b's name→token direction.
3. **"Where did we stop" is answered** — the call stack at any stop (breakpoint, step, Break) is
   readable via `DebugSession.GetStackFrames()`.

## Next (5b — variables)

For a frame, read locals + arguments: QI `ICorDebugFrame`→`ICorDebugILFrame`, then
`EnumerateLocalVariables`/`GetArgument` → `ICorDebugValue` → `GetType` (CorElementType) +
`ICorDebugGenericValue.GetValue` for primitives. Local NAMES live in the PDB (deferred — index-based
v1); argument names are in metadata. That makes "stopped here, with THESE values" observable.

## References

- Probe: `poc/drhook-engine/14-stackframes-smoke.cs`, `11-bp-target.cs`
- Fixture: `fixtures/14-stackframes-osx-arm64-20260522T034844Z.txt`
- Engine: `src/DrHook.Engine/Interop/Frames.cs`, `MetadataResolver.MethodName`, `DebugSession.GetStackFrames`, `CallbackPump.StopThread`
- Findings 19 (breakpoints — the stop), 18 (4b metadata name→token — this is the reverse), 16 (stopping model)
- Mercury session 2026-05-21 observation `probe-14-passed`
