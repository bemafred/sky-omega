# ADR-049: One SPARQL Expression Evaluator — FILTER and BIND share `[110] Expression`

## Status

**Status:** Accepted — 2026-06-20 (Epistemics. The SPARQL 1.1 grammar settles the design — both FILTER and BIND consume the one `[110] Expression` production — and the two current evaluators are each a non-conformant *half* of it, empirically demonstrated. The decision is validated by the spec, not by either implementation; what remains is the careful, W3C-verified engineering of the merge.)

## Context

Mercury has **two** independent SPARQL expression evaluators (divergence register S2), both zero-GC ref structs over the *same* `Value` ref struct + `ValueType` enum (`FilterEvaluator.cs:2080`):

- **`FilterEvaluator`** (`Sparql/Execution/Expressions/FilterEvaluator.cs` + `.Functions.cs`, ~3,200 lines) — used by FILTER. Full logical grammar `Or→And→Unary(!)→Primary→Comparison→Term`, `IN`/`NOT IN`, and **52** built-in functions including the 11 boolean predicates (`CONTAINS`/`REGEX`/`STRSTARTS`/`STRENDS`/`LANGMATCHES`/`sameTerm`/`text:match`/`isIRI`/…). Returns `bool` (EBV) at the top; only the primary/function layer returns `Value`. **No arithmetic.**
- **`BindExpressionEvaluator`** (`BindExpressionEvaluator.cs`, ~2,340 lines) — used by BIND. `Comparison→Additive→Multiplicative→Unary→Primary` with **arithmetic**, and **41** functions. Returns `Value`. **No `||`/`&&`/`!`**, and lacks the 11 boolean predicates.

### What the spec says (the oracle, not the implementations)

SPARQL 1.1 Query Language grammar (verbatim from the W3C Recommendation). Both forms use the *one* `[110] Expression` production:

- `[60] Bind ::= 'BIND' '(' Expression 'AS' Var ')'`
- FILTER: `[68] Constraint ::= BrackettedExpression | BuiltInCall | FunctionCall`; `[72] BrackettedExpression ::= '(' Expression ')'`

`[110] Expression` is the full chain, with **no** FILTER-only or BIND-only sub-grammar:

```
[110] Expression            ::= ConditionalOrExpression
[111] ConditionalOrExpr     ::= ConditionalAndExpr ( '||' ConditionalAndExpr )*
[112] ConditionalAndExpr    ::= ValueLogical ( '&&' ValueLogical )*
[113] ValueLogical          ::= RelationalExpression
[114] RelationalExpression  ::= NumericExpr ( ( '=' | '!=' | '<' | '>' | '<=' | '>=' | 'IN' | 'NOT' 'IN' ) NumericExpr )*
[115] NumericExpression     ::= AdditiveExpression
[116] AdditiveExpression    ::= MultiplicativeExpr ( ( '+' | '-' ) MultiplicativeExpr )*
[117] MultiplicativeExpr    ::= UnaryExpression ( ( '*' | '/' ) UnaryExpression )*
[118] UnaryExpression       ::= ( '!' | '+' | '-' )? PrimaryExpression
[119] PrimaryExpression     ::= BrackettedExpression | BuiltInCall | iriOrFunction | RDFLiteral | NumericLiteral | BooleanLiteral | Var
[120] BrackettedExpression  ::= '(' Expression ')'
[121] BuiltInCall           ::= Aggregate | 'STR' '(' Expression ')' | 'LANG' … | (all built-ins)
```

An expression evaluates to **an RDF term or an error** (§17). The *only* FILTER-vs-BIND difference is post-evaluation consumption:

- **FILTER** applies the **effective boolean value** (§17.2.2); an expression error → the FILTER eliminates the solution.
- **BIND**: *"If the evaluation of the expression produces an error, the variable remains unbound for that solution but the query evaluation continues"* (§18).

### The current split is non-conformant (empirically demonstrated)

Each evaluator implements a *different half* of `[110]`. Verified 2026-06-20 against the running code (store: Alice 30, Bob 25, Charlie 35):

| Query | Spec-correct | Mercury today | Cause |
|---|---|---|---|
| `FILTER(?age + 10 > 40)` | 1 row (Charlie) | **3 rows (all)** | FilterEvaluator has no `[116]` arithmetic → silently evaluates `EBV(?age)` |
| `BIND((?age > 100 \|\| ?age < 100) AS ?x)` | `?x = true` | **`?x = false`** | BindExpressionEvaluator has no `[111]` `\|\|` → silently returns `(?age > 100)` |

So this convergence is a **conformance fix**, not merely de-duplication. (These exact silent bugs are not caught by the current W3C suite — the conformance corpus does not exercise FILTER-with-arithmetic or BIND-with-`||` — which is itself why the divergence persisted.)

### Why not a shared function library instead

The ~40 shared built-ins (`CONCAT`/`SUBSTR`/`REPLACE`/hashes/…) are **parser-coupled**: each parses its own comma-separated arguments inline from the source span (e.g. `ParseConcatFunction`), so they cannot be lifted into a pure `(Value…) → Value` library. The only real convergence is a single evaluator of the whole `[110]` grammar.

## Decision

Implement `[110] Expression` **once**, as a `Value`-producing recursive-descent evaluator (result = RDF term **or** `ValueType.Unbound` for error), with the two consumers deriving their behaviour:

- **FILTER** `Evaluate()` → `CoerceToBool(EvaluateToValue())` — EBV; an error/unbound → `false` (eliminate the solution).
- **BIND** → `EvaluateToValue()` — bind the term; an error/unbound → leave the variable unbound.

`FilterEvaluator` is the base (it already has the full logical grammar, `IN`, and the superset of functions). The merge is **incremental and W3C-verified at every step**:

1. **Add `EvaluateToValue()`** — the full `Value`-producing `[110]` grammar at spec precedence (`|| → && → relational/IN → additive → multiplicative → unary(!,+,-) → primary`), with the logical connectives implementing SPARQL's three-valued logic (§17.4.1.5/6) and a bare term returning its `Value`. Reuses FilterEvaluator's function library, comparison ops, `IN`, and EBV; only arithmetic (`Add`/`Subtract`/`Multiply`/`Divide`/`Negate`, ported from BindExpressionEvaluator) is added. Lives in `FilterEvaluator.Value.cs`. **Done — `EvaluateToValue` built and compiling, additive (suite 4687/0/6); not yet wired.**
2. **Base-IRI parity (prerequisite for wiring BIND)** — BindExpressionEvaluator carries a `_baseIri` used by `IRI()`/`URI()` to resolve a relative IRI (`BindExpressionEvaluator.cs:839/859`); FilterEvaluator has no such field (its base handling is for PREFIX expansion only). Add a base-IRI field to FilterEvaluator + thread it through `EvaluateToValue`, and have FilterEvaluator's `IRI`/`URI` resolve against it (additive — FILTER gains the same, must stay green). *Discovered during step 1.*
3. **Route the 9 BIND call sites** (`QueryExecutor.cs` ×3, `QueryResults.Modifiers.cs` ×2, `QueryResults.Patterns.cs` ×2, `QueryResults.cs` ×1, `TreeJoinExecutor.cs` ×1) from `new BindExpressionEvaluator(...)` to `FilterEvaluator(...).EvaluateToValue(...)`. The BIND-`||` case above goes green; add it as a permanent test. **⚠ Attempted 2026-06-20, reverted — see finding below.**
4. **Switch FILTER** `Evaluate()` to `CoerceToBool(EvaluateToValue())`, deleting the old bool-grammar duplication; the FILTER-arithmetic case goes green (add as a permanent test).
5. **Delete `BindExpressionEvaluator`** (~2,340 lines).

### Corrected finding (2026-06-20): the function libraries *diverge in correctness*, not just in coverage

Wiring BIND to `EvaluateToValue` (step 3) regressed **~10 W3C function tests** — `NOW`, `MINUTES`, `SECONDS`, `HOURS`, `MONTH`, `YEAR`, `DAY`, `TIMEZONE`, `TZ`, `BNODE` — and **hung** the run (an infinite loop in `EvaluateToValue` on some expression). Reverted to the step-1 baseline (green); substrate kept valid. The root cause reshapes the plan:

> The two evaluators' **shared** function implementations are not equivalent. The date/time and `BNODE` functions are **conformant in BindExpressionEvaluator** (the W3C corpus exercises them via `BIND(NOW() …)`) but **buggy/incomplete in FilterEvaluator** — where they were never tested, because no FILTER test invokes a date function. So FilterEvaluator is **not** a conformant superset; it has *more* functions but *some shared ones are wrong*. "Route BIND through FilterEvaluator" assumed a conformant superset that does not exist.

**Revised plan — a function-reconciliation phase is a prerequisite to wiring.** Before step 3 can land: (a) ~~fix the `EvaluateToValue` infinite loop~~ **done — see "Reconciliation (a) done" below**; (b) *(output-form punch-list **done** — see "Reconciliation (b) done" below; the `@lang`/datatype literal-parsing gap remains, sequenced into wiring)* build **one conformant function library** — for each of the ~41 shared builtins, keep the implementation the W3C corpus validates (BindExpressionEvaluator's for date/time/`BNODE`; FilterEvaluator's for the 11 boolean predicates), with the spec as the final oracle, and route both consumers through it. This is the real cost of S2 and is larger than this ADR first scoped. Step 1 (the grammar) stands; steps 2–5 wait on the reconciliation phase.

### Enumerated divergences (2026-06-20 differential probe)

A throwaway probe evaluated a battery of constant expressions through *both* evaluators and diffed the results (BindExpressionEvaluator = the conformant reference). **31 of 39 functions/operators MATCH** (all arithmetic, `STR`/`STRLEN`/`UCASE`/`LCASE`/`CONCAT`/`SUBSTR`/`STRBEFORE`/`STRAFTER`/`ENCODE_FOR_URI`, the hashes, `DATATYPE`, `STRDT`, `IF`, `COALESCE`, `ABS`, `YEAR`/`MONTH`/`DAY`/`HOURS`/`MINUTES`, plain `BNODE()`). The divergences are concentrated and almost all about **output lexical form** — FilterEvaluator emits bare values, BindExpressionEvaluator emits the full canonical form needed for atom storage:

| Function | BindExpressionEvaluator (conformant) | FilterEvaluator (current) | Fix |
|---|---|---|---|
| `NOW()` | `"…"^^<xsd:dateTime>` | bare `2026-…Z` | wrap as typed literal |
| `TIMEZONE(NOW())` | `"PT0S"^^<xsd:dayTimeDuration>` | bare `PT0S` | wrap as typed literal |
| `TZ(NOW())` | `"Z"` (quoted) | bare `Z` | quote as simple literal |
| `REPLACE(…)` | `"bbb"` (quoted) | bare `bbb` | quote as simple literal |
| `IRI`/`URI`/`UUID()` | `<…>` (angle-bracketed Uri) | bare `…` | bracket (IRI/URI fixed by step 2; UUID too) |
| `CEIL`/`FLOOR`/`ROUND` | `Double` (decimal arg → decimal result) | `Integer` | preserve numeric datatype |
| `SECONDS(NOW())` | full precision `12.798093` | truncated `12.798` | match precision |
| `BNODE("x")` | `_:r0_x` (per-row seed) | `_:x` | apply the row-seed labeling |

So the reconciliation is a **defined punch-list of output-form alignments in FilterEvaluator's function library**, not scattered logic bugs — verifiable function-by-function against the probe (oracle = the conformant evaluator) while the FILTER suite stays green. The `EvaluateToValue` hang is separate (not triggered by any constant expression; involves a variable or a specific construct — find via a variable-bearing probe).

Add permanent conformance tests for the two demonstrated gaps (and the EBV/error-vs-unbound asymmetry) so the spec behaviour is locked, not just incidentally passing.

### Reconciliation (a) done — the hang is the weak argument parser (2026-06-20)

The hang is **not** a non-advancing grammar path in `EvaluateToValue` itself, and it **is** reachable by a constant expression — the step-1 constant probe simply never passed a *compound* argument to a function. Root cause, verified by a `blame-hang` dump on `COALESCE(1 + 1, 0)`:

> `FilterEvaluator`'s function-argument parsers consume each argument with the single-term **`ParseTerm()`**, not the full `[110]` grammar. A compound argument (arithmetic, comparison, `||`) leaves the parser parked on the operator. The variadic argument while-loops (`COALESCE`/`CONCAT`) then never advance → **infinite loop**; the fixed-arity functions don't hang but silently return wrong results (they read only the first term). `BindExpressionEvaluator` (conformant) parses every argument with `ParseAdditive` — the arithmetic-bearing grammar — which is exactly why BIND never hung. **This is also a pre-existing latent FILTER bug:** `FILTER(COALESCE(1 + 1, 0))` hangs on the shipped path today; it was never hit because no W3C FILTER test passes a compound argument to a function.

**Fix:** route every function-argument parse through the full value grammar — all 37 argument call sites changed from `ParseTerm()` to `ValueOr()` (the `[110] Expression` entry): 34 in `FilterEvaluator.Functions.cs` (every site is an argument), plus the generic single-arg site, the `BNODE(label)` argument, and the `IN`-list item in `FilterEvaluator.cs`. For a single-term argument `ValueOr()` returns exactly what `ParseTerm()` did (it parses the term, finds no operator, returns it), so the FILTER path is behaviour-identical for everything the suite exercises; the only change is that compound arguments now evaluate instead of hanging. **Verified: full W3C suite 4692/0/6** (step-1 baseline 4687 + 5 probe tests in `Adr049EvaluateToValueProbeTests`), zero regressions. The `ParseTerm()`/`ParseAdditive` split is the structural form of the S2 divergence at the argument level — closing it is a prerequisite the punch-list (b) sits on top of.

### Reconciliation (b) done for the output-form punch-list (2026-06-21)

A differential probe (`Adr049FunctionReconciliationProbeTests`, oracle = `BindExpressionEvaluator`) drove the punch-list and all eight families now converge in `FilterEvaluator`: `NOW`/`STRUUID`/`TZ` quoted; `NOW`/`TIMEZONE` wrapped as typed literals; `UUID`/`IRI`/`URI` angle-bracketed (consistent with how `ParseFullUri` stores a literal IRI — this also fixed a latent FILTER bug where `IRI(x)` couldn't compare equal to `<x>`); `CEIL`/`FLOOR`/`ROUND` preserve the `Double` datatype (and `ROUND` is away-from-zero); `SECONDS` parses the seconds field from the lexical form at full precision; `BNODE(label)` is per-row-seeded (`_:r<seed>_<label>`) via the new `FilterEvaluator.IncrementBnodeRowSeed()`. Six `FilterEvaluatorTests` that pinned the *old bare* forms were updated to the conformant forms (5 `TIMEZONE` now assert `STR(TIMEZONE(?x))` against the lexical value; 1 `BNODE` asserts the row-seed shape). The FILTER path is otherwise unaffected because `CompareEqual` normalises strings through `GetLexicalForm`/`GetLangTagOrDatatype` and coerces numerics — quoting and Double-vs-Integer are invisible to EBV; only `TIMEZONE`'s datatype suffix is (correctly) visible. **Verified: full W3C suite green**, plus the differential + non-deterministic-form probes.

#### Newly surfaced — `ParseStringLiteral` drops datatypes and `@lang` (a wiring prerequisite, not done here)

A completeness sweep over the rest of the shared library surfaced one **separate, deeper** divergence whose root cause is `FilterEvaluator.ParseStringLiteral`: it stores only the lexical content for a non-numeric typed literal (it drops `^^<…dayTimeDuration>`) and it does not parse the `@lang` suffix at all (it strips it). Two consequences: a typed-literal comparison such as `TIMEZONE(?x) = "PT0S"^^xsd:dayTimeDuration` can't round-trip (the right operand loses its datatype — which is why the `TIMEZONE` tests assert via `STR(...)`); and inline `@lang` literals in expressions diverge for `DATATYPE` (`xsd:string` vs the correct `rdf:langString`), `UCASE`/`LCASE` (lose the tag), and `CONCAT` (returns unbound). This is shared, pre-existing, and has FILTER-path blast radius (FILTER currently *drops* `@lang`), so it is **its own increment with its own full-suite verification, sequenced into the wiring phase (step 3)** — not folded into the output-form punch-list. (One case is the reverse: `LANG("plain")` → `""` is the spec-correct FILTER behaviour; `BindExpressionEvaluator` returns unbound there — the **spec**, not either implementation, is the oracle.)

## Consequences

- **Conformance fix:** FILTER gains arithmetic; BIND gains `||`/`&&`/`!` and the 11 boolean predicates — both per `[110]`.
- **One evaluator** of `[110] Expression`; FILTER and BIND differ only in the final EBV-vs-bind step, exactly as the spec frames it. ~2,340 lines deleted.
- **Risk:** the most conformance-sensitive change in the divergence register (hundreds of W3C FILTER+BIND tests). Mitigated by the strictly-incremental sequence with the full W3C suite green after each step, and by grounding the target in the spec grammar rather than either (divergent) implementation — the same EBNF-oracle discipline as [ADR-047](ADR-047-default-path-cutover.md).
- **Zero-GC preserved:** both are already ref structs over one `Value`; the merge removes a parser, it does not add allocation.

## Related

- `docs/divergence/README.md` S2 — the divergence this closes.
- [ADR-047](ADR-047-default-path-cutover.md) — the "ground the oracle in the spec/EBNF, not a contaminated implementation" lesson, applied here.
