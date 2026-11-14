ADR-004: Omega — Terminal-First Mira Application and Under-Engineering
Component: Mira (interaction and presentation surfaces)
Status: Proposed — 2026-11-12
Related: [Mira ADR-001 — Interaction Surface Architecture](ADR-001-interaction-surface-architecture.md); [DrHook ADR-012 — Debug-State Surfaces](../drhook/ADR-012-debug-state-surfaces.md)
Evidence: poc/AvaTerm/ (cross-platform terminal proof of concept)
Summary
Omega begins as a terminal, not as a predesigned IDE.

A terminal is typically a secondary panel in Visual Studio, VS Code, or Rider. In Omega, the terminal is the primary workspace. The first useful application should provide a consistent graphical terminal experience across Windows, Linux, and macOS while allowing each operating system's native shells and tools to run normally.

Omega may eventually become Sky Omega's development environment, but that outcome must emerge from demonstrated needs. It is not a requirement to construct an IDE, editor, debugger frontend, agent framework, or distributed orchestration layer in advance.

The guiding discipline is under-engineering:
Under-engineering is the deliberate refusal to build what has not yet demonstrated a need to exist.

This is not lower engineering quality. It is less unnecessary engineering.
Context
Sky Omega already provides substantial capabilities through existing command-line tools, MCP servers, and development workflows. Claude Code and Codex can inspect repositories, edit source code, execute builds and tests, and use Git from a terminal. Mercury supplies persistent semantic knowledge; DrHook supplies runtime observation. ssh, tmux, shells, and the .NET SDK already provide their own established capabilities.

A new graphical development environment need not reimplement any of them to be valuable.
AvaTerm: the initial evidence
The AvaTerm proof of concept contains:
- AvaTerm.Core: a self-contained VT/xterm terminal model and parser, with screen, scrollback, and input handling.
- AvaTerm.Pty: Unix PTY and Windows ConPTY implementations, with terminal session management.
- AvaTerm.App: an Avalonia desktop frontend with rendering, input handling, and tabs.
- AvaTerm.Tests: a standalone console-based test runner, including terminal and PTY checks.
  The POC targets .NET 10 and uses Avalonia 11.3. Its authoring-time README records an earlier validation state, before the GUI was built against real Avalonia and before the Windows/macOS paths were run. Subsequently, the POC was reported as working on Windows, Ubuntu, and macOS. The repository import should retain the original README for provenance and document the later validation explicitly, including host, SDK, shell, test command, and any remaining defects when those measurements are collected. A successful manual run is evidence of feasibility, not a claim of production readiness.

AvaTerm is an experimental starting point, not a mandate to preserve every implementation decision.
Decision (proposed)
D1 — Terminal first
The first Omega application shall be a graphical terminal whose primary function is to host ordinary command-line work. The terminal remains useful when no Sky Omega services or AI agents are running.

In particular, Omega must not require James, Lucy, Mercury, DrHook, Claude Code, Codex, or a network connection to start and operate as a local terminal.
D2 — Consistent experience, native execution
Use a shared cross-platform user interface and terminal behavior while respecting platform-native process and shell mechanisms:
- Windows: ConPTY, running an installed shell such as PowerShell or cmd.exe.
- Linux: PTY, running the user's configured shell, commonly bash or zsh.
- macOS: PTY, running the user's configured shell, commonly zsh.
  The goal is a consistent terminal experience, not an invented cross-platform shell. Omega should not conceal meaningful host differences or prescribe a particular shell.

tmux is optional. It can be used where available, locally on Unix, through WSL on Windows, or on a remote host accessed using ssh. Omega should neither depend on tmux nor implement a second multiplexer without a demonstrated need.
D3 — Agent clients are ordinary terminal programs
Claude Code, Codex, and future command-line agent clients initially run exactly as other terminal applications do. No agent-specific integration or protocol is required for the first release.

Mercury and DrHook likewise remain independently executable tools or services. Omega does not become their owner or orchestrator merely by hosting a terminal in which they are used.
D4 — Preserve AvaTerm as an independent POC
Import the existing source without a premature rewrite:
poc/AvaTerm/
├── AvaTerm.slnx
├── Directory.Build.props
├── README.md
├── src/
│   ├── AvaTerm.Core/
│   ├── AvaTerm.Pty/
│   └── AvaTerm.App/
└── tests/
└── AvaTerm.Tests/
Treat poc/AvaTerm/ as a standalone experiment. Do not automatically absorb it into the main SkyOmega.sln, or rename its projects to Mira/Omega, merely because it has entered the repository. Import does not imply promotion to production code.

The POC may later seed production projects under src/ if experiments justify that move. Preserve working behavior and test evidence during any extraction.
D5 — Under-engineering follows EEE
Use Emergence → Epistemic → Engineering (EEE) as the admission process for new functionality:
1. Emergence: A real development workflow exposes a limitation or opportunity. Record the observation and reproduction conditions.
2. Epistemic: Establish what is known, compare the smallest viable alternatives, and identify how a change can be verified. Distinguish evidence from inference.
3. Engineering: Implement the smallest dependable change that resolves the established need, validate it across affected platforms, and incorporate the result into the next iteration.
   No predetermined IDE feature matrix is accepted as a substitute for this process. Under-engineering applies to scope, not to correctness, error handling, security, lifecycle management, or testability.
   D6 — Keep interactions at their existing boundaries
   Omega is initially a terminal-hosting desktop application. This does not automatically make every character emitted by a shell or agent a James-mediated cognitive input.

How a desktop terminal participates in Mira's broader surface contract is a separate question under [Mira ADR-001](ADR-001-interaction-surface-architecture.md). Do not create a speculative surface adapter, registry, or cognitive-message protocol until there is an actual consumer and validated requirement.
Existing DrHook visualization boundary — an emergent opportunity
[DrHook ADR-012](../drhook/ADR-012-debug-state-surfaces.md) already establishes a surface-agnostic debugging-state contract, independently of Omega. This is implemented evidence, not a newly proposed Omega subsystem:
- DrHook.Engine/DebugStateSnapshot.cs and DebugStateDelta.cs represent debugger state and changes.
- DrHook.Engine/Transport/DebugStateServer.cs publishes a snapshot on connection followed by deltas over newline-delimited JSON on a Unix-domain socket.
- DrHook.Viz/DebugStateClient.cs and IDebugStateView.cs support independently launched consumers of the same client-side state model; a console view and a full-screen TUI demonstrate the separation.
- The authoritative debugging session belongs to the agent-driven DrHook MCP process. A human view can connect and disconnect without owning or terminating that session.
  Omega is a plausible future home for an Avalonia IDebugStateView implementation. This may realize DrHook ADR-012's planned GUI surface inside the terminal-first application, without embedding the debugging engine or creating a competing state model. It is an architectural option, not an Omega v0.1 feature commitment and not a unilateral change to DrHook ADR-012.

The current transport is read-only; bidirectional debugger control and human/agent command serialization remain separate, unresolved work. Cross-platform visualization must also verify the transport and session rendezvous on Windows rather than infer portability from AvaTerm's already demonstrated terminal support. The first justified integration experiment, if one emerges, should be an independently attachable read-only view consuming the existing DrHook stream. Do not tie the agent-owned debugger session's lifetime to an Omega tab or window.
Explicitly out of scope for the first experiment
- A built-in source-code editor, language server, project system, or IDE workspace model.
- Embedded Claude/Codex SDKs or custom agent conversation panes.
- A custom shell, replacement for tmux, or an SSH protocol implementation.
- Mercury or DrHook dashboards absent a demonstrated use case.
- Automatic remote-session discovery, process supervision, or collaboration protocols.
- A general plugin architecture or a comprehensive Mira interface hierarchy.
  These are not rejected forever. They are uncommitted hypotheses, to be revisited only when justified by use.
  Validation before acceptance
  Acceptance of this ADR should be based on recorded evidence, not on the attractiveness of the vision:
- [ ] Import the original AvaTerm POC into poc/AvaTerm/ and record its provenance.
- [ ] Run the POC and its tests on Windows, Ubuntu, and macOS; record the commands, environment, outcomes, and known gaps.
- [ ] Verify basic shell operation and terminal interaction on each platform, including keyboard layout, resizing, copy/paste, process exit, and common full-screen terminal applications.
- [ ] Verify Claude Code or Codex can run as an ordinary terminal process where installed, without special Omega integration.
- [ ] Verify tmux behavior on supported hosts, including a reconnect workflow if one is exercised; distinguish local, WSL, and SSH cases.
- [ ] Characterize security boundaries (including OSC 52 clipboard writes and untrusted escape sequences), process cleanup, and PTY lifecycle before claiming production suitability.
- [ ] Determine the minimal install/run approach for a first Omega application. Assess .NET tool packaging for a graphical desktop application on all three OSes rather than treating packaging as already solved.
- [ ] Compare the POC with the relevant parts of Mira ADR-001 and DrHook ADR-012; document any actual conflict before restructuring components.
  An initial delivery can be considerably smaller than this full validation list; these items are acceptance questions, not permission to expand the POC speculatively.
  Consequences
  Benefits
- A useful application can ship before broader Sky Omega cognitive components are available.
- Existing shells and CLI applications keep doing their own work.
- One interface supports Windows, Linux, and macOS without pretending their operating systems are identical.
- AvaTerm offers an inspectable foundation for experimenting with Sky Omega's own development environment.
- New capabilities can grow from observed constraints, limiting unnecessary architectural commitments.
- The work is recursively testable: agents can use Omega while changing Omega, with Mercury and DrHook available as independent tools.
  Costs and risks
- A terminal emulator is not inherently simple: PTY and ConPTY lifecycle, escape sequences, text layout, input methods, security, and platform behavior require careful validation.
- A successful POC may tempt premature productization; keeping it experimental is intentional.
- .NET tool distribution of a desktop GUI may require platform-specific packaging investigation.
- A terminal alone does not provide the structured observation available through DrHook or Mercury; terminal output should not be mistaken for authoritative machine-readable state.
- DrHook visualization is an enticing adjacent capability; prematurely integrating it would violate the terminal-first scope and EEE admission discipline.
  Alternatives considered
1. Build a complete Mira IDE upfront: rejected at this stage because it starts from an assumed feature set rather than observed need.
2. Build an IDE extension for Rider/Visual Studio/VS Code first: not chosen for the initial experiment; those applications place the terminal inside a broader IDE and introduce their own integration constraints.
3. Use an existing terminal with no Omega application: remains a valid baseline. Omega earns its existence only by proving a distinct benefit in actual use.
4. Build agent-specific UI before terminal capabilities: deferred; Claude Code and Codex already have terminal interfaces.
   Open questions
   These are deliberately unresolved and should not become implementation requirements until investigated:
- Which first friction point, if any, warrants an Omega-specific feature beyond AvaTerm?
- Should the first installable Omega be a .NET tool, platform-native application packages, or both?
- How should a local terminal eventually relate to Mira's formal interaction-surface contract, if at all?
- Does actual use justify hosting DrHook ADR-012’s planned Avalonia view inside Omega, or is a standalone view preferable? If justified, can an Omega view attach read-only through DrHook.Viz without altering DrHook session ownership or relying on unverified Windows transport behavior?
- When does terminal session ownership need to be separated from window/tab lifetime?
- What concrete security and lifecycle characteristics are required before publishing a production tool?
  Initial repository action
1. Commit the unchanged AvaTerm POC under poc/AvaTerm/ with its existing solution, README, sources, and tests.
2. Add this ADR at docs/adrs/Mira/ADR-004-omega-terminal-first.md, initially Proposed.
3. Add an ADR-004 entry to docs/adrs/README.md under Cognitive Component ADRs, noting Mira and Proposed status.
4. Preserve the planned Mira ADR-002 (Surface Contract Specification) and ADR-003 (MCP Surface Evolution); neither must be implemented to begin the terminal experiment.
5. Conduct a joint EEE review before promoting the POC to a maintained application, renaming it Omega, or wiring it into the main solution.
   Guiding principle: Build what we can justify. Reuse what already works. Discover what is missing by using the system.