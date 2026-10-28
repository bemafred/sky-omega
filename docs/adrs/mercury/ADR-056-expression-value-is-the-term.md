# ADR-056: An Expression Value Is the RDF Term — The Evaluator Keeps the Term It Read, and Compares by the SPARQL Operator Mapping

## Status

**Status:** Accepted — 2026-10-28

Split from [ADR-053](ADR-053-one-rdf-term-one-atom.md), whose D3 ("A binding → its stored form") needed the evaluator
to keep the term. ADR-053 changes what the store holds and needs a migration, with two questions open (Q2, Q3). This
part changes no stored data and needs no migration, so it goes first. Accepted by Martin on 2026-10-28 ("split ADR-053
and do part 1"). Found by the SPARQL 1.0 W3C triage (step 3: 55 known failures on literal equality, term identity,
type promotion, casts, `str`, `lang` and the effective boolean value).

## Context

### What the evaluator does with a term (probe on 1.8.28 + `00dddff`)

The probe stores one object per term form and evaluates an expression over it with `BIND`:

| Stored term | `?o` (BIND) | `str(?o)` | `?o != 1` | `sameTerm(?o, 1)` |
|---|---|---|---|---|
| `"1"^^xsd:integer` | `1` | `1` | false | true |
| `"01"^^xsd:integer` | `1` | `1` | false | **true** |
| `"1"^^xsd:short` | `1` | `1` | false | **true** |
| `"1.0"^^xsd:decimal` | `1` | `1` | false | false |
| `"1.0e0"^^xsd:double` | `1` | `1` | false | false |
| `"a"^^<http://t/type1>` | the term | `a` | **true** | false |
| `"abc"@en` | the term | `abc` | true | false |
| `<http://ex/i>` | the term | `http://ex/i` | true | false |

Also:
- `datatype(xsd:float(?o))` is `xsd:double`, and `xsd:decimal("1.0e0"^^xsd:double)` stays a double.
- `lang(<http://ex/i>)` is `""`; it is an error (§17.4.2.6).
- `?o = "a"^^t:type1` is false even for the stored `"a"^^<http://t/type1>`: a datatype written as a prefixed name is
  expanded only when its prefix is `xsd:`.
- `?o = "abc"@en` is false for the stored `"abc"@EN`.
- `FILTER(! ?w)` with `?w` unbound keeps the row: `!` of an error is true, where it must be an error (§17.2).
- `1e3` is read as `1`, and the `e3` is left unparsed.

### Why

`Value`, the evaluator's carrier, has four kinds of literal (`String`, `Integer`, `Double`, `Boolean`) and one text
field:
- **A numeric term keeps only its number.** `"01"^^xsd:short` becomes the long 1. The lexical form and the datatype
  are gone, so `str`, `sameTerm`, `datatype` and a BIND of the value can't recover them. A decimal rides on `Double`.
- **The text field holds two different things.** A literal read from a binding holds the wrapped term (`"x"@en`); a
  literal constant and most function results hold the bare content (`x`). Every function guesses which with
  `GetLexicalForm` / `GetLangTagOrDatatype`.
- **A datatype is recognized by substring.** `Contains("int")` makes `<http://example/point>` an integer type when
  its lexical form parses as one.
- **Comparison has two outcomes**, true and false. SPARQL's `=` has three: RDFterm-equal is an error for two literals
  it can't compare (§17.4.1.7), and the error, not false, is what `!=`, `!` and `||` see.
- **Five sites turn a value into a binding**, each its own way: BIND in `TreeJoinExecutor`, SELECT expressions and
  HAVING-time expressions in `QueryResults.Modifiers`, sub-SELECT projection in `QueryResults.Patterns`, ORDER BY
  conditions, and aggregate arguments. Some bind a typed number, some format a term, and one drops the datatype.

### What SPARQL requires (§17.2, §17.3, §17.4.1.7)

The W3C open-world, expr-equals and boolean-effective-value tests pin down the readings where the spec leaves room.
In open-eq-10, `?v1 != ?v2` over eight terms each side gives 52 of 64 rows, and the 12 missing ones are exactly the
pairs below that are errors. In date-2, a dateTime is `!=` a date.

- **Operator mapping first.** Two numerics compare by value after promotion. Two strings (simple literal or
  `xsd:string`), two booleans, two dateTimes or two dates compare by value.
- **Otherwise RDFterm-equal.**
  - An IRI or a blank node equals only the same term, and is never an error.
  - A language-tagged literal never equals a literal without a tag. Two tagged literals are equal when the lexical
    forms are, and the tags are equal ignoring case (RDF 1.1 Concepts §3.3: the value space of tags is lower case).
  - Two literals of known types with disjoint value spaces (a number and a date, a string and a boolean) are not
    equal.
  - Any other pair of literals (an unknown datatype, or an ill-typed literal such as `"xyz"^^xsd:integer`) is equal
    when it is the same term, and an error otherwise.
- **`<`, `>`, `<=`, `>=`** are defined for the same pairs as the operator mapping. Anything else is an error.
- **Effective boolean value:**
  - a boolean is its value;
  - a numeric is false when it is 0 or NaN;
  - a string is false when it is empty;
  - an ill-typed numeric or boolean is false;
  - anything else, an unbound variable included, is an error.
  - `!`, `&&` and `||` follow the §17.2 truth tables, and FILTER keeps a row only when the value is true.
- **Arithmetic** promotes integer → decimal → float → double. Integer `/` integer is a decimal, and decimal division by
  zero is an error (XPath op:numeric-divide). Every integer subtype (`xsd:short`, `xsd:unsignedLong`, …) is an
  integer, and a sum of two is an `xsd:integer`.
- **A constant evaluates to its term**: `01` is `"01"^^xsd:integer`, `1.0` is `"1.0"^^xsd:decimal`, `1e3` is
  `"1e3"^^xsd:double`, `+1` is `"+1"^^xsd:integer` ([146]–[155]).

## Decision

**A `Value` is an RDF term. It keeps the term it was read from, and the evaluator compares, converts and binds terms
by the SPARQL operator mapping.**

### D1. The carrier

- `ValueType` gets a kind for each class of literal the operator mapping tells apart:
  - `String` (a simple literal or `xsd:string`) and `LangString`;
  - `Integer` (every integer subtype), `Decimal`, `Float`, `Double` and `Boolean`;
  - `Typed`, for any other datatype: a dateTime, a date, an unknown type, or an ill-typed numeric.
  - `Uri`, `BlankNode` and `Unbound` (the error) stay.
- A literal's text fields always mean one thing:
  - `StringValue` is the lexical form, decoded (ADR-044);
  - `Language` is the tag, without the `@`;
  - `Datatype` is the datatype IRI in angle brackets.
  - For an IRI, `StringValue` is `<iri>`; for a blank node, `_:label`.
- A numeric or boolean read from a term keeps its lexical form and datatype next to its value. A computed one has
  neither; its term is the canonical form of its type (`xsd:integer`, `xsd:decimal`, `xsd:float`, `xsd:double`,
  `xsd:boolean`).
- `decimal` is its own field and kind. A float is carried in the `double` field, rounded to float precision.
- **One reader.** A stored term, read from a binding, and a literal constant, read from the expression, are
  classified by the same rule: the datatype IRI is matched exactly against the XSD datatypes, and the lexical form
  must be valid for the type, or the literal is `Typed`.

### D2. The datatype of a constant is an IRI

A prefixed datatype in an expression (`"a"^^t:type1`) is expanded through the query's prefixes, as every prefixed
name is. An undeclared prefix is an error, so the comparison it's in is an error too. Today only `xsd:` is expanded,
and it is expanded even when undeclared.

### D3. Comparison has three outcomes

`=`, `!=`, `<`, `>`, `<=`, `>=`, `IN` and `NOT IN` give true, false or an error, by the rules in *Context*:
- `!=` is the negation of `=`, and an error stays an error.
- `IN` is the `||` of the `=` tests, so it is an error when nothing matches and one test is an error.
- `sameTerm` compares terms: the same kind, lexical form, datatype and language tag. A computed value is compared
  through its canonical term.

### D4. The effective boolean value is one function

FILTER, `!`, `&&`, `||` and IF's condition all use one EBV, which can be an error. IF's condition is evaluated in the
same context as the rest of the expression: today it is evaluated without the query's prefixes.

### D5. Numbers, casts and the term functions follow the types

- **Arithmetic and the numeric functions** (`ABS`, `ROUND`, `CEIL`, `FLOOR`) keep the promoted type.
- **The casts follow XPath casting (§17.5).** `xsd:float` makes a float. `xsd:decimal` makes a decimal, and rejects
  an exponent in a string and NaN or INF in a double. `xsd:dateTime` makes a dateTime from a dateTime or a valid
  string.
- **The term functions read the kept term.**
  - `str`, `datatype` and `sameTerm` use the kept term.
  - `lang` of anything but a literal is an error.
  - A string function's language tag is carried in `Language`, not rebuilt as text.
- **Numeric constants are read by the grammar:** an exponent ([148]), a leading `.` ([147]), and a sign that is part
  of the literal ([149]–[154]).

### D6. One way from a value to a binding

The `Value` writes its own term, and all five sites bind that term. A binding is then always a term:
- `BIND(01 AS ?x)` binds `"01"^^xsd:integer`, `BIND(1 + 1 AS ?x)` binds `"2"^^xsd:integer`, and `BIND("a" AS ?x)`
  binds `"a"`.
- Today, a string computed by BIND is bound bare (`a`), which is neither the stored form of `"a"` nor a term.

### D7. Terms written by earlier writers

Until ADR-053's migration (its D5) has run, a store holds two forms that no current RDF parser writes: a datatype
written as the prefixed name `xsd:T` (819 atoms in the memory store), and a bare numeric or boolean (26). The
evaluator reads the first as the XSD datatype `T`, and the second as SPARQL's shorthand for that literal.

This is what the evaluator does today, through its substring test, so a recall FILTER on a stored date keeps working.
It is a read rule only: these forms are never written, and pattern matching doesn't use the rule. ADR-053 D5 deletes
the rule once the stores are migrated.

The 33 atoms with an empty datatype (ADR-053 Q3) read as an unknown datatype, so comparing one by value is an error.
Today they compare as text.

## Consequences

- **Positive:**
  - `str`, `datatype`, `sameTerm` and BIND return the term that was stored. `"01"` stays `"01"`, `xsd:short` stays
    `xsd:short`, and `1.0` stays a decimal.
  - The evaluator follows the SPARQL error rules, so `!=` and `!` no longer keep rows whose comparison has no answer.
  - Five value-to-binding sites become one, and the text field no longer has two meanings.
- **Negative:**
  - **Results change where they were wrong.** A BIND or SELECT expression returns full terms (`"2"^^xsd:integer`, not
    `2`; `"a"`, not `a`). `"5" + 1`, `"30" = 30`, `<a> < <b>` and FILTER on an IRI are errors where Mercury used to
    coerce. A FILTER that relied on the coercion now drops its rows.
  - `Value` grows by a `decimal` and two spans. This touches the zero-GC evaluator, so the allocation tests and the
    stack probes are rerun, not assumed.
- **Neutral:**
  - Storage, the scan and pattern matching don't change. A constant in a pattern (`{ ?s :p "a"^^t:type1 }`) still
    misses: that is ADR-053 D1.

## Validation plan

1. **Probe matrix as tests.** The table in *Context*, plus `datatype`, the casts, `lang`, and the EBV of each kind,
   asserted per term form.
2. **The SPARQL 1.0 triage.** Each test whose cause this ADR covers passes and leaves the triage list. These are
   open-world, expr-equals, expr-builtin (`sameTerm`, `str`, `lang`, langMatches, language-tag case),
   type-promotion, cast and boolean-effective-value. Those that remain are listed with their cause.
3. **The full W3C suites** stay green.
4. **Mercury's own tests** that expected a coercion or a bare value are updated to the spec, each change named in the
   commit.
5. **Zero-GC and stack.** The FILTER allocation tests pass, and the stack probes are rerun.
6. **Dogfood:** a memory recall that filters on a stored `^^xsd:date` value and on a number, through the MCP after a
   reconnect.

## References

- [ADR-053](ADR-053-one-rdf-term-one-atom.md): the write side and the migration. D7 here is deleted by its D5.
- [ADR-044](ADR-044-sparql-update-literal-canonicalization.md): literals are stored decoded.
- [ADR-049](ADR-049-unified-expression-evaluator.md): the one [110] evaluator this changes.
- SPARQL 1.1 Query §17.2 (EBV, error truth tables), §17.3 (operator mapping), §17.4.1.7 (RDFterm-equal), §17.5
  (casting); XPath Functions and Operators §6 (numeric operators); RDF 1.1 Concepts §3.3.
