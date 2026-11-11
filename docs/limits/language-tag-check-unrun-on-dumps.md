# Limit: The language-tag check has not been run against a Wikidata dump

**Status:**        Latent
**Surfaced:**      2026-11-10, at the update to rdf-tests `202a74e` ([ADR-059](../adrs/mercury/ADR-059-every-w3c-test-runs.md), Submodule updates), when the parsers began to check language tags; 2026-11-11 for base directions.
**Last reviewed:** 2026-11-11
**Promotes to:**   An ADR when a bulk load or convert of a dump stops on a literal the check rejects, or before the next full Wikidata load is started.

## Description

The N-Triples / N-Quads, Turtle and TriG parsers share one rule for a language-tagged literal,
`src/Mercury/Rdf/RdfLanguageTag.cs` ([divergence register](../divergence/README.md) S1f):

- the tag is well-formed BCP 47 (RFC 5646 §2.2.9);
- a base direction after `--` is `ltr` or `rtl`;
- `rdf:langString` and `rdf:dirLangString` are never an explicit `^^` datatype.

Before the rule, each parser accepted whatever its grammar loop scanned: N-Quads ASCII letters, digits and hyphens of
any length, Turtle any Unicode letter, TriG any character up to a delimiter. They now reject, among others, a subtag
of more than 8 characters (`zh-abcdefghi`), a language of one letter, an empty subtag (`en-`), and a non-ASCII
letter. A rejected literal is a parse error, and a parse error stops a bulk load or a convert at that line: the
parsers do not skip a bad triple and go on.

The W3C tests check the rule. Nothing has checked it against the data Mercury loads at scale: `latest-all.ttl.bz2`
(21.3 B triples, the Phase 7 source) and `latest-truthy.nt.bz2` (8.17 B). Whether either holds a tag the rule rejects
is not known. MediaWiki uses some language codes that are not BCP 47; whether any of them reach the RDF dumps is not
checked either.

Martin, 2026-11-11: *"Bulk load we can defer but make a note of it, it is very time consuming."*

## Trigger condition

- A bulk load or `ConvertAsync` of a Wikidata dump fails with "Not a well-formed language tag (BCP 47) or base
  direction (ltr, rtl)".
- A full Wikidata load is planned: check the dump first, so a 9-hour parse does not stop partway through.

## Current state

The stores loaded before 2026-11-10 (`wiki-21b-ref`, `wiki-truthy-ref`) were parsed without the check. They hold
every tag their dumps had, and they still open and query as before: the check runs only in the parsers.

## Candidate mitigations

1. **Collect the dump's distinct tags and check each.** Decompress the dump once, collect every distinct tag after
   `"…"@`, and run `RdfLanguageTag.IsWellFormed` on each. This avoids building a store. The cost is one full
   decompression of the dump.
2. **Read the distinct tags from a store that is already loaded.** The atom store of `wiki-21b-ref` holds each literal
   once, so its tags can be read without the dump. The cost of that scan has not been measured.
3. **If a dump holds rejected tags,** decide in an ADR what a bulk load does with them. A lenient parser mode is a
   behavior flag, which the no-behavior-flags rule rules out
   ([ADR-029](../adrs/mercury/ADR-029-store-profiles.md), Status). The options are a separate code path or a
   correction applied to the source data.

## References

- [ADR-059](../adrs/mercury/ADR-059-every-w3c-test-runs.md), Submodule updates: the update that added the check.
- [divergence register](../divergence/README.md), S1f: the one rule every parser shares.
- `tests/Mercury.Tests/Rdf/RdfLanguageTagTests.cs`: the tags the rule accepts and rejects.
