# ADR-025 - REPL Line Editor

## Status

**Status:** Completed — 2026-03-30. Implemented, all tests passing.

## Context

Mercury's REPL (`ReplSession`) uses raw `TextReader.ReadLine()` for input. This means:

- No history navigation (up/down arrows do nothing)
- No cursor movement within the line
- No in-line editing (inserting characters, deleting words)
- The `:history` command exists but entries can't be recalled

For an interactive SPARQL REPL, this is a significant usability gap. Users type long queries, make typos, want to re-execute with modifications. Every modern REPL has readline-style editing.

### Prior Art

The SkyChatBot MVP (Summer 2025) includes a BCL-only `ConsoleLineEditor` that handles ReadKey interception, history navigation, cursor movement, paste detection, and multi-line rendering. It works but has known quirks with line wrapping and history cursor positioning. The core mechanics are proven and can be adapted.

## Decision

Add a `LineEditor` class to `Mercury.Runtime` that provides readline-style editing for interactive console input. BCL-only, no external dependencies.

### Capabilities

| Feature | Key | Description |
|---------|-----|-------------|
| History up | ↑ | Previous history entry |
| History down | ↓ | Next history entry / restore current input |
| Cursor left | ← | Move cursor left |
| Cursor right | → | Move cursor right |
| Word back | Alt+B | Move to previous word boundary |
| Word forward | Alt+F | Move to next word boundary |
| Home | Home | Move to start of line |
| End | End | Move to end of line |
| Delete char | Delete | Delete character at cursor |
| Backspace | Backspace | Delete character before cursor |
| Submit | Enter | Submit the line |

### Integration with ReplSession

`ReplSession.ReadInput()` currently calls `TextReader.ReadLine()`. When running interactively (`input == Console.In && !Console.IsInputRedirected`), it will use `LineEditor.ReadLine()` instead. Non-interactive mode (piped input, PipeServer) continues using `TextReader.ReadLine()` unchanged.

The existing `_history` list in `ReplSession` will be shared with `LineEditor` so that REPL command history and line editor history are the same collection.

### What Not to Include

- **Tab completion** — valuable but separate concern (needs SPARQL keyword/prefix awareness)
- **Ctrl+R reverse search** — diminishing returns for a SPARQL REPL
- **Multi-line editing** — Mercury's brace-balancing continuation already handles this
- **Swedish keyboard workarounds** — the SkyChatBot hack applied Rider-specific fixes globally on macOS; not appropriate here

## Implementation Plan

### Phase 1: LineEditor in Mercury.Runtime
- New class `Mercury.Runtime.IO.LineEditor`
- ReadKey loop with history, cursor movement, editing
- Redraw logic for in-place line updates
- Fix SkyChatBot quirks: history cursor at buffer end, wrapping-aware Home/End

### Phase 2: Integrate with ReplSession
- `ReadInput()` delegates to `LineEditor` when interactive
- Share `_history` list between ReplSession and LineEditor
- Non-interactive paths unchanged

### Phase 3: Tests
- Unit tests for history navigation logic
- Unit tests for buffer editing (insert, delete, backspace)

## Success Criteria

- [ ] Up/down arrows navigate history in `mercury` CLI
- [ ] Left/right arrows move cursor within the line
- [ ] Home/End work correctly
- [ ] Editing mid-line (insert, delete, backspace) renders correctly
- [ ] Non-interactive mode (piped input) unaffected
- [ ] BCL-only, no external dependencies

## Consequences

### Positive
- Mercury CLI becomes a usable interactive tool
- History navigation eliminates re-typing of queries
- Foundation for future tab completion (ADR deferred)

### Trade-offs
- ~200 lines of console rendering code in Mercury.Runtime
- Console cursor manipulation is inherently platform-sensitive (tested on macOS, should work on Windows/Linux)
