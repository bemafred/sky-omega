# Conformance coverage and dogfood-driven discovery

**Date:** 2026-05-17. Substrate: Mercury 1.7.72.

## Observation

On 2026-05-17, three teaching mechanisms were applied in parallel to encode a substrate-discipline rule into Mercury: an example update in `MERCURY.md`, a behavioral memory file (`feedback_use_trigram_match_for_recall.md`), and a Mercury triple in `<urn:sky-omega:discipline:recall>`. Each was meant to teach an AI agent to prefer `text:match` over `CONTAINS` for case-insensitive substring search.

Verifying the third mechanism — querying Mercury for the triple via `FILTER(text:match(?comment, "trigram"))` — surfaced two latent substrate bugs in the SPARQL execution path within 90 minutes:

1. **GRAPH-clause parser missing `;` continuation handler.** `ParseGraph` in `SparqlParser.Clauses.cs` lacked the property-list-shorthand handler that the main BGP parser (`TryParseTriplePattern` in `SparqlParser.cs`) had carried correctly all along. The result: `?s p1 ?o1 ; p2 ?o2` inside a `GRAPH` block silently dropped the second pattern. Fixed in 1.7.71.

2. **`Value.GetLexicalForm()` using `IndexOf('"')` instead of `LastIndexOf('"')`.** The function locating the closing quote of an RDF literal would stop at the first `"` — including any escaped `\"` inside the literal — truncating the lexical form returned to `CONTAINS`, `STRSTARTS`, `STRENDS`, `UCASE`, `LCASE`, `REGEX`, and any other FILTER predicate calling `GetLexicalForm()`. The peer implementation `GetLexicalForm` in `QueryResults.Modifiers.cs` had been using `LastIndexOf` correctly all along — a divergence between two parallel paths. Fixed in 1.7.72.

Both bugs passed all **421/421 W3C SPARQL 1.1 Query** conformance tests and all **103/103 SPARQL 1.1 Syntax** tests. Both were surfaced not by adding test cases but by *attempting to use the substrate the way an agent would actually use it* — insert a structured rule, recall it later via the queries the rule itself recommends.

## What this says about formal conformance

The W3C SPARQL 1.1 test suite is rigorous and the 100% pass count is real evidence of substrate maturity. But two structural coverage gaps in the canonical suite are now known by example:

- **Property-list shorthand inside `GRAPH` blocks** chained beyond a single triple with all object variables projected.
- **`CONTAINS` / `STRSTARTS` against literals containing escape sequences** (`\"`, `\\`, `\n`, etc.).

These are not exotic shapes. Real-world RDF data routinely contains escape sequences (any literal with quoted strings inside — code snippets, dialogue, structured comments). Property-list shorthand is the *recommended* SPARQL idiom for multi-predicate patterns. The conformance suite doesn't exercise their intersection with `GRAPH` blocks, or their interaction with literal-content scanning.

A substrate that passes 421/421 W3C Query tests can still be broken on the day a real workload arrives that uses these idioms together.

## What this says about validation discipline

Formal conformance suites validate **specification compliance** on the shapes the spec authors thought to test. Dogfood-driven discovery validates **workload representativeness** — *can the substrate actually do the work a real agent would ask it to do?* These are complementary disciplines, not substitutes.

The asymmetry to internalize:

> Conformance suites prove what the substrate *can* do correctly. Dogfood discovery proves what the substrate *will* do correctly for the workloads it is actually asked to carry. The first is necessary; the second is what the user experiences.

The cost-of-discovery curve is also asymmetric. The 1.7.71 + 1.7.72 arc cost ~90 minutes from first symptom to second fix shipped. The same bugs sitting latent in the substrate, found six months later by an external user trying to use Mercury via MCP, would have cost trust — substantially more than the engineering hours.

## Implications for Sky Omega's discipline

- **Substrate hardening claims should be paired with dogfood evidence**, not just conformance counts. When 1.7.69 was framed as "substrate hardening done," the framing was technically correct under the conformance lens — but the 1.7.71 + 1.7.72 fixes show that "done" was premature once the substrate was actually exercised against real recall-discipline content.

- **Every public-facing example in `MERCURY.md` is a dogfood-driven test in disguise.** When the example uses a Mercury-specific feature (`text:match`), running it against the actual substrate is the first validation pass. The decision to embed examples in user-facing documentation pays off in this loop.

- **Coverage-gap observations are themselves substrate artifacts.** This file is one. Future substrate work should preserve the "where does conformance not exercise" record alongside the "what does conformance prove" record. The limits register (`docs/limits/`) is one home for characterized gaps; the conformance-coverage-gap framing belongs at the methodology layer.

- **Two parallel implementations of the same logic are a recurring risk site.** Both bugs fixed today were divergences: `ParseGraph` vs `TryParseTriplePattern` (different `;` handling), `Value.GetLexicalForm` vs `QueryResults.Modifiers.GetLexicalForm` (different `IndexOf` choice). Pattern: when the same semantic operation lives in two code paths, one path can drift while the other stays correct, and conformance tests may exercise only the corrected path. A periodic audit for "same operation, two implementations" would surface this category of drift before users do.

## How this fits EEE

This observation lives in the **Emergence** phase of the EEE methodology — it surfaces an unknown unknown about the substrate-validation regime that was operating up through 1.7.70. Promoting it to Epistemics would require articulating a concrete discipline (e.g., "every release ships with a paired dogfood validation alongside the conformance count"). Promoting that to Engineering would mean wiring an automated dogfood pass into the release workflow.

The observation is filed now as Emergence-phase substrate. The next time a substrate-hardening claim is being prepared, this file is one input.

## References

- 1.7.71 fix: `SparqlParser.Clauses.cs ParseGraph` (commit `a016314`). Limits entry: [`property-list-shorthand-projection.md`](../../limits/property-list-shorthand-projection.md).
- 1.7.72 fix: `FilterEvaluator.cs Value.GetLexicalForm / GetLangTagOrDatatype` (commit `0a2f8f9`).
- The dogfood trigger: commit `6338a87` inserting the `<urn:sky-omega:discipline:recall>` triple to teach `text:match` over `CONTAINS`.
- W3C SPARQL conformance counts: [`STATISTICS.md`](../../../STATISTICS.md) — Core 1,181/1,181, SPARQL 1.1 Query 421/421, all unchanged across this arc.
