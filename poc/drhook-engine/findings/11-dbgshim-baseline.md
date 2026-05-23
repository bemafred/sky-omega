# Finding 11: dbgshim Baseline — adopt `Microsoft.Diagnostics.DbgShim`

**Status:**   Baseline adopted. `Microsoft.Diagnostics.DbgShim` (official NuGet native asset) is the canonical dbgshim for the PoC and the engine, replacing the borrowed VS Code `.debugger` copy. The dbgshim library is ruled out as the cause of probe 05's callback-delivery blocker (corrected reasoning — see Correction below; the original "identical-to-VS-Code-copy" comparison no longer holds, but the conclusion is independently confirmed by probe 06 / finding 13).
**Date:**     2026-05-21
**Correction (2026-05-23):** The "Local copy" row in the table below originally claimed `poc/drhook-engine/.local-dbgshim/libdbgshim.dylib` was extracted from the `Microsoft.Diagnostics.DbgShim` NuGet package. That provenance was false — the file there had been scavenged from a VS Code C# extension install on the dev machine, not extracted from the NuGet. The folder has been retired (2026-05-23); both `DrHook.Engine.DbgShim.Resolve` and the local `ResolveDbgShim` in probes 02–06 now walk the NuGet cache (`~/.nuget/packages/microsoft.diagnostics.dbgshim.<rid>/`) directly. The revalidation chain in this finding ("identical to the VS Code-copy result, therefore library-independent") is weakened in proportion — the two runs may have exercised the same upstream binary with different labels. The conclusion that dbgshim is not the cause of the callback-delivery blocker still holds, but via **probe 06 (finding 13)** — which validated callback delivery using `[UnmanagedCallersOnly]` vtables against the same dbgshim — not via the original revalidation comparison.
**Decision (Martin):** "Adopt `Microsoft.Diagnostics.DbgShim` — it must be considered the baseline. We start with that and revalidate."

## The baseline

| | |
|---|---|
| Package | `Microsoft.Diagnostics.DbgShim.osx-arm64` (RID-specific native-asset package) |
| Version | **9.0.661903** (latest official on nuget.org) |
| Payload | `runtimes/osx-arm64/native/libdbgshim.dylib` — Mach-O 64-bit arm64, 396,624 bytes |
| Cache path | `~/.nuget/packages/microsoft.diagnostics.dbgshim.<rid>/<version>/runtimes/<rid>/native/libdbgshim.dylib` — populated automatically by `dotnet restore` (the engine references the package per-RID via `<PackageReference>`); both `DrHook.Engine.DbgShim.Resolve` and the local `ResolveDbgShim` in probes 02–06 walk this cache directly. Nothing checked in, nothing copied. |

**Versioning note:** the DbgShim package line is `9.0.x` — it tracks the `dotnet/diagnostics` tooling generation, not the runtime it debugs. dbgshim is forward-compatible (it inspects the target, detects its runtime, and loads that runtime's `mscordbi`), which is why `9.0.661903` debugs a `.NET 10.0.0` target. When a `10.0.x` DbgShim ships, bump the baseline; until then `9.0.661903` is current.

Per [ADR-009 clarification](../../docs/adrs/ADR-009-substrate-dependency-policy.md), dbgshim is a **native runtime-substrate asset**, not a managed dependency — the four-axis test doesn't apply; it's part of the .NET debugging substrate, sourced from its official redistribution vehicle.

## Obtain (reproducible)

Building `DrHook.Engine` once triggers NuGet restore for the per-RID dbgshim package and lands the native asset in the cache:

```bash
dotnet build src/DrHook.Engine/DrHook.Engine.csproj
```

From then on, both `DrHook.Engine.DbgShim.Resolve` (probes 07+) and the local `ResolveDbgShim` in probes 02–06 find `libdbgshim.dylib` automatically under `~/.nuget/packages/microsoft.diagnostics.dbgshim.<rid>/<version>/runtimes/<rid>/native/`. `DBGSHIM_PATH` remains as an explicit override for testing a custom build.

## Revalidation result — the library is RULED OUT

*(See the Correction at the top — the 2026-05-23 audit found that the file labelled "official 9.0.661903" in this re-run was likely the same VS Code-shipped binary used in finding 10. The conclusion holds via probe 06 / finding 13, not via this comparison.)*

Probe 05 re-run against `9.0.661903`-labelled dbgshim, event-generating target:

```
DebugActiveProcess: hr=0x00000000      (attach S_OK)
attached   : ICorDebugProcess 0x102F33B18
callback   : none within 15s (attached but no dispatch)
Detach     : hr=0x00000000
Terminate  : hr=0x00000000
```

**Identical to the VS Code-copy result** (finding 10): attach / detach / terminate all `S_OK`, zero callbacks delivered. So:

- **Candidate cause #2 (dbgshim version/library mismatch) is ELIMINATED.** The official, current, version-appropriate dbgshim behaves exactly like the borrowed one. The callback-delivery blocker is **library-independent**.
- By elimination, the blocker is **candidate cause #1: the event-transport / threading model** — ICorDebug on Unix is out-of-process via `mscordbi`, and callback delivery needs whatever event-loop / transport servicing our single-thread `Wait()` probe doesn't provide. (Candidate #3 macOS event-channel and #4 attach-completion remain possible but are downstream of understanding the delivery model.)

This is exactly why adopting the baseline first was the right call: it cheaply and definitively removed the library as a variable before we invest in the event-loop investigation.

## Next (unchanged target, now unambiguous)

Read how **netcoredbg receives post-attach events** — its event loop, the thread `mscordbi` delivers callbacks on, any attach-completion handshake. With the library ruled out, this is the single remaining hypothesis to resolve. netcoredbg demonstrably gets post-attach callbacks (finding 04: it waits on a CV set *by* a callback), so the mechanism is real and in its source.

## References

- Package: `Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903 (nuget.org)
- Finding 10 — probe 05 partial outcome (the blocker); candidate cause #2 now ruled out by this finding
- [ADR-009 clarification](../../docs/adrs/ADR-009-substrate-dependency-policy.md) — dbgshim as native runtime-substrate asset
- [ADR-006](../../docs/adrs/drhook/ADR-006-drhook-engine.md) — engine bundles dbgshim via this NuGet
- Mercury session 2026-05-21 finding `dbgshim-baseline-adopted`
