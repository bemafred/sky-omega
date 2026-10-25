# ADR-046: Multi-operation SPARQL UPDATE at the engine facade

## Status

**Status:** Completed — 2026-10-25 (Proposed → Accepted 2026-06-06 in the same design review as [ADR-045](ADR-045-graph-clause-feature-parity.md); implemented 1.8.12 after the bug recurred on a freshly bootstrapped Linux instance on 2026-10-24.)

Surfaced while updating Mercury semantic memory: a single `DELETE DATA { … } ; INSERT DATA { … }` request applied only the DELETE and silently dropped the INSERT. Distinct root cause from [ADR-045](ADR-045-graph-clause-feature-parity.md) (not GRAPH-related) — but surfaced in the same dogfood session.

## Context

### The defect — silent loss of operations 2..N

A single update request containing two `;`-separated operations executes **only the first**; the remainder is silently discarded (no error, no warning). Reproduced via the Mercury MCP server:

```sparql
DELETE DATA { GRAPH <g> { <s> <p> "old" } } ;
INSERT DATA { GRAPH <g> { <s> <p> "new" . <s> <p2> "x" } }
```
→ reported "1 triple affected" (the DELETE); the INSERT never ran. Re-submitting the INSERT alone applied all of its triples.

### This is valid SPARQL 1.1 — and a MUST-level conformance violation

```
[29] Update   ::= Prologue ( Update1 ( ';' Update )? )?
```
`Update` is a recursive, `;`-separated sequence of `Update1` operations — a single valid request. The Update spec is explicit and normative:

> *"Implementations MUST ensure that the operations of a single request are executed in a fashion that guarantees the same effects as executing them sequentially in the order they appear in the request."*

and each request *"SHOULD be treated atomically."* Executing only operation 1 is therefore not a gray area — it is a MUST violation with silent data loss.

### Root cause — the facade calls the single-operation path

The complete, correct, **already-tested** multi-operation machinery exists in the engine; the production facade simply doesn't call it.

- **Facade (the bug):** `SparqlEngine.Update` (`src/Mercury/SparqlEngine.cs:160`, `:174-175`) calls the single-operation `parser.ParseUpdate()` then `new UpdateExecutor(...).Execute()`. `ParseUpdate()` (`SparqlParser.cs:238`) parses the Prologue + **exactly one** `Update1` and returns; the trailing `; INSERT DATA { … }` is left unconsumed and silently ignored — the drop is at **parse time**.
- **The correct path already exists:** `parser.ParseUpdateSequence()` (`SparqlParser.cs:267`) loops on `;` and returns `UpdateOperation[]`; `UpdateExecutor.ExecuteSequence(...)` (`UpdateExecutor.cs:101-132`) `foreach`-executes every operation, accumulates affected counts, and short-circuits only on a non-`SILENT` failure (matching the spec's "failure aborts the sequence").
- **The MCP layer is innocent:** `MercuryTools.Update` (`Mercury.Mcp/MercuryTools.cs:106`) forwards the full string verbatim.

### Blast radius

Every production surface routes through the single-operation facade and therefore has the bug:
- MCP — `MercuryTools.cs:106`
- HTTP — `SparqlHttpServer.cs:580`
- CLI — `Mercury.Cli/Program.cs:564`
- REPL/test helper — `tests/Mercury.Tests/Repl/TestSessionHelper.cs:206`, `:216` (same single-op mis-wire)

Any multi-operation update submitted through any of these has silently lost operations 2..N. (The bug was first hit live this session; a prior memory-update `DELETE DATA … ; INSERT DATA …` left a node statusless until the INSERT was re-run standalone.)

### Why conformance (94/94 Update) misses it

The W3C suite **does** contain multi-operation requests — e.g. `tests/w3c-rdf-tests/sparql/sparql11/basic-update/insert-05a.ru` has five `;`-separated operations. But the conformance harness drives Update evaluation through the **correct** path directly: `SparqlConformanceTests.cs:961` `ParseUpdateSequence()` → `:974` `ExecuteSequence(...)` (it even logs `Update operations: {N}`). So the suite validates the *library* methods — which are correct — and **no test exercises `SparqlEngine.Update` (the facade) with a multi-operation string.** The bug lives entirely in the gap between the tested library and the untested facade. (Update *syntax* tests route through `ParseQuery`, not the update parser, so they don't cover it either.)

## Decision

The behavior is **settled by spec** (all operations execute, in order, atomically). This is the same principle as [ADR-045](ADR-045-graph-clause-feature-parity.md): **one path, with the special case as a degenerate instance.** A single update is a sequence of length 1 — `ParseUpdate`/`Execute` should not stand as a parallel path the facade can wrongly choose; the facade uses the one sequence path, and single-operation handling falls out as N=1. The fix is a wiring correction, not new logic:

1. **Re-point the facade.** `SparqlEngine.Update` calls `ParseUpdateSequence()` + `UpdateExecutor.ExecuteSequence(...)` instead of `ParseUpdate()` + `Execute()`. Return the accumulated affected-count. Both target methods are public and conformance-tested today.
2. **Fix REPL parity.** Apply the same correction at `TestSessionHelper.cs:206`/`:216` so REPL behavior matches.
3. **Honor atomicity (verify, not assume).** Confirm `ExecuteSequence`'s failure-abort semantics meet the spec's "failure of any operation MUST abort subsequent operations." If the substrate can offer all-or-nothing for `DATA` operations, note it; if only sequential-with-abort is feasible today, state that explicitly as the conformance posture.
4. **Close the test gap at the facade.** Add facade-level tests (`SparqlEngine.Update` with multi-op strings: `INSERT;INSERT`, `DELETE;INSERT`, mixed with a failing middle op to assert abort) so the tested boundary is the one production actually uses.

Single-operation `ParseUpdate()`/`Execute()` may remain as internal fast paths, but no public surface should depend on them for request handling.

## Consequences

- **Positive:** multi-operation updates work through MCP/HTTP/CLI/REPL; the spec's sequencing + atomicity guarantees hold; silent data loss ends. Near-zero implementation risk — the executed code is already conformance-validated.
- **Negative/risk:** none material. Watch that error-reporting (affected counts, partial-failure messages) is sensible for the multi-op case.
- **Lesson (recorded):** conformance green on a *library method* does not certify the *facade* callers route through. This is the second conformance-blind defect found this session by dogfooding (see ADR-045); both argue for testing the production entry points, not only the library.

## Alternatives considered

- **Split multi-op in the MCP/CLI wrappers and call the facade per operation.** Rejected — pushes SPARQL `;`-tokenization (string-splitting that must respect literals/IRIs/comments) into every caller, duplicated and fragile, and breaks request-level atomicity. The engine already parses sequences correctly; use it.
- **Document as a limit, require callers to send one op per request.** Rejected — silent data loss on a valid, MUST-level request, on the primary memory-write path; not a "fit" substrate.

## Validation plan

1. Facade unit tests: `SparqlEngine.Update` with N-operation requests (N≥2), asserting all operations apply and the affected-count is the sum.
2. Abort semantics test: a failing middle (non-SILENT) operation aborts the rest, per spec.
3. Re-run the live MCP repro (`DELETE DATA … ; INSERT DATA …`) and confirm both take effect.
4. Full W3C Update suite remains 94/94; add at least one evaluation test routed through the facade (not only `ExecuteSequence`).

## Implementation (1.8.12, 2026-10-25)

The bug recurred on 2026-10-24, while bootstrapping Mercury on a new Ubuntu LTS rig (`zbook`): a chained update applied only its first operation, as on 2026-06-06. That was the second independent dogfood hit.

1. **Facade re-pointed.** `SparqlEngine.Update` parses with `ParseUpdateSequence()` and runs `UpdateExecutor.ExecuteSequence(...)` with its `LoadExecutor`. The whole request parses before anything executes, so a syntax error in any operation executes nothing.
2. **REPL parity by unification, not a second patch.** `TestSessionHelper` had its own copy of the facade, and that copy carried the same mis-wire. It now calls `SparqlEngine.Update` directly, so the copy is gone.
3. **Trailing content is a syntax error.** `ParseUpdateSequence` used to stop at the first operation not followed by `;` and drop the rest without a word (`INSERT DATA {…} INSERT DATA {…}`). Without this fix, the new path would have kept a silent-drop case. It now throws `Expected ';' or end of update request at position N`.
4. **Atomicity posture: sequential with abort.** Operations run in order, and each commits on its own. A failing non-SILENT operation stops the sequence, and the operations before it keep their effects. The error names the operation: `Update operation 2 of 3 failed; operations after it were not run: …`. That meets the MUST (sequential effects, failure aborts the rest). All-or-nothing, the spec's SHOULD, is not offered: an outer batch spanning operations would have to let a later `WHERE` see earlier uncommitted writes, and the batch layer does not provide that today. Cancellation is checked between operations.
5. **Found by the new tests: `LOAD SILENT` was not silent.** `LoadExecutor` applied SILENT only to thrown exceptions, so a reported failure (file not found, size or triple limit) still failed the operation and would have aborted the sequence. `ExecuteAsync` now applies SILENT around the whole load, to reported and thrown failures alike.

**Validation:** 10 facade tests in `SparqlEngineTests` (`Update_*`) and 1 REPL test drive the production entry point. They cover two inserts, the live `DELETE;INSERT` repro in a named graph, a prologue between operations, `;` inside a literal, abort on a failing middle operation, SILENT continuing, a syntax error in a later operation, a missing separator, and an empty request. All 10 failed before the fix. Mercury.Tests 4722/4722 (6 environment skips), W3C Update suite green, Mercury.Solid.Tests 25/25, solution builds with 0 warnings. The live repro through the `mercury` CLI reports `3 triples affected`, and both writes land.

**Open:** both REPL front-ends (`ReplSession`, `mercury-sparql`) use a line-ending `;` as "execute now". A multi-line request whose operations are separated at line ends therefore runs as several requests. Nothing is lost, but the request is not one request, and that is a surface-design question for those front-ends.

## References

- Root-cause evidence (file:line) recorded in the Mercury session graph `https://sky-omega.dev/sessions/2026-06-06-docs-version-audit/graph` (observation `mercury-mcp-update-multiop`).
- Companion: [ADR-045](ADR-045-graph-clause-feature-parity.md) — GRAPH-clause feature parity (separate root cause, same dogfood session, same "conformance-blind facade/path" lesson).
