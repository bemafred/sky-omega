# ADR-014: Culture Invariance for RDF/SPARQL Formatting

## Status

Accepted

## Context

Sky Omega tests failed on Windows with Swedish locale (sv-SE) while passing on macOS with English locale. Swedish uses `,` as decimal separator, but RDF and SPARQL specifications require `.` per W3C standards.

**Example of the problem:**

```csharp
// On English locale:
double value = 3.14;
value.ToString();  // Returns "3.14"

// On Swedish locale:
value.ToString();  // Returns "3,14" - INVALID for RDF!
```

The codebase already uses `CultureInfo.InvariantCulture` in most numeric formatting paths (25+ locations), but several gaps were found in:
- SPARQL CONCAT function with integer arguments
- HAVING clause COUNT evaluation
- DateTime formatting in NOW() function
- Blank node counter generation
- **String interpolation** with numeric values (e.g., `$"{doubleVal}"` uses current culture)

## Decision

All numeric and DateTime formatting in RDF/SPARQL code paths MUST explicitly specify `CultureInfo.InvariantCulture`.

### Applies to:

| Type | Pattern |
|------|---------|
| Integer | `value.ToString(CultureInfo.InvariantCulture)` |
| Double/Float | `value.ToString("G", CultureInfo.InvariantCulture)` |
| Decimal | `value.ToString(CultureInfo.InvariantCulture)` |
| DateTime | `dt.ToString("format", CultureInfo.InvariantCulture)` |

### String Interpolation Warning

**String interpolation uses the current culture by default.** This is a common source of bugs:

```csharp
// WRONG - uses current culture (Swedish: "3,14")
var formatted = $"\"{doubleVal}\"^^<xsd:double>";

// CORRECT - explicit culture
var formatted = $"\"{doubleVal.ToString(CultureInfo.InvariantCulture)}\"^^<xsd:double>";
```

When interpolating numeric values into RDF literals, always call `.ToString(CultureInfo.InvariantCulture)` explicitly within the interpolation.

### Does NOT apply to:

- `ReadOnlySpan<char>.ToString()` - culture-invariant by design
- User-facing display strings in CLI output (locale-aware is acceptable)
- Internal debugging/diagnostic messages (unless they affect test comparisons)

## Implementation

Files updated to use `CultureInfo.InvariantCulture`:

| File | Location | Type |
|------|----------|------|
| `FilterEvaluator.Functions.cs` | CONCAT function | Integer |
| `QueryResults.Modifiers.cs` | HAVING COUNT | Integer |
| `FilterEvaluator.cs` | NOW() function | DateTime |
| `ILogger.cs` | Log timestamps | DateTime |
| `JsonLdStreamParser.Values.cs` | Exponent formatting | Integer |
| `DiagnosticFormatter.cs` | Line numbers | Integer |
| `TurtleStreamParser.*.cs` | Blank node counters | Integer |
| `RdfXmlStreamParser.cs` | Blank node counters | Integer |
| `Operators.cs` | `GetBoundValue()` typed literal formatting | Integer, Double |

## Consequences

### Benefits

- Consistent behavior across all locales
- W3C RDF/SPARQL specification compliance
- Eliminates locale-dependent test failures
- Explicit intent in code (no reliance on system defaults)

### Drawbacks

- Slightly more verbose code
- Requires vigilance in code reviews

### Enforcement

- Code review checklist item
- Documented in CLAUDE.md Code Conventions section
- Grep patterns for CI:
  - `\.ToString\(\)` on numeric types without `CultureInfo`
  - `\$".*\{[a-z]*[Vv]al\}` - interpolated numeric variables without `.ToString(...)`
