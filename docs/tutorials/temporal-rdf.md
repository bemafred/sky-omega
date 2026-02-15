# Temporal RDF

Mercury stores every triple with a time dimension. Facts have a period when
they were true (valid time) and a record of when they were stored (transaction
time). This enables time-travel queries, version tracking, and historical
reconstruction -- all through SPARQL.

> **Prerequisites:** Mercury installed and a working REPL. See
> [Getting Started](getting-started.md) if you haven't done that yet.

---

## Why Temporal Matters

Most databases store the current state. When a fact changes, the old value is
gone. Mercury keeps the old value with its time bounds, so you can answer
questions like:

- Where did Alice work in 2021?
- When did this configuration change?
- What did we know about this customer last quarter?
- Show me every version of this entity.

No audit tables. No change-data-capture pipelines. The history is built in.

---

## The Two Time Dimensions

Mercury uses a **bitemporal** model with two independent time axes:

| Dimension | Question it answers | Set by |
|-----------|-------------------|--------|
| **Valid time** | When was the fact true in reality? | You (explicitly) |
| **Transaction time** | When was the fact recorded in the database? | Mercury (automatically) |

Valid time is the domain truth: "Alice worked at Anthropic from 2020 to 2023."
Transaction time is the system truth: "We recorded this fact on 2024-01-15."

Together they let you ask: "What did we *know* on date X about what was *true*
on date Y?"

---

## Recording Temporal Facts

### Current facts (default)

When you use INSERT DATA or `AddCurrent()`, Mercury stores the triple with
valid-from = now and valid-to = infinity. This is the normal case for data
that is true right now with no known end date:

```sparql
INSERT DATA {
    <http://ex.org/alice> <http://ex.org/worksFor> <http://ex.org/Acme> .
}
```

### Facts with explicit time bounds

From code, use the temporal `Add()` method to specify when a fact was true:

```csharp
// Alice worked at Anthropic from 2020 to mid-2023
store.Add(
    "<http://ex.org/alice>",
    "<http://ex.org/worksFor>",
    "<http://ex.org/Anthropic>",
    new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero));

// Alice works at OpenAI from mid-2023 onwards
store.Add(
    "<http://ex.org/alice>",
    "<http://ex.org/worksFor>",
    "<http://ex.org/OpenAI>",
    new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero),
    DateTimeOffset.MaxValue);
```

Both facts coexist in the store. Neither overwrites the other.

---

## Querying Time

Mercury extends SPARQL with four temporal modes. The mode appears after the
solution modifiers (LIMIT, OFFSET, ORDER BY).

### Current (default)

Without any temporal clause, queries return facts valid right now:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
}
```

This returns only OpenAI (assuming the current date is after 2023-07-01).

### AS OF -- point-in-time

Travel to a specific date and see what was true then:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
} AS OF "2021-06-15"^^xsd:date
```

This returns Anthropic, because that's where Alice worked on that date.

AS OF accepts both `xsd:date` and `xsd:dateTime`:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
} AS OF "2023-07-01T12:00:00Z"^^xsd:dateTime
```

### DURING -- time range

Find facts that overlap with a period:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
} DURING ["2022-01-01"^^xsd:date, "2024-01-01"^^xsd:date]
```

This returns both Anthropic and OpenAI, because both employment periods
overlap with 2022-2024.

### ALL VERSIONS -- complete history

Show every version of a fact, regardless of time:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
} ALL VERSIONS
```

This returns both Anthropic and OpenAI with their full validity periods.

---

## Practical Examples

### Employment history

Track where someone has worked over time:

```csharp
var periods = new[]
{
    ("Google",    new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero),
                  new DateTimeOffset(2018, 12, 31, 0, 0, 0, TimeSpan.Zero)),
    ("Anthropic", new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
                  new DateTimeOffset(2022, 6, 30, 0, 0, 0, TimeSpan.Zero)),
    ("OpenAI",    new DateTimeOffset(2022, 7, 1, 0, 0, 0, TimeSpan.Zero),
                  DateTimeOffset.MaxValue)
};

foreach (var (company, from, to) in periods)
{
    store.Add("<http://ex.org/bob>", "<http://ex.org/worksFor>",
              $"<http://ex.org/{company}>", from, to);
}
```

Then query any point in time:

```sparql
-- Where did Bob work in 2017?
SELECT ?company WHERE {
    <http://ex.org/bob> <http://ex.org/worksFor> ?company .
} AS OF "2017-06-01"^^xsd:date
```

Result: Google.

```sparql
-- Where did Bob work in 2020?
SELECT ?company WHERE {
    <http://ex.org/bob> <http://ex.org/worksFor> ?company .
} AS OF "2020-06-01"^^xsd:date
```

Result: Anthropic.

### Version tracking

Track a value that changes over time, like a salary:

```csharp
var salaryHistory = new[]
{
    (80000,  new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
             new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)),
    (90000,  new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero),
             new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero)),
    (100000, new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero),
             new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)),
    (120000, new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
             DateTimeOffset.MaxValue)
};

foreach (var (salary, from, to) in salaryHistory)
{
    store.Add("<http://ex.org/charlie>", "<http://ex.org/salary>",
              $"\"{salary}\"", from, to);
}
```

View the full evolution:

```sparql
SELECT ?salary WHERE {
    <http://ex.org/charlie> <http://ex.org/salary> ?salary .
} ALL VERSIONS
```

### Snapshot reconstruction

Reconstruct the complete state of an entity at any point in history:

```sparql
-- What did we know about Country1 on 2012-01-01?
SELECT ?predicate ?value WHERE {
    <http://ex.org/Country1> ?predicate ?value .
} AS OF "2012-01-01"^^xsd:date
```

This returns every fact about Country1 that was valid on that date -- population,
capital, name, or whatever else was recorded with overlapping time bounds.

### Bitemporal corrections

Sometimes you record a fact and later discover the dates were wrong. With
bitemporal storage, the correction doesn't erase the original record:

```csharp
// Initially recorded: Eve worked at Acme 2020-2023
store.Add("<http://ex.org/eve>", "<http://ex.org/worksFor>",
          "<http://ex.org/Acme>",
          new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
          new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));

// Later discovered: Actually left in late 2022
store.Add("<http://ex.org/eve>", "<http://ex.org/worksFor>",
          "<http://ex.org/Acme>",
          new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
          new DateTimeOffset(2022, 12, 31, 0, 0, 0, TimeSpan.Zero));
```

Both records exist in the store with different transaction times. You can
query what was known at any point:

- **Current query** returns the corrected end date (2022-12-31)
- **ALL VERSIONS** shows both the original and corrected records
- The transaction time on each record shows when each version was stored

---

## The Code API

The SPARQL temporal extensions map directly to the QuadStore API:

| SPARQL Mode | API Method | Description |
|-------------|-----------|-------------|
| *(default)* | `QueryCurrent()` | Facts valid at `UtcNow` |
| `AS OF` | `QueryAsOf()` / `TimeTravelTo()` | Facts valid at a specific time |
| `DURING` | `QueryChanges()` | Facts overlapping a time range |
| `ALL VERSIONS` | `QueryEvolution()` | Complete history of all versions |

### Writing temporal data

| Method | Behavior |
|--------|----------|
| `AddCurrent(s, p, o)` | Stores with valid-from=now, valid-to=max |
| `Add(s, p, o, from, to)` | Stores with explicit valid-time bounds |
| `AddCurrentBatched(s, p, o)` | Batch version for bulk loading |
| `AddBatched(s, p, o, from, to)` | Batch version with explicit bounds |

---

## Running the Examples

The examples project includes runnable temporal demonstrations:

```bash
dotnet run --project examples/Mercury.Examples -- temporal
```

This runs seven temporal examples: basic temporal triples, time-travel queries,
version tracking, range queries, bitemporal corrections, evolution tracking,
and snapshot reconstruction.

---

## See Also

- [Getting Started](getting-started.md) -- first-time setup
- [Mercury CLI](mercury-cli.md) -- temporal queries in the REPL
- [Pruning and Maintenance](pruning-and-maintenance.md) -- managing temporal
  history growth
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF basics
