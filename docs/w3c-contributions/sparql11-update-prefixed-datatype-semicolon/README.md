# W3C SPARQL 1.1 Update Test Contribution

**Target:** `github.com/w3c/rdf-tests`, path `sparql/sparql11/syntax-update-1/`
**Prepared:** 2026-04-17
**Source:** Sky Omega / Mercury

## Coverage gap

The W3C SPARQL 1.1 `syntax-update-1` manifest (manifest-sparql11-update.ttl) contains 54 entries. None exercise the following grammatically-valid pattern:

> A `PrefixedName` form of the datatype IRI in an `RDFLiteral` (e.g. `"x"^^xsd:date`) immediately followed by a `;` continuation in `PropertyListNotEmpty` inside `QuadData`.

This syntax is legal per SPARQL 1.1 Update productions:

- `[37] InsertData ::= 'INSERT DATA' QuadData`
- `[47] QuadData ::= '{' Quads '}'`
- `[50] TriplesTemplate ::= TriplesSameSubject ('.' TriplesTemplate?)?`
- `[51] TriplesSameSubject ::= VarOrTerm PropertyListNotEmpty | TriplesNode PropertyList`
- `[52] PropertyListNotEmpty ::= Verb ObjectList (';' (Verb ObjectList)?)*`
- `[129] RDFLiteral ::= String ( LANGTAG | ( '^^' iri ) )?`
- `[136] iri ::= IRIREF | PrefixedName`
- `[137] PrefixedName ::= PNAME_LN | PNAME_NS`

The `^^ iri` slot explicitly accepts `PrefixedName`. The `;` continuation is the standard predicate-list separator. The combination is common in real-world SPARQL Update usage.

## How the gap was discovered

2026-04-17, while storing bitemporal session observations in Mercury (Sky Omega's BCL-only RDF triple store) via its MCP server, an `INSERT DATA` containing `sky:date "2026-04-17"^^xsd:date ;` failed to parse with `Expected '}' but found ';'`.

Differential isolation confirmed:

| Variant | Result |
|---|---|
| `;` chain, plain literals | works |
| `;` chain, literal with `;` inside string | works |
| Full-IRI datatype `"x"^^<http://...>` + `;` | works |
| **Prefixed datatype `"x"^^xsd:date` + `;`** | **fails** |

Running the 94-test `manifest-sparql11-update.ttl` suite against a known-correct implementation passes 100% — but the minimal reproducer (grammatically valid) fails in the specific path. The suite has a genuine coverage hole.

## Contribution

Two positive-syntax tests:

- **`syntax-update-55.ru`** — `INSERT DATA` in the default graph: prefixed datatype + `;` continuation.
- **`syntax-update-56.ru`** — `INSERT DATA` inside a `GRAPH` block: prefixed datatype + `;` continuation.

Both are `mf:PositiveUpdateSyntaxTest11` — the query must parse successfully; no execution required.

The manifest addition is in `manifest.patch.ttl`. To apply upstream: insert the two entries inside the `mf:entries` list and add `:test_55` / `:test_56` to the entry sequence.

## Submission plan

1. Fork `github.com/w3c/rdf-tests`
2. Add the two `.ru` files to `sparql/sparql11/syntax-update-1/`
3. Apply the manifest patch
4. Open a PR referencing this gap analysis

## Local use before upstream merges

Mercury already exercises the same grammar path via a hand-authored xUnit regression (`tests/Mercury.Tests/Sparql/SparqlParserTests.cs`, `InsertData_PrefixedDatatypeFollowedBySemicolon_*`). When upstream merges these two `.ru` files, the next `./tools/update-submodules.sh` will pull them into the conformance run automatically — no further work needed on our side.
