# ADR-011: Debug-session lifecycle (stop / detach / kill) and debuggee console I/O — isolation, surfacing, and the DrHook dashboard

**Status:** Completed — 2026-06-28 (Proposed — 2026-05-31 → Accepted — 2026-06-01 → Completed). D1 (lifecycle triad stop/detach/kill) / D2 (launched-debuggee console isolation) / D3 (drain seam) shipped at **1.8.3**; D4 (dashboard) + D5 (surface-agnostic debug-state model) were deferred behind Q6 and are carried by [ADR-012](ADR-012-debug-state-surfaces.md) (which "Realizes ADR-011 D4 + D5"). This ADR's lifecycle + console scope is complete; the dashboard surface lives in ADR-012.

**Revises:** [ADR-010](ADR-010-mcp-tool-surface-redesign.md) lifecycle decisions (Q2 + the Session-lifecycle tool catalog). ADR-010 stays Accepted; this ADR refines the lifecycle verbs it shipped in Tier 1 and adds the debuggee-console-I/O concern ADR-010 did not address.

## Epistemic note — read first

Like ADR-010, this is structured for review, not ship. Every factual claim about the current substrate is grounded in a code read (cited `file:line`); every claim about IDE/protocol behavior is grounded in cited research (a four-strand survey of Visual Studio, VS Code, the Debug Adapter Protocol, and JetBrains Rider, 2026-05-31). Proposed-but-uncertain points carry `[?]`. The Open Questions MUST be answered before Proposed → Accepted.

Two findings motivate this ADR, one urgent:

1. **A confirmed latent correctness bug** (urgent): a launched debuggee's console output corrupts the MCP JSON-RPC channel.
2. **The lifecycle verbs ADR-010 shipped are mis-named** relative to established convention — and the console concern needs a home.

## Context

### The urgent finding — debuggee stdout collides with the MCP protocol channel (factual, code-read)

`DrHook.Mcp` runs as a **stdio JSON-RPC MCP server** — `Program.cs` builds it `WithStdioServerTransport()` (`Program.cs:91`), so the server's **stdout is the protocol channel** to the client (Claude Code).

`drhook_launch` (Owned) spawns the debuggee via `DbgShim.LaunchWithDebugger` → `CreateProcessForLaunch(commandLine, suspend: TRUE, env: null, cwd, out pid, out resumeHandle)` (`Interop/DbgShim.cs:237`). **No stdio handles are redirected** — the launched child inherits the MCP server process's stdin/stdout/stderr unmodified. Therefore a debugged `Console.WriteLine` (or any write to fd 1/2) is injected **directly into the MCP server's stdout**, corrupting the JSON-RPC frame stream → protocol desync → tool failure.

This is **already known and explicitly deferred**: `EngineSteppingSession.cs:171` and [ADR-006](ADR-006-drhook-engine.md):149 both record *"stdout/stderr isolation (child currently inherits parent fds, leaking target output — fix is dedicated pipes through CreateProcessForLaunch)"* as "Phase 3 polish." No capture/redirect exists today: the `IDebugEventSink` surface (`OnEvent` / `OnLog` / `OnAnomaly`) carries **debugger telemetry and logpoint output, not debuggee console I/O**.

**Empirical corroboration (EEE, found-in-use):** the ADR-010 Tier 1 smoke launched `examples/drhook-verify.cs`, which executes five `Console.WriteLine`s in `DoWork` before the stop that surfaced. The MCP responses returned as clean JSON anyway — meaning the client *tolerated* the interleaved non-JSON lines. The bug is real; it was masked by client leniency, not avoided. A verbose target, or output landing mid-frame, would break the session.

**Attach (Borrowed) is unaffected:** `DebugActiveProcess` connects to a process that already owns its console; DrHook creates nothing and inherits nothing. The problem is exclusive to Launch (Owned).

### The lifecycle finding — ADR-010's verbs don't match established convention (cited survey)

ADR-010 Q2 chose `drhook_detach` (mode-agnostic normal-end) + a deferred `drhook_kill` (anomaly). Tier 1 shipped `drhook_detach` with this behavior: Owned → SIGTERM→SIGKILL graceful terminate (ADR-008); Borrowed → detach-leave-running.

The survey shows that **behavior is correct but the name is wrong** — every established tool calls that action *Stop*, and reserves *Detach* for "disconnect and leave running":

- **Visual Studio** (`learn.microsoft.com/.../debug-multiple-processes`): *Stop Debugging* (Shift+F5) → launched process **ended**, attached process **detached/left running**. *Detach All* → **all left running** (even launched). *Terminate All* → **all ended**. Per-process "Detach when debugging stops" overrides the default.
- **DAP** (`microsoft.github.io/debug-adapter-protocol`): `disconnect` → MUST terminate if launched, MUST NOT terminate if attached (override via `terminateDebuggee`). `terminate` → graceful cooperative shutdown (a signal the debuggee can intercept), best-effort, escalate to `disconnect` if vetoed.
- **Rider** (`jetbrains.com/help/rider`): *Stop* (Ctrl+F2) → launched **terminated**, attached **left running**. True detach-without-kill for a *launched* process is an open request (RIDER-3800 / RIDER-53869) — **even Rider hasn't solved the Owned-leave-running case**, confirming DrHook's F-010-2 is intrinsically hard, not a local gap.

Consensus triad: **stop** (normal end; terminate-if-launched / detach-if-attached), **detach** (always leave running), **kill/terminate** (force end regardless of origin).

### The console-hosting finding — a protocol-channel debugger cannot host the console on its own channel (cited survey)

All three IDEs expose a three-way console taxonomy, and DAP formalizes *why*:

- **VS Code `console`** (`code.visualstudio.com/docs/csharp/debugger-settings`): `internalConsole` (default; debuggee stdio routed through the Debug Console; simple `Console.ReadLine` works, **no `ReadKey`/raw input**), `integratedTerminal` (real PTY; full interactive stdin), `externalTerminal` (separate OS window). VS 2022 17.5+ and Rider's "terminal mode" (Automatic / pseudoterminal / redirect-streams / External Console) mirror this.
- **DAP `runInTerminal`** (reverse request, adapter→client) exists precisely because *"the debuggee … output channels are connected to a client's debug console via output events. However, this has … limitations, such as not being able to write to the terminal device directly and not being able to accept standard input. For those cases, launching the debuggee in a terminal is preferable."* The adapter asks the **client** to spawn the debuggee in a terminal and report its `processId`; the adapter then drives it by PID. Debuggee console I/O is bound to the client's terminal, **decoupled from the adapter's protocol channel**.

**The MCP constraint `[?]→`confirmed-by-spec-reading:** DAP solves this with a *reverse request*. **MCP has no reverse-request equivalent** — it is client→server requests plus server→client notifications; a server cannot synchronously ask its client "run this in your terminal and return the PID." Therefore DrHook **cannot delegate terminal-hosting to its client** the way a DAP adapter does. Its options reduce to:

1. **Capture-and-surface** (the `internalConsole` analogue): redirect the child's stdout/stderr to pipes DrHook owns; surface captured output via the event sink / an MCP tool; accept simple line-stdin via an MCP tool. No raw/`ReadKey` stdin. *This is also the fix for the §collision bug.*
2. **Self-hosted terminal/PTY** (the `runInTerminal` analogue, but DrHook-owned because MCP can't delegate): DrHook allocates a PTY (openpty/forkpty on POSIX, ConPTY on Windows), binds the launched child to it, and a **separate human-facing surface renders that PTY**. This is the only path to full interactive stdin — and it is where the **dashboard** lives.

## Decision (proposed)

### D1 — Lifecycle is three verbs, by established convention

| Tool | Owned (launched) | Borrowed (attached) | Status |
|---|---|---|---|
| **`drhook_stop`** | graceful terminate (SIGTERM→SIGKILL escalation, ADR-008) | detach, leave running | **rename of the Tier-1 `drhook_detach`** — behavior already shipped |
| **`drhook_detach`** | detach, **leave running** | detach, leave running | Borrowed works today; **Owned gated on F-010-2** (intrinsically hard — see Rider) |
| **`drhook_kill`** | force SIGKILL (`DebugSession.Abandon()`) | force SIGKILL | Owned exists (ADR-008 1b); **Borrowed gated on F-010-1** |

`drhook_stop` is the normal, expected end-of-session — what an agent reaches for by default. `drhook_detach` becomes the deliberate "disconnect but keep it alive." `drhook_kill` stays the anomaly escape-hatch. This **supersedes ADR-010 Q2**: the Tier-1 `drhook_detach` is renamed to `drhook_stop`, and `drhook_detach` is re-introduced with leave-running semantics. The rename is cheap (MCP is self-describing; ADR-010 Decision principle 9 covers transition asymmetry).

**Why `drhook_kill`, not `drhook_terminate`** (2026-06-01): "terminate" is the *genus* — both `drhook_stop` (graceful) and the forced verb are terminations; naming the forced one "terminate" wrongly implies `stop` isn't one. DAP's `terminate` request is specifically the *graceful* path (a catchable signal), so `drhook_terminate` = hard-kill would contradict the protocol that formally defines the verb, while `drhook_stop` already owns graceful-for-Owned. `kill` names the specific forced mechanism (SIGKILL), carries the deliberate "anomaly, not routine" starkness, and matches [`feedback_process_lifecycle_discipline`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_process_lifecycle_discipline.md). (VS's "Terminate" button is `terminate`'s one merit — outweighed.)

### D2 — Isolate launched-debuggee stdio from the MCP channel (the bug fix; do first)

At Launch, give the child **dedicated stdout/stderr pipes** owned by DrHook instead of the inherited MCP-server fds (the fix `EngineSteppingSession.cs:171` already prescribes). This closes the protocol-corruption bug independently of everything else. Substrate work in `DbgShim`/`DebugSession.Launch`: create pipe(s), pass/duplicate the write ends to the child before resume, read the read ends on a DrHook-owned thread. Borrowed is untouched.

### D3 — Surface captured debuggee output (the `internalConsole` analogue)

Captured stdout/stderr becomes a **new `IDebugEventSink` record kind** (e.g. `OnConsoleOutput`), surfaced to the agent via MCP — either drained like anomalies (`drhook_drain_console` `[?]`) or attached to step/continue responses. Simple line-`stdin` to the debuggee via either a dedicated MCP tool (`drhook_stdin` `[?]`) or — per Q2 — `elicitation/create`, which is a **protocol-native** way to prompt the human for a line and feed it to the debuggee. This gives the agent *observability* of the program's console behavior. No raw/`ReadKey` stdin (that needs a real terminal — D4). `OnConsoleOutput` is a **surface-agnostic substrate event** (per D5): the MCP layer consumes it for the agent now; Mira's human surfaces consume the same event later — no refactor.

### D4 — Real terminal for interactive console apps — DrHook owns the surface

Full interactive stdin (`Console.ReadKey`, cursor control, TUIs) needs a real PTY, which MCP cannot provide (Q2). **DrHook allocates and owns a PTY** (openpty/forkpty on POSIX, ConPTY on Windows) and binds the launched child to it; the PTY master is rendered by the dashboard (D5). This is the substrate-owned analogue of DAP `runInTerminal` — DrHook hosts the terminal itself because (a) MCP cannot delegate it and (b) **the stack must own its surfaces.**

**Rejected — borrowing the operator's terminal as a workaround.** An earlier draft proposed letting the agent launch the debuggee in the operator's own terminal multiplexer (e.g. a tmux pane used for remote access) and `drhook_attach` by PID. The *pattern* (console decoupled from the protocol channel; attach rather than launch-and-inherit) is sound and informs the design above — but **relying on the operator's incidental access tooling (tmux / Blink / Tailscale / SSH) is an external dependency, not substrate.** It conflates how a human happens to reach the session with how DrHook is built, fails for any consumer not using that tooling (IDE, headless, cron), and is a convenient workaround in place of the real work. Per EEE and [`feedback_no_vibe_coding`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_vibe_coding.md): DrHook owns its terminal/dashboard surface — *inspired by* the pattern, not *reliant on* the operator's environment. The collision bug (§D2) is fixed by D2, not papered over with operator tooling.

### D5 — Human-facing surface(s): a surface-agnostic debug-state model, not "a dashboard"

Per the 2026-06-01 directive ([`project_mira_multisurface_trajectory`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_mira_multisurface_trajectory.md)), the human-facing surface is **not** a single TUI. **Console, TUI, and an Avalonia GUI are all eventual _Mira_ views**, and the DrHook + Mira trajectory is toward a full-blown IDE — so the substrate is shaped now to admit multiple views without a later refactor. **Substrate first.**

The seam:

- **Substrate emits surface-agnostic debug-state.** The existing `IDebugEventSink` stream (`OnEvent` / `OnLog` / `OnAnomaly` / D3's `OnConsoleOutput`) plus a queryable debug-state model — execution position, stack, locals, breakpoints, the `(hypothesis, observation)` log, lifecycle/session state. The substrate knows nothing about views.
- **Views are pluggable consumers.** The agent (via MCP) is one consumer today; Mira's console / TUI / Avalonia GUI are future ones; a full IDE is the horizon. The transport must admit multiple — possibly simultaneous, possibly remote — consumers; lean to a **local socket** over a shared file for live updates.
- **No view is baked into the substrate.** The first human rendering is *one view* of the model, not the model. Its requirements feed back into the model, but the model is the load-bearing artifact.

This is the architectural payoff of the MCP-can't-delegate constraint (Q2): since DrHook must self-host the terminal/PTY anyway (D4), the surface-agnostic debug-state it already produces becomes **the human's window into the agent's debugging** — governed-automation / human-in-the-loop made concrete ([`project_governed_automation_thesis`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_governed_automation_thesis.md), [`feedback_together_not_parallel`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_together_not_parallel.md)) — and the foundation the eventual IDE is built on. It is the [`feedback_infrastructure_no_yagni`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_infrastructure_no_yagni.md) case: the view consumers are *named* (console, TUI, Avalonia), so the surface-agnostic seam is built up front, not speculatively.

**Keep-open-after-exit** (VS's "Automatically close the console when debugging stops"): when an Owned debuggee exits, the surface **holds** the final output until dismissed, configurable — a view/host property, not a lifecycle-verb concern.

**Status:** deferred behind Q6 (= ship D2 + D3 first). When built, the model + transport seam above is locked *before* the first view, so adding views never refactors the substrate.

### Phasing (EEE — one unknown per phase; learn the rest in use)

1. **Phase 1 — Isolation (D2).** Fix the collision bug. Pure substrate; smallest, highest-urgency. Probe: launch a verbose target, confirm the MCP stream stays clean.
2. **Phase 2 — Lifecycle triad (D1).** Rename `detach`→`stop`; add leave-running `detach` (Borrowed only until F-010-2); `drhook_kill` (Owned now; Borrowed with F-010-1). Mostly MCP-surface + the deferred findings.
3. **Phase 3 — Surface captured output (D3).** Agent observability of console behavior.
4. **Phase 4 — PTY + dashboard (D4 + D5).** The richest, most exploratory; design refined in use. Interactive console apps + the human surface.

Per [[feedback_substrate_dissolves_per_variant_planning]] and Martin's framing, build a **flexible, simple primitive** at each phase and discover the routing nuances by using the MCP tool — do not over-specify the dashboard up front.

## Open questions — answer before Proposed → Accepted

1. **`stop` vs `detach` rename timing.** Rename in the next increment, or batch with the F-010-1/F-010-2 lifecycle work? (The behavior is shipped under `detach` today; the rename is cosmetic but corrects a just-shipped name.) **RESOLVED 2026-06-01 → A: rename now, as its own small self-describing increment — don't let the wrong-but-shipped name sit.**
2. **~~MCP reverse-request — confirm the constraint.~~ RESOLVED 2026-06-01 → NO protocol equivalent; YES at the agent-orchestration layer.** MCP defines exactly three server→client requests — `sampling/createMessage`, `elicitation/create`, `logging/setLevel` (+ `ping`), per spec **2025-11-25**; **none can spawn/host a process or return a PID.** Claude Code supports sampling and elicitation (elicitation since v2.1.76, 2026-03) but neither hosts a terminal. So DrHook **cannot delegate terminal-hosting via the protocol** the way a DAP adapter does — D4's premise holds: **DrHook must own its terminal surface.** The launch-in-terminal + attach *pattern* (console decoupled from the protocol channel; attach rather than launch-and-inherit) still informs D4 — but an earlier draft that proposed leaning on the operator's terminal multiplexer (a tmux pane the human uses for remote access) is **rejected as a relied-upon workaround**: it conflates the operator's access tooling with the stack (see D4). Side-finding: `elicitation/create` IS a protocol-native channel for *line* stdin (prompt the human for a line, feed it to the debuggee) — folded into D3.
3. **Dashboard form and transport.** Separate process DrHook spawns (renders PTY + reads a side-channel of debug state)? A TUI? How does the human launch/attach to it, and how does it receive debug-state updates from the MCP server process (shared file? local socket? the anomaly/console sink)? **RESOLVED 2026-06-01 → deferred behind Q6 (= A). When built, it is shaped surface-agnostically per D5 — multiple Mira views (console / TUI / Avalonia GUI) over a local-socket transport; the debug-state model is locked before the first view.** 
4. **D3 surfacing shape.** Drain-on-demand (`drhook_drain_console`, like anomalies) vs attached-to-step-responses vs server→client MCP notifications? Which fits the agent's loop without flooding it? **DEFERRED 2026-06-01 → decided at the D3 implementation increment; not a Proposed→Accepted blocker.**
5. **Does `stop`'s Owned-terminate stay graceful (ADR-008 SIGTERM→SIGKILL)?** Proposed yes — it is the shipped behavior and matches DAP `terminate`-then-`disconnect`. Confirm. **RESOLVED 2026-06-01 → YES: keep the ADR-008 SIGTERM → wait → SIGKILL escalation.**
6. **Scope of D4/D5 vs. just D2+D3.** Is the dashboard in near-term scope, or do we ship isolation + capture-and-surface (D2+D3) first and let dashboard demand emerge from use? **RESOLVED 2026-06-01 → A: ship D2 (done) + D3 first; the dashboard (D4/D5) is deferred, its demand and form to emerge from D3 in use.**

## Consequences

- **Closes a real bug.** D2 removes a protocol-corruption hazard that currently makes `drhook_launch` unsafe for any verbose target.
- **Names match every IDE an agent might know** (D1) — the mental model transfers, as ADR-010 intended.
- **Turns an MCP limitation into a thesis-aligned feature** (D5) — because DrHook must self-host the terminal, it gains a human-facing dashboard that makes the cognitive loop legible. No IDE's console does the `(hypothesis, observation)` part.
- **Interacts with the deferred findings**: F-010-1 (Borrowed `Kill`) and F-010-2 (Owned detach-leave-running) become Phase-2 dependencies; the latter is hard (Rider hasn't solved it).

## References

### Code reads (factual basis)
- `src/DrHook.Mcp/Program.cs:91` — `WithStdioServerTransport()` (stdout = protocol channel).
- `src/DrHook.Engine/Interop/DbgShim.cs:237` — `CreateProcessForLaunch(...)`; no handle redirection.
- `src/DrHook.Mcp/EngineSteppingSession.cs:171` — known-gap comment ("child inherits parent fds … fix is dedicated pipes").
- `docs/adrs/drhook/ADR-006-drhook-engine.md:149` — same gap recorded as Phase 3 polish.

### IDE / protocol survey (cited 2026-05-31)
- VS: `learn.microsoft.com/.../debug-multiple-processes`, `.../how-to-specify-debugger-settings` (Stop/Detach/Terminate matrix; auto-close-console option).
- VS Code / DAP: `code.visualstudio.com/docs/csharp/debugger-settings` (`console` enum), `microsoft.github.io/debug-adapter-protocol/specification.html` (`runInTerminal`, `disconnect`/`terminateDebuggee`, `terminate`), `.../changelog.html` (runInTerminal DAP 1.12).
- Rider: `jetbrains.com/help/rider/Debug_Tool_Window.html`, `.../attach-to-process.html`; YouTrack RIDER-3800 / RIDER-53869 (no detach-from-launched), RIDER-43475 (no keep-console-open).

### Prior ADRs / memory
- [ADR-010](ADR-010-mcp-tool-surface-redesign.md) — lifecycle Q2 revised here; substrate findings F-010-1 / F-010-2 become Phase-2 dependencies.
- [ADR-008](ADR-008-process-lifecycle-discipline.md) — SIGTERM→SIGKILL graceful escalation = `drhook_stop`'s Owned behavior.
