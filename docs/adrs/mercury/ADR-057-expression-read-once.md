# ADR-057: An Expression Is Read Once — The Parser Reads Every Expression by the [110] Grammar, and the Evaluator Runs What It Read

## Status

**Status:** Proposed — 2026-10-28

Found by the SPARQL 1.0 W3C syntax triage. syn-bad-26, syn-bad-29 and syn-bad-30 are expressions that are not
SPARQL; Mercury accepts each one, and its FILTER keeps no row. Martin chose an ADR over a recognizer beside the
evaluator ("ADR first", 2026-10-28): a recognizer would be a second copy of the expression grammar, and this ADR is
about deleting copies.

## Context

### How an expression is read today (1.8.30 + `b19484a`)

1. **The parser finds where an expression ends, and nothing more.** FILTER, BIND, SELECT and GROUP BY expressions,
   aggregate arguments, HAVING and ORDER BY are recorded as source spans. Their ends are found with the token rule
   (`SkipExpressionToken`, `SkipBracketedGroup`): string literals, IRIs and brackets, but no grammar. A parenthesized
   FILTER is stepped over as one bracketed group.
2. **The evaluator reads the span's text for every row.** `FilterEvaluator` is a recursive-descent interpreter over the
   text: each row re-lexes and re-parses the expression and evaluates it in the same pass. Ten places construct one
   (`TreeJoinExecutor` 3, `QueryResults.Modifiers` 5, `QueryResults` 1, `QueryResults.Patterns` 1).
3. **The evaluator reads what it can, and an expression it can't read is an error value, not a syntax error.** Trailing
   text after a complete expression is not read at all.

### What that accepts (probe on `b19484a`, one stored `?s ?p "5"^^xsd:integer`)

| Expression | Result | SPARQL |
|---|---|---|
| `FILTER(_:x)` | parses, 0 rows | syntax error: no blank node in an expression (syn-bad-29) |
| `FILTER(?o<?a&&?b>?o)` | parses, 0 rows | syntax error: `<?a&&?b>` is an IRIREF, so `?o <iri> ?o` (syn-bad-26) |
| `FILTER(?o > )` | parses, 0 rows | syntax error |
| `FILTER(1 +)` | parses, 0 rows | syntax error |
| `FILTER(STRLEN())`, `FILTER(STRLEN(?o, ?o))` | parse, 0 rows | syntax error: STRLEN takes one argument |
| `FILTER(nosuchfn(?o))` | parses, 0 rows | syntax error: not a built-in, and not an IRI |
| `FILTER([] = ?o)` | parses, 0 rows | syntax error |
| **`FILTER(?o ?o)`** | **parses, 1 row** | syntax error; the second `?o` is never read |
| **`FILTER(?o = 5 5)`** | **parses, 1 row** | syntax error; the second `5` is never read |
| **`BIND(?o ?o AS ?x)`** | **parses, `?x` = `?o`** | syntax error |
| `BIND(1 + AS ?x)` | parses, `?x` unbound | syntax error |

The last group gives answers, not just missing errors: an expression with text the evaluator never reads.

### The evaluator's lexer is a copy

The parser and the evaluator each read the tokens of an expression, with readers that have drifted apart:

| Token | Parser (one reader since `4400a34`) | Evaluator |
|---|---|---|
| String literal | `SkipStringLiteral`: four quote forms, ECHAR, surrogate check | `ParseStringLiteral` |
| Language tag | `ParseLiteral`: [145] LANGTAG | its own loop: letters, digits and '-' anywhere |
| Prefixed name | `TryReadPnameNs` + `ReadPnLocal`: [140]–[173], Unicode, PLX | `CodePointOps.IsPrefixedNameLocalChar` |
| Function name | — | `ParseFunctionCall`: ASCII letters, digits, '_' and ':' |
| Prefix expansion | `PrefixExpander`: throws on an undeclared prefix (§4.1.3), removes PN_LOCAL_ESC | `FilterEvaluator.ExpandPrefixedName`: null on an undeclared prefix, which ADR-056 D2 made an error value; keeps escapes |
| Numeric literal | `ParseNumericLiteral` | `ParseNumericLiteral` / `Value.FromNumericToken` |

Each fix to a reader in the parser (the prefixed names, strings and language tags of `4400a34`) leaves the evaluator's
copy as it was. The aggregate path also reads its argument expression with no prologue: its evaluator gets no prefixes
(`GroupedRow.UpdateAggregates`).

### What is not known

The cost of reading the text again for every row has not been measured. Reading once could make evaluation faster;
this ADR makes no claim about it until a measurement exists (validation step 5).

## Decision

### D1. The parser reads every expression by the [110] grammar

When a query or Update is parsed, each expression is read by [110] Expression and its productions ([111]–[128],
[121] BuiltInCall with each built-in's arity, [128] iriOrFunction). Anything the grammar does not read is a
`SparqlParseException` with its position: trailing text, a missing operand, a blank node, an unknown built-in name, a
built-in with the wrong number of arguments. Every row of the probe table becomes a syntax error.

### D2. One reading: the parser's output is what the evaluator runs

The parser writes what it read into a compact form, and the evaluator evaluates that form for each row. The evaluator no
longer reads text, so its token readers (the evaluator column of the table above) are deleted, and with them the second
grammar. One grammar, read once, by the parser's lexer.

The form is an expression array, in the manner of `PatternArray`: fixed-size nodes in a pooled buffer, no allocation
per row. A node is an operator, a function with its argument count, or a term: a variable (its name's hash), or a
constant as it was written. A constant is classified once, at parse time, with `Value.Literal` (ADR-056 D1). Its exact
layout is Q1.

### D3. Names are expanded when the expression is read

Prefixed names, in IRIs, datatypes and function names, are expanded when the expression is read, by `PrefixExpander`,
the expander patterns use, so escapes are removed the same way. Whether an undeclared prefix in an expression becomes a
static error, as in a pattern, is Q3.

### D4. Every site evaluates through the one form

FILTER, BIND, SELECT and GROUP BY expressions, aggregate arguments, HAVING and ORDER BY each reference their expression
by its index in the array, from the tree slot or the clause that holds it. The aggregate path gets the query's
prologue, so a prefixed name inside an aggregate argument is expanded like any other.

## Consequences

- Malformed expressions are syntax errors at parse time, with a position, for queries and Updates (ADR-046: a syntax
  error anywhere means nothing is executed).
- The evaluator's token readers are deleted, and with them drift between the two lexers.
- A query keeps one more buffer, the expression array, for its lifetime.
- `FilterEvaluator`'s public shape changes. It is internal, but the tests that construct it over a text
  (`FilterEvaluatorTests` and others) move to the parsed form.

## Open questions

- **Q1. The node layout.** Postfix (one pass, an operand stack) or a tree (child indexes)? Short-circuit `&&`, `||`, IF
  and COALESCE need to skip a subtree, which a tree gives directly; postfix needs jump offsets. Either way fixed-size
  nodes, with a constant's text referenced by source offset.
- **Q2. EXISTS inside an expression.** `FILTER(?a && EXISTS { … })` embeds a group, parsed today into its own pattern
  tree (`EmbeddedExistsIn`). The expression node would hold that tree's index.
- **Q3. An undeclared prefix in an expression.** ADR-056 D2 made it an error value, so a FILTER keeps no row. With D3
  it could be a static error, as in a pattern (§4.1.3). That would change ADR-056 D2.
- **Q4. Constants with no prologue.** The aggregate path and EXPLAIN read expressions without a query's prologue today.
  D4 gives the aggregate path one; EXPLAIN may need it too.

## Validation plan

1. **W3C:** syn-bad-26, syn-bad-29 and syn-bad-30 pass, and leave the triage. SPARQL 1.1, 1.0 and the RDF suites stay
   green.
2. **The probe table:** each row is a syntax error, as a test each.
3. **Mercury's own tests** stay green. Those that build an evaluator over text move to the parsed form, each change
   named in the commit.
4. **Zero-GC and stack:** the FILTER allocation tests pass, and the stack probes (`tools/probes/stack`) are rerun.
5. **Measured cost:** `FilterBenchmarks` (`benchmarks/Mercury.Benchmarks/SparqlBenchmarks.cs`) before and after, to
   report evaluation time per row. It is a measurement, not a target.
6. **Dogfood:** memory recall through the MCP, filtering with `text:match`, a date and a number, after a reconnect.

## References

- [ADR-049](ADR-049-unified-expression-evaluator.md): one [110] evaluator for FILTER and BIND (S2).
- [ADR-055](ADR-055-query-where-one-parse.md): a query's WHERE parsed once, by the pattern tree; the model for D2.
- [ADR-056](ADR-056-expression-value-is-the-term.md): an expression value is the RDF term; D1's `Value.Literal`, D2's
  undeclared prefix (Q3).
- SPARQL 1.1 Query Language, §17 (expressions), §19.8 grammar [110]–[128], §4.1.3 (prefixed names).
