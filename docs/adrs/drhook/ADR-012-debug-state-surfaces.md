# ADR-012: Debug-state surfaces — a surface-agnostic model and its first human views (TUI dashboard, Avalonia sibling)

**Status:** Proposed — 2026-06-03 (Emergence / ideation); **Phases 1–2 built — 2026-06-26** — the surface-agnostic model + the read-only transport + the first console view (`DrHook.Wire` / `DrHook.Viz` / `DrHook.Viz.Console`) shipped and dogfooded end-to-end, resolving Q1/Q4/Q6/Q7 + the lifecycle half of Q3. **Phase-2 enrichment — 2026-06-27:** the execution position now carries a *structured* source location (full path + line per frame), the substrate enabler for source-on-step rendering. **Phase 4 — Accepted (2026-06-27):** the source-on-step rendering approach (source pane + typed value rendering) is validated end-to-end, dogfooded live through the real DrHook MCP; the full TUI surface + Q2 are the remaining Engineering. Phases 3, 5, 6, and Q2/Q5 + the command half of Q3, remain Proposed; the whole ADR stays **Proposed** until its Open Questions resolve.

**Realizes:** [ADR-011](ADR-011-lifecycle-console-dashboard.md) D4 (DrHook owns the terminal/dashboard surface) and D5 (a surface-agnostic debug-state model, not "a dashboard"), which ADR-011 deferred behind its Q6 (ship D2 + D3 first, let the dashboard's demand and form emerge). F-010-2 (Owned detach-leave-running) is now closed end-to-end (ADR-011 lifecycle triad complete on macOS; findings 78–80), so the next DrHook phase is the human surface.

## Epistemic note — read first

This ADR is **ideation** (the EEE Emergence phase). Unlike ADR-010/011 it is not yet structured for ship: it commits to an architectural **seam** and sketches the views, but deliberately under-specifies the views themselves. Martin's standing directive ([`project_mira_multisurface_trajectory`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_mira_multisurface_trajectory.md)) governs the *shaping*; his methodology governs the *pace*: **"build a flexible, simple primitive at each phase and discover the routing nuances by using the surface — do not over-specify the dashboard up front. Substrate first. Impatience does not benefit us."**

The reason to commit the *seam* now and not the *views*: the consumers are **named** (the agent via MCP today; a console/TUI dashboard and an Avalonia GUI as Mira views; a full IDE on the horizon). Named consumers mean the seam is built up front, not speculatively — [`feedback_infrastructure_no_yagni`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_infrastructure_no_yagni.md). The views are *not* named in detail, so they are sketched, and their nuances are discovered in use.

Every factual claim about today's substrate is grounded in a code read (`file:line`). Proposed-but-uncertain points carry `[?]`. The Open Questions MUST be resolved before Proposed → Accepted.

## Context

### The trajectory (the directive)

Per the 2026-06-01 directive and the **Mira** component of the Sky Omega architecture (surface / interaction layer): **console, TUI, and an Avalonia GUI are all eventual _Mira_ views**, and the DrHook + Mira trajectory is toward a full-blown IDE. The substrate must be shaped now so multiple views — possibly simultaneous, possibly remote — plug in without ever forcing a refactor. The first human rendering is *one view of the model*, not the model itself.

The thesis payoff is specific: DrHook's distinguishing capability over a classical debugger is the **`(hypothesis, observation)` braid** — every state-changing or state-reading operation carries a hypothesis (ADR-010 Decision 5), and the (hypothesis, observation) pairs are the corpus a cognitive-layer analyzer consumes. A human surface that renders that braid live makes the agent's cognitive loop **legible** — governed automation made concrete ([`project_governed_automation_thesis`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_governed_automation_thesis.md)). No IDE's debugger console shows this.

### What exists today (factual, code-read)

The substrate already emits **surface-agnostic events** and already supports **multiple consumers** — the seam is half-built:

- **`IDebugEventSink`** (`src/DrHook.Engine/IDebugEventSink.cs`) is the event contract: `OnEvent(string)` (`:14`), `OnLog(LogRecord)` (`:23`), `OnAnomaly(EngineAnomaly)` (`:45`), and `OnConsoleOutput(ConsoleOutputRecord)` (ADR-011 D3; `src/DrHook.Engine/BoundedConsoleSink.cs:56`). The substrate emits; it knows nothing about views.
- **`CompositeEventSink`** (`src/DrHook.Engine/CompositeEventSink.cs:13`) **already fans one event stream to N sinks** — the multi-consumer fan-out primitive exists. A new view is just another `IDebugEventSink`.
- **`BoundedConsoleSink` / `BoundedAnomalySink` / `BoundedLogSink`** are bounded ring buffers drained on demand — the buffered-stream pattern.
- The **queryable** state is reachable today only **piecemeal**, through individual MCP tools (`drhook_locals`, `drhook_break_list`, `drhook_snapshot`, `drhook_drain_console` / `_log` / `_anomalies`). There is **no single unified queryable debug-state model**.
- The **only consumer today** is the MCP server (`src/DrHook.Mcp/EngineSteppingSession.cs`), and it is a **stdio JSON-RPC server** — `Program.cs:91` builds it `WithStdioServerTransport()`, so **its stdout is the protocol channel**. It therefore **cannot host a TUI on its own process** (the same constraint that forced ADR-011 D2's console isolation).
- The **`(hypothesis, observation)` braid is not yet recorded** as substrate state: a hypothesis is passed into each MCP tool and echoed in the response, but no persistent (hypothesis, observation) log exists in `DrHook.Engine`.

### The constraint that shapes everything (inherited from ADR-011 D2 / Q2)

A protocol-channel debugger **cannot host a human surface on its own channel**, and MCP has no reverse-request / terminal-hosting (ADR-011 Q2, confirmed against the MCP 2025-11-25 spec). Therefore the human surface **must be a separate process** that consumes the debug-state over a **transport**, decoupled from the MCP JSON-RPC channel. This is the same decoupling ADR-011 D2 forced for debuggee console I/O; the dashboard generalizes it to *all* debug-state.

## Decision (proposed)

### D1 — The surface-agnostic debug-state model is the load-bearing artifact

A single model the substrate produces, knowing nothing about views, with **two faces**:

- a **snapshot** — a queryable, point-in-time view; and
- a **delta stream** — live events (the existing `IDebugEventSink`).

Built by **extending** the existing sink + bounded-buffer machinery, **not** replacing it. Contents:

- **Session / lifecycle**: Owned vs Borrowed, active, stopped vs running, target pid + assembly version.
- **Execution position**: current stop (reason), top frame, call stack.
- **Inspection**: locals / arguments at the current stop (on demand — depth-bounded per `MaxInspectionDepth`).
- **Breakpoints + exception filters**: with their policies, conditions, hit counts.
- **Streams**: console output (D3), logpoints, anomalies.
- **The braid**: the `(hypothesis, observation)` log — the running cognitive loop. *This is new substrate state* (see D4) and the element that distinguishes DrHook's surface from any IDE's.

### D2 — A transport carries the model to multiple, possibly-remote consumers

Lean to a **local socket** (Unix domain socket on POSIX) over a shared file, for live, low-latency, **bidirectional** updates — debug-state **out** (deltas + snapshot-on-request) and commands **in** (D5: the dashboard *controls*, it is not read-only). The fan-out already exists (`CompositeEventSink`); the new piece is a sink that **serializes the stream onto the transport** plus a command reader.

Topology is an Open Question (Q1): the lightest path is **MCP-server-as-host** — the process that already hosts `DrHook.Engine` adds a publishing sink + a command socket, so the dashboard is a pure peer consumer with no broker. The cleaner-long-term path is a **separate DrHook host/broker** that both the MCP server and the dashboard connect to (decouples the human surface from the agent's transport entirely). Resolve by building the lighter one first and feeling the seams.

### D3 — Views are pluggable consumers; ship ONE simple reference view first

- The **agent** (via MCP) is a consumer today.
- The **first human view**: a **simple console / TUI dashboard** that *visualizes* the model **and** *controls / interacts with* the debugger (Martin's three verbs). Keep it minimal; grow it by use.
- The **Avalonia GUI** is the sibling — the **same** model + transport, richer rendering.
- A full IDE is the horizon.

No view is baked into the substrate; each is one rendering of the model. The model's requirements feed back from the views, but the model — not any view — is the artifact under version control as the contract.

### D4 — The `(hypothesis, observation)` braid is first-class, and recording it is new substrate work

The substrate begins **recording** the `(hypothesis, observation)` pairs into the debug-state model (today they pass through MCP requests/responses unrecorded). The dashboard surfaces this braid **prominently** — the human watches the agent reason in real time. This both (a) makes the cognitive loop legible (governed automation, human-in-the-loop) and (b) produces the in-substrate corpus the eventual cognitive-layer analyzer consumes (ADR-010's "hypothesis as cognitive-loop seam" insight). This is the surface no classical debugger has.

### D5 — One shared session; the human and the agent debug *together, not in parallel*

The dashboard and the agent drive the **same** `DebugSession` — not two sessions, not a parallel context. Commands from both surfaces are **serialized by the substrate** (the pump is already a single-consumer FIFO — ADR-007 finding 58), and **both surfaces render the same model**. When the agent steps, the human's view updates; when the human sets a breakpoint, the agent's next operation sees it. This is [`feedback_together_not_parallel`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_together_not_parallel.md) made concrete in the debugging surface — one working frame, shared. The conflict/coordination semantics (who has the "stop", what happens to an in-flight agent operation when the human continues) are discovered in use (Q3).

### Sovereignty: model + transport are BCL-only; views may use frameworks

The **debug-state model and the transport are BCL-only** — they are substrate, and substrate keeps semantic sovereignty (BCL + P/Invoke, as `DrHook.Engine` already is). The **views are application-layer surfaces** and may use view frameworks: **Avalonia** for the GUI is settled (Martin). The **TUI technology is an Open Question** (Q2) — a BCL-`Console`-only renderer keeps the first view sovereign and matches "simple", versus a TUI library for speed. This mirrors the Mercury rule: core BCL-only, surfaces/tooling may use packages.

## Phasing (EEE — one unknown per phase; discover the rest in use)

1. **Phase 1 — The model + a read-only tap. ✅ BUILT (2026-06-26).** `DebugStateSnapshot` (the unified, self-contained snapshot — renderable mid-session by a view connecting alone) + `DebugStateDelta` (the live stream atop `IDebugEventSink`) + a `DebugStateTapSink` on the existing `CompositeEventSink`; `DebugSession.CaptureState` assembles the snapshot from the bounded buffers (non-destructive `Peek`). Pure substrate; smallest — done first.
2. **Phase 2 — The transport (read-only). ✅ BUILT (2026-06-26).** `DebugStateServer` (`src/DrHook.Engine/Transport/`) publishes snapshot-on-connect + a delta stream over a Unix-domain socket, wired into `EngineSteppingSession` as a 4th composite sink (`PublishTransportSnapshot` after each stop). The consumer overshot the planned trivial `drhook-tail`: a layered **`DrHook.Wire` (protocol contract) ← `DrHook.Viz` (client library) ← `DrHook.Viz.Console` (view)** stack — a structured console view that renders the full model (a down-payment on Phase 4, minus the braid). Proved **multiple live consumers decoupled from the MCP channel**, dogfooded end-to-end through the real DrHook MCP. The urgent correctness property held: the MCP JSON-RPC channel stays clean (the D2 lesson generalized).
   - **Phase-2 enrichment — structured execution location (✅ BUILT 2026-06-27; "A now, B in Phase 4" — Martin).** The wire carried the current position only as the flattened `"Type.Method @ basename:line"` strings of `CallStack` — a view could show *where* execution stopped but not *open* the file (`DebugSession.GetStackFrames` resolved the full path at `:765`, then `Path.GetFileName` discarded it). A new `FrameLocation(Display, File, Line)` (engine) / `WireFrame` (wire) carries the **full** source path + line per frame; `ExecutionPosition.CallStack` and `WirePosition.CallStack` are now structured (the string `TopFrame` dropped — top frame is `CallStack[0]`), mirroring how `WireBreakpoint` already carried structured `File`/`Line`. `GetStackFrames()` (strings, MCP path) becomes a `.Display` projection over the structured `GetStackFrameLocations()`. Pure substrate / sequencing-neutral defect fix — it does **not** touch Phase 3 (the braid). Verified: `DrHook.Wire.Tests` 4/4, `DrHook.Viz.Tests` 6/6, `DrHook.Engine.Tests` 154/154, and the live `CaptureStateSnapshotTest` integration (the structured `File`/`Line` resolve from the target's real PDB: top frame `…/Program.cs` at the marker line).
3. **Phase 3 — Record the braid (D4).** Begin persisting `(hypothesis, observation)` pairs into the model; surface them on the tap.
4. **Phase 4 — The TUI dashboard, read-only. — Accepted (2026-06-27).** Render the model — execution position, stack, locals, breakpoints, console/log/anomaly streams, and the braid. **Includes source-on-step rendering (the "B" of the source-view increment):** a view reads the file from disk by the structured `WireFrame.File`+`Line` (Phase-2 enrichment) — the transport is a local UDS, so the view is co-located and the source tree is reachable — and renders a window around the current line with the stopped line marked. Drift guard (verify the disk file against the Portable PDB's source checksum; DrHook already reads embedded PDBs) is a hardening follow-up, not v1. **Started 2026-06-27:** the view-agnostic source-window *primitive* — `SourceWindowReader` + the `SourceWindow` render-model + a bounded-LRU `SourceFileCache`, in `DrHook.Viz` — is built and unit-tested (`DrHook.Viz.Tests` 15/15). It returns a model (lines, which is current, and a *named* status for missing / oversized / out-of-range / no-location — best-effort, never throws), so it is **independent of Q2** (the same primitive feeds a BCL-Console TUI, a library TUI, or the Avalonia GUI). **Console view wired — 2026-06-27 (Martin: "console view first"):** `DrHook.Viz.Console` now renders a source pane between the frame and locals lines — a `source : <basename>` header, then a windowed listing with `►` on the stopped line; a missing / drifted file shows a short note, never an error (`DrHook.Viz.Tests` 17/17). The first render surface is therefore the existing reference console view; the **full TUI dashboard + the Q2 framework choice remain** for the larger Phase-4 build. Typed value rendering followed (the two live-dogfood findings, closed to completion): object/array/value-type references render as their runtime type — `this={Worker}`, generics `tags={List<String>}` — and a null reference as `null`, never a bare `?`. **Status — Accepted (2026-06-27):** the source-on-step *approach* is validated end-to-end (source pane + typed value rendering dogfooded live three times through the real DrHook MCP; commits `41aaaab`→`06c48a0`). What remains is the **Engineering** of the full TUI surface, gated on **Q2** (TUI technology) — so the phase is **Accepted (Epistemics)**, not Completed. The whole ADR stays **Proposed** while Q2/Q5 + the command half of Q3 are open.
5. **Phase 5 — Control (commands in).** The transport goes bidirectional; the TUI drives break / step / continue / detach. The shared-session concurrency nuances (D5 / Q3) are discovered here.
6. **Phase 6 — The Avalonia GUI sibling.** Same model + transport, richer view. Confirms the seam admits a second, structurally different view with no substrate refactor — the directive's acceptance test.

## Open questions (resolve before Proposed → Accepted)

> **Resolution log — 2026-06-25 (architect directive, Martin).** *"The mcp debugger is owned by the LLM. Views should be standalone, started by the human and connectable to a session. Human can terminate a view without affecting an ongoing debugging session."* This resolves **Q1**, **Q6**, and the **lifecycle half of Q3**, and surfaces a new **Q7 (rendezvous)**. Q2, Q4, Q5, and the command-serialization half of Q3 remain open.
>
> **Phase 2 build — 2026-06-25.** Resolves **Q4** (wire format → newline-delimited JSON via `System.Text.Json`, through a thin **wire-DTO layer** that decouples the wire contract from the internal domain records) and **Q7** (rendezvous → a fixed well-known **Unix-domain-socket** path, one active session at a time). A correctness decision settled here: the **transport handles only immutable snapshots + deltas** — the session *driver* (the MCP request thread that owns stepping) captures the snapshot after each stop and pushes it; the transport never calls into `DebugSession`, so there is no transport↔stepping concurrency hazard. The listener is **per-session** (started on launch/attach, closed on session end), matching "views connect to *a session*". **Q2** (TUI tech), **Q5** (PTY), and the command-serialization half of **Q3** remain open.

1. **Transport topology** — ~~MCP-server-as-host vs a separate DrHook broker~~ → **Resolved (2026-06-25): MCP-server-as-host, no broker.** The session-owning MCP/engine process is the **listener**; human-launched views are **connect-in clients**. The host *accepts* connections — it never *spawns* a view. The cleaner-long-term broker is explicitly declined for now.
2. **TUI technology** — BCL-`Console`-only (sovereign, "simple") vs a TUI library? (Avalonia is settled for the GUI.) *(Open.)*
3. **Shared-session control semantics (D5)** — **Lifecycle half resolved (2026-06-25):** a view's lifecycle is independent of the session's; terminating a view never affects the ongoing session; views are ephemeral peers, the LLM-owned session is authoritative. **Still open:** when a view *sends commands* (Phase 5), how do agent and human commands serialize and display — explicit "who holds the stop", or pure FIFO? What happens to an in-flight agent operation when the human continues/stops?
4. **Wire format** — ~~JSON line-protocol vs binary vs MCP shapes~~ → **Resolved (2026-06-25, Phase 2): newline-delimited JSON** (one message envelope per line: `{"type":"snapshot"|"delta", …}`) via `System.Text.Json` (BCL — ships in the shared framework, so the transport stays BCL-only). Serialized through a **thin wire-DTO layer**, not the domain records directly: the wire format is the contract every view depends on, so it is deliberate and decoupled from internal record shapes / renames (and sidesteps `System.Text.Json` polymorphism on `BreakpointInfo` and the `object?` of `LocalValue.RawValue`).
5. **Console (PTY) + debug-state in one surface?** — does the dashboard also host the debuggee's PTY (ADR-011 D4), giving one surface for both the program's console *and* the debug-state, or are they separate panes/processes? *(Open.)*
6. **Dashboard lifecycle** — ~~attach vs launch/own~~ → **Resolved (2026-06-25): attach only.** Views connect to a running, LLM-owned session; the agent owns sessions. Human-driven debugging with no agent present is not the near-term model.
7. **Rendezvous (new — 2026-06-25)** — ~~how does a human-launched view discover the session?~~ → **Resolved (2026-06-25, Phase 2): a fixed well-known Unix-domain-socket path** (`~/Library/SkyOmega/drhook/session.sock` — the Sky Omega data-dir convention), no registry/broker. One active session at a time (DRHOOK.md), so a fixed path is the simplest rendezvous; the listener unlinks a stale socket on bind. Multi-session keying-by-pid is a later refinement. (Windows AF_UNIX / Linux XDG path follow the engine's POSIX-first pattern.)

## Consequences

- Realizes ADR-011 D4 + D5 as a concrete, phased line of work, now that F-010-2 / the lifecycle triad is closed.
- The surface-agnostic seam means console / TUI / Avalonia / IDE never force a substrate refactor — the directive's whole point.
- Turns the MCP-can't-host-a-terminal *constraint* into a thesis-aligned *feature*: a human window into the agent's cognitive loop. The `(hypothesis, observation)` braid (D4) is the part no classical debugger surface has.
- **DrHook + Mira → IDE**: this ADR defines the seam where Mira's surfaces plug in. The TUI shipped here is DrHook's reference view; Mira's console / TUI / Avalonia views (and eventually a full IDE) consume the same contract.
- Adds substrate state (the braid, D4) and a long-lived transport — both are new resource-lifecycle surfaces and must be accounted under [`feedback_resource_limit_class_audit`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md) (socket fds, per-consumer buffers, the braid's bounded growth).
- Cross-platform is a fluent goal: macOS-arm64 first to production, Windows/Linux validated later (ADR-007 Phase 9). The transport (Unix domain socket) and any P/Invoke follow the engine's existing POSIX-first / Windows-deferred pattern.

## References

### Code reads (factual basis)
- `src/DrHook.Engine/IDebugEventSink.cs:14/23/45` — the `OnEvent` / `OnLog` / `OnAnomaly` event contract; `src/DrHook.Engine/BoundedConsoleSink.cs:56` — `OnConsoleOutput` (D3).
- `src/DrHook.Engine/CompositeEventSink.cs:13` — the existing multi-consumer fan-out primitive.
- `src/DrHook.Mcp/EngineSteppingSession.cs` — the sole consumer today; `src/DrHook.Mcp/Program.cs:91` — `WithStdioServerTransport()` (stdout = protocol channel, so the MCP process cannot host a TUI).

### Prior ADRs / memory
- [ADR-011](ADR-011-lifecycle-console-dashboard.md) D2 / D4 / D5 / Q2 — the protocol-channel decoupling and the deferred dashboard this ADR realizes.
- [ADR-010](ADR-010-mcp-tool-surface-redesign.md) — hypothesis as the cognitive-loop seam (the braid's origin).
- Memory: [`project_mira_multisurface_trajectory`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_mira_multisurface_trajectory.md) (the directive), [`project_governed_automation_thesis`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_governed_automation_thesis.md), [`feedback_infrastructure_no_yagni`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_infrastructure_no_yagni.md), [`feedback_together_not_parallel`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_together_not_parallel.md).
