# Query time-limit probes

[ADR-060](../../../docs/adrs/mercury/ADR-060-query-time-limit-and-cancellation.md) checks its claims about .NET's
regular expressions and about Mercury's REGEX and REPLACE with these two probes. They print what they see and assert
nothing. Its validation plan re-runs them after the change.

| Probe | Shows |
|---|---|
| `regex-facts.cs` | what `(a+)+$` costs as the input grows; how long a match runs past its timeout; which timeouts the `Regex` constructor accepts; that no constructor takes a `TimeProvider`; that `Match.Result` in a `MatchEvaluator` expands a replacement exactly as `Replace(string, string)` does |
| `sparql-regex.cs` | whether a regex timeout in FILTER, BIND, a SELECT expression, OPTIONAL and REPLACE fails the query or is turned into an expression error; what REPLACE returns for the replacement strings where XPath's `fn:replace` differs from .NET |

```bash
cd tools/probes/query-time-limit
dotnet run --no-cache regex-facts.cs     # stops once one input takes over 10 s
dotnet run --no-cache sparql-regex.cs
```

## Results on omega, 2026-10-31 (`1518dae`, .NET 10.0.9, osx-arm64, idle)

`regex-facts.cs`:

- `(a+)+$` on n `a`s and a `!`: n = 16 took 9.1 ms, 18 took 33.7 ms, 20 took 128.9 ms, 22 took 487.9 ms, 24 took
  1,952 ms, 26 took 10,543 ms. About double for each `a`; finite at every n.
- Time until `RegexMatchTimeoutException` on forty `a`s and a `!`: a 1-tick limit 1.781 ms, 1 ms 0.981 ms, 10 ms
  9.996 ms, 100 ms 99.976 ms (by `Stopwatch`).
- Constructor: zero rejected; one tick, 24 days and `InfiniteMatchTimeout` accepted; `int.MaxValue` ms, one more,
  and 25 days rejected.
- Public constructors: `Regex(String)`, `Regex(String, RegexOptions)`, `Regex(String, RegexOptions, TimeSpan)`.
- `Replace(input, r)` and `Replace(input, m => m.Result(r))` agree in all nine cases.

`sparql-regex.cs`:

- FILTER, BIND, SELECT expression, FILTER inside OPTIONAL, REPLACE inside BIND: each query fails with the regex's
  timeout message (Mercury's fixed 100 ms limit), none returns rows.
- REPLACE differs from XPath in six of nine cases; the table is in ADR-060, Context.

After the interim limit (one second per match, ADR-060 Q1), the same five queries each fail with the regex's
timeout; the whole probe, build included, took 6.0 s on omega. The REPLACE outputs are unchanged.
