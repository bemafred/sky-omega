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

1. **Add the arithmetic layer** (`[116] Additive` / `[117] Multiplicative` / `[118]` unary `+`/`-`) to FilterEvaluator's value grammar, between `Comparison` and `Term`. Purely additive capability — full suite must stay green, plus the FILTER-arithmetic case above goes green.
2. **Add `EvaluateToValue()`** — a `Value`-returning form of the top-level grammar (`||`/`&&`/`!`/comparison/`IN` produce boolean-typed `Value`s; a bare term returns its `Value`). Reuses the existing function library and `Value` helpers — only the thin connective layer is mirrored, not the 40 functions. `FILTER Evaluate()` becomes `CoerceToBool(EvaluateToValue())`, removing the bool-grammar duplication.
3. **Route the 9 BIND call sites** (`QueryExecutor.cs` ×3, `QueryResults.Modifiers.cs` ×2, `QueryResults.Patterns.cs` ×2, `QueryResults.cs` ×1, `TreeJoinExecutor.cs` ×1) from `new BindExpressionEvaluator(...)` to `FilterEvaluator(...).EvaluateToValue(...)`. The BIND-`||` case above goes green.
4. **Delete `BindExpressionEvaluator`** (~2,340 lines).

Add permanent conformance tests for the two demonstrated gaps (and the EBV/error-vs-unbound asymmetry) so the spec behaviour is locked, not just incidentally passing.

## Consequences

- **Conformance fix:** FILTER gains arithmetic; BIND gains `||`/`&&`/`!` and the 11 boolean predicates — both per `[110]`.
- **One evaluator** of `[110] Expression`; FILTER and BIND differ only in the final EBV-vs-bind step, exactly as the spec frames it. ~2,340 lines deleted.
- **Risk:** the most conformance-sensitive change in the divergence register (hundreds of W3C FILTER+BIND tests). Mitigated by the strictly-incremental sequence with the full W3C suite green after each step, and by grounding the target in the spec grammar rather than either (divergent) implementation — the same EBNF-oracle discipline as [ADR-047](ADR-047-default-path-cutover.md).
- **Zero-GC preserved:** both are already ref structs over one `Value`; the merge removes a parser, it does not add allocation.

## Related

- `docs/divergence/README.md` S2 — the divergence this closes.
- [ADR-047](ADR-047-default-path-cutover.md) — the "ground the oracle in the spec/EBNF, not a contaminated implementation" lesson, applied here.
