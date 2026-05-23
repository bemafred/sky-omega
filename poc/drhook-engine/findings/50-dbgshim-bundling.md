# Finding 50: `libdbgshim` bundling via per-RID NuGet packages

**Status:**   **DONE** — `DrHook.Engine` references per-RID `Microsoft.Diagnostics.DbgShim.<rid>`
conditionally on the dev's SDK RID; `DrHook.Mcp` references all RIDs unconditionally so the
global tool ships with every platform's shim. `DbgShim.cs` resolver gained a primary lookup at
`AppContext.BaseDirectory/runtimes/<rid>/native/libdbgshim.<ext>`. `DBGSHIM_PATH` stays as the
explicit override. Probes 18/26/36/39/40 + the 47-test unit suite all pass without the env var.
**Date:**     2026-05-23
**Files:**    `src/DrHook.Engine/DrHook.Engine.csproj`, `src/DrHook.Mcp/DrHook.Mcp.csproj`,
              `src/DrHook.Engine/Interop/DbgShim.cs`

## The gap

Before this slice, no `Microsoft.Diagnostics.DbgShim*` package was referenced anywhere. Probes
relied on `DBGSHIM_PATH` pointing at a manually-downloaded `.local-dbgshim/libdbgshim.dylib`;
the global `drhook-mcp` install had **no shim at all** and would die at runtime on first attach.
`DrHook.Engine.csproj` carried a comment acknowledging this as a "packaging refinement (Phase 1
follow-up)" — landing now, before the Phase 3 SessionManager rewrite makes the gap user-visible.

## Per-RID, not the meta package

`Microsoft.Diagnostics.DbgShim` (no RID) is a meta/rollup that pulls every per-RID package
transitively. Convenient but **always** fetches every platform's asset (~10 MB of native binaries
your local build will never use). Per-RID packages have **no transitive dependencies** (verified
from the osx-arm64 nuspec — no `<dependencies>` block), so referencing them individually gives
us precise control:

```xml
<!-- DrHook.Engine.csproj — only the dev's RID, kept lean for local builds -->
<PackageReference Include="Microsoft.Diagnostics.DbgShim.osx-arm64" Version="9.0.*"
                  Condition="'$(NETCoreSdkRuntimeIdentifier)' == 'osx-arm64'" />
<!-- + one PackageReference per supported RID, each gated by the same NETCoreSdkRuntimeIdentifier check -->
```

```xml
<!-- DrHook.Mcp.csproj — ALL RIDs unconditionally so `dotnet tool install -g drhook-mcp`
     works on any platform. ~10 MB total across the seven RIDs we ship. -->
<PackageReference Include="Microsoft.Diagnostics.DbgShim.osx-arm64"      Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.osx-x64"        Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.linux-x64"      Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.linux-arm64"    Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.linux-musl-x64" Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.win-x64"        Version="9.0.*" />
<PackageReference Include="Microsoft.Diagnostics.DbgShim.win-arm64"      Version="9.0.*" />
```

Note from Microsoft's nuspec: "Internal implementation package not meant for direct consumption.
Please do not reference directly." We reference them anyway — this is the same pattern
`dotnet-trace`, `dotnet-counters`, etc. use; the "don't reference directly" warning is aimed at
end-user app authors who'd typically take the meta. For a debugger consumer, per-RID is correct.

## Resolver gap discovered + fixed

First test of bundling: probe 40 PASSED without `DBGSHIM_PATH`, but via the **NuGet-cache fallback**
in `DbgShim.cs`'s resolver, not via `AppContext.BaseDirectory`. Why? The package deploys to
`bin/<config>/<tfm>/runtimes/<rid>/native/libdbgshim.<ext>`, but the resolver only looked at
`bin/<config>/<tfm>/libdbgshim.<ext>` (the flat-layout convention). Fix: add a new lookup that
constructs the canonical `runtimes/<rid>/native/` path:

```csharp
string rid = RuntimeInformation.RuntimeIdentifier;
string runtimesNative = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libName);
if (File.Exists(runtimesNative)) return runtimesNative;
```

After the fix, the resolver's order is:
1. `DBGSHIM_PATH` env var (explicit override)
2. **`AppContext.BaseDirectory/runtimes/<rid>/native/libdbgshim.<ext>`** (primary — bundled via PackageReference)
3. `AppContext.BaseDirectory/libdbgshim.<ext>` (flat layout, rare; safety net)
4. `RuntimeEnvironment.GetRuntimeDirectory()` (pre-.NET-7, defunct)
5. NuGet cache walk (legacy / naked builds)

## What this changes for dev

Before: every command needed `DBGSHIM_PATH=$PWD/.local-dbgshim/libdbgshim.dylib dotnet …`, and
`.local-dbgshim/libdbgshim.dylib` had to be manually placed there (it was — but new contributors
would hit `DllNotFoundException` until they sorted it out).

After: just `dotnet 40-arrays-smoke.cs …`. The shim deploys automatically with the engine
PackageReference; the resolver finds it.

`.local-dbgshim/` is untracked in git and now redundant — kept on disk as a manual override
target for `DBGSHIM_PATH` if anyone needs to test a custom build of dbgshim, but no longer the
default path.

## Verified

- Probes (clear runfile cache + no `DBGSHIM_PATH`): 18 locals, 26 exception, 36 subclass-walk,
  39 fields, 40 arrays — all PASS.
- 47 unit tests pass.
- `DrHook.Mcp` build deploys all seven RIDs' native assets to `bin/Debug/net10.0/runtimes/…/native/`
  (the package's runtime-targets metadata handles it; nothing custom needed).

## Pre-staging in `DrHook.Mcp` (note)

`DrHook.Mcp` currently references the netcoredbg-based `..\DrHook\DrHook.csproj`, NOT
`DrHook.Engine`. The dbgshim native assets it ships are **dead weight** until the Phase 3
SessionManager rewrite switches the MCP backend to `DrHook.Engine`. Accepted cost: ~10 MB of
unused binaries in the global tool now, in exchange for zero packaging churn at switchover. If
the SessionManager rewrite slips significantly, revisit (split the tool package, or move the
multi-RID refs into a separate tool that only ships post-switchover).

## Scope / next

ADR-006 Phase 3 work remaining:

- `SteppingSessionManager` rewrite backed by `DebugSession` — the slice that actually consumes
  the dbgshim bundling on the MCP side.
- Per-MCP-tool regression suite + netcoredbg retirement.
- Polish: Launch Terminate-on-dispose, stdout/stderr capture, multi-dim arrays,
  generic-type-parameter naming.

## References

- `src/DrHook.Engine/DrHook.Engine.csproj` — per-RID conditional refs.
- `src/DrHook.Mcp/DrHook.Mcp.csproj` — multi-RID for the tool.
- `src/DrHook.Engine/Interop/DbgShim.cs` — `Resolve` updated with `runtimes/<rid>/native/` lookup.
- ADR-006 line 113 updated to reflect bundled state.
- Memory: `feedback_filebased_app_stale_cache` (still applies for engine edits → re-running a
  probe still needs the runfile cache cleared if the engine changed).
- Mercury session 2026-05-22 observation `dbgshim-bundling`
