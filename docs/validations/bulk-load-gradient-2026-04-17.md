# Bulk Load Scale Gradient — 2026-04-17

**Status:** Found + fixed two bugs on first gradient step. Fresh convert re-running in background; gradient resumes when it completes.

Phase 4.1 of [ADR-027](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md). Objective: observe Mercury's bulk-load behavior at increasing scales.

## Timeline

| Local time | Event |
|---|---|
| ~19:39 | Extract 1 M slice from `latest-all.nt`, attempt bulk load at 1.7.5 |
| ~19:40 | Bulk load crashes at triple ~2,718 with `Expected '.' after object` |
| ~19:45 | Diagnosed: `NTriplesStreamWriter.WriteLiteral` does not re-escape quotes unescaped by parser |
| ~20:00 | Fix applied + tests added, released as 1.7.6 |
| ~21:00 | Sanity check on 1.7.6 via `mercury --convert`: **still buggy**. Root cause: `RdfEngine.ConvertAsync` bypasses the writer entirely for N-Triples output, writing spans directly to `StreamWriter` |
| ~21:10 | Second fix in `RdfEngine.ConvertAsync` routes through `NTriplesStreamWriter`, released as 1.7.7 |
| ~21:15 | Sanity + round-trip verified end-to-end; 3.0 TB corrupt `latest-all.nt` deleted; fresh convert started under `nohup` |

## Bugs found and fixed

### Bug 1 — `NTriplesStreamWriter.WriteLiteral` forward-scan with escape tracking

**Symptom:** literals containing `\"` in source Turtle emitted as unescaped `"` in N-Triples, truncated at first internal quote.

**Root cause:** Turtle parser unescapes `\"` → `"` in memory (correct — the in-memory form is the logical lexical value). The writer's forward scan with backslash tracking then had no information to distinguish "internal quote" from "closing quote", always chose the first `"`.

**Fix:** backward suffix-aware close-quote detection. Datatype form ends with `>` (find `^^<`); lang form ends with lang-tag chars (find `@"`); plain form ends with `"`. The unambiguous suffix shape pins the close quote regardless of escape state.

**Location:** `src/Mercury/NTriples/NTriplesStreamWriter.cs:140+` (`WriteLiteral` method, plus `IsLangChar` helper).

**Released as:** 1.7.6.

### Bug 2 — `RdfEngine.ConvertAsync` bypassed the writer entirely

**Symptom:** after deploying 1.7.6, `mercury --convert` still produced buggy output.

**Root cause:** `ConvertAsync` had a hot-path optimization that wrote spans directly to `StreamWriter` without routing through `NTriplesStreamWriter.WriteTriple`. The writer's escape logic was therefore dormant for the convert command.

**Fix:** construct a `NTriplesStreamWriter` when output is N-Triples and call `WriteTriple`; dispose/flush appropriately.

**Location:** `src/Mercury/RdfEngine.cs:546+` (`ConvertAsync` method).

**Released as:** 1.7.7.

## Regression tests added

`tests/Mercury.Tests/Rdf/NTriplesStreamWriterTests.cs` gained:

1. `WriteTriple_PlainLiteralWithInternalQuotes_ReEscapes` — the exact `Entity[\"...\", \"...\"]` case
2. `WriteTriple_LangTaggedLiteralWithInternalQuotes_ReEscapes`
3. `WriteTriple_DatatypedLiteralWithInternalQuotes_ReEscapes`
4. `WriteTriple_LiteralWithInternalBackslashAndQuote_ReEscapesBoth`
5. `WriteTriple_EmptyLiteral_PlainPreserved`
6. `WriteTriple_EmptyLiteralWithLang_LangPreserved`
7. `WriteTriple_LexicalEndsWithQuoteAndHasLang_ClosingFound`
8. `WriteTripleAsync_PlainLiteralWithInternalQuotes_ReEscapes`
9. `RoundTrip_TurtleLiteralWithEscapedQuotes_ParsesBack` — **end-to-end round trip** Turtle → N-Triples → parse. This is the test shape that would have caught the bug.

All 27 writer tests pass at 1.7.7.

## Coverage gap that let this through

W3C RDF conformance tests Turtle→triples and N-Triples→triples **separately**. They do not test Turtle→N-Triples→parse round-trip. Mercury's own conformance runs followed the same shape. The bug lived in the gap between them for as long as Mercury has emitted N-Triples.

**Prevention going forward:** test #9 above establishes the pattern. Any future serializer (TriG, RDF/XML, JSON-LD) should have an equivalent round-trip regression test from Turtle.

## Fresh convert — in progress

Started 2026-04-17 ~21:15 local with `mercury 1.7.7`, detached via `nohup ... & disown`. The process will continue running even if Claude Code exits.

- Command: `mercury --convert latest-all.ttl latest-all.nt --metrics-out /tmp/convert-1.7.7-metrics.jsonl`
- PID: 20085
- stdout/stderr: `/tmp/convert-1.7.7-stdout.log`
- Initial observation at 5 s in: 10 M triples, ramping to ~2.2 M/sec (warming up)
- Expected completion: ~2 h 15 m from start → ~23:30 local (2026-04-17)

When it completes, the resulting `latest-all.nt` should be valid N-Triples end-to-end. The gradient (1 M → 10 M → 100 M → 1 B) can resume against it.

## Gradient table (updated)

| Scale | Input size | Status | Notes |
|---|---|---|---|
| 1 M | 134 MB | HALTED → resumable after fresh convert | Surfaced both N-Triples writer bugs |
| 10 M | — | pending fresh convert | — |
| 100 M | — | pending fresh convert | — |
| 1 B | — | pending fresh convert | — |
| 21.3 B (full) | — | pending | — |

## Scope-discipline note

The "parser validated at 21.3 B" finding from this morning's run stands for the Turtle parser (it reached EOF, handled every input construct, flat throughput). What was falsified by the bulk-load attempt is that the **convert output was a clean byte artifact** — it wasn't, and that's now fixed. The distinction: parser correctness ≠ convert-path correctness. Round-trip tests verify both together.

## Provenance

- Bugs surfaced, fixed, and re-released in a single evening session, 2026-04-17
- Temp files: `/tmp/sanity-input.ttl`, `/tmp/sanity-output.nt` (tiny test files demonstrating the fix)
- Mercury semantic memory updated with bug findings and resolutions
- No commits to git yet — all changes in working tree for morning review
