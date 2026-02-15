# Pruning and Maintenance

Mercury's temporal storage accumulates history over time. Pruning compacts the
store by physically removing soft-deleted data and optionally flattening
history, filtering out specific graphs or predicates, and verifying the result.

> **Prerequisites:** A working Mercury installation with data in the store.
> See [Getting Started](getting-started.md) for setup and
> [Temporal RDF](temporal-rdf.md) for how temporal history accumulates.

---

## Why Pruning Matters

Every fact Mercury stores has a valid-time range. When a fact changes, the old
version is soft-deleted (its valid-to is set) and a new version is written.
Over time, the store accumulates:

- Soft-deleted triples that are no longer current
- Multiple versions of the same fact
- Temporary or debug data you no longer need
- Graphs that have served their purpose

Pruning physically removes this data by copying the live triples to a fresh
store and swapping it in. The old store is then discarded.

---

## How Pruning Works

Pruning uses a **copy-and-switch** pattern:

1. A fresh secondary store is created
2. Triples from the primary store are filtered and written to the secondary
3. The primary and secondary stores are swapped
4. The old primary (now secondary) is cleared

This is atomic from the application's perspective -- queries continue against
the primary store during the transfer, and the swap happens in a single
operation.

---

## CLI Pruning

### Basic prune

From the Mercury REPL:

```
mercury> :prune
Prune complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Space saved:   45.2 MB
  Duration:      320ms
```

### Dry run

Always preview first. A dry run enumerates and filters without writing:

```
mercury> :prune --dry-run
Prune dry-run complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Duration:      280ms
```

The "quads written" count shows how many *would* be kept. The difference
(1,234 - 1,100 = 134) is how many would be removed.

### History modes

By default, pruning flattens history to keep only the current version of
each triple. Use `--history` to change this:

```
mercury> :prune --history preserve
```

| Mode | Flag value | What it keeps |
|------|-----------|---------------|
| Flatten to current | *(default)* | Only the latest version of each triple |
| Preserve versions | `preserve` | All versions, but removes soft deletes |
| Preserve all | `all` | Everything including soft deletes (rewrite only) |

**Flatten to current** produces the most compact store. Use it when you don't
need historical queries.

**Preserve versions** keeps the version timeline intact for AS OF and ALL
VERSIONS queries, but removes triples that have been logically deleted.

**Preserve all** is a pure rewrite -- it doesn't remove anything, but
rebuilds the store files for compaction.

### Excluding graphs

Remove an entire named graph from the pruned store:

```
mercury> :prune --exclude-graph <http://example.org/temp>
```

Multiple exclusions:

```
mercury> :prune --exclude-graph <http://example.org/temp> --exclude-graph <http://example.org/scratch>
```

### Excluding predicates

Remove all triples with specific predicates:

```
mercury> :prune --exclude-predicate <http://example.org/debug>
```

### Combining options

Options compose freely:

```
mercury> :prune --dry-run --history preserve --exclude-graph <http://example.org/temp> --exclude-predicate <http://example.org/debug>
```

---

## MCP Pruning

Claude can prune the MCP store using the `mercury_prune` tool. The parameters
map directly to the CLI options:

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `dryRun` | bool | false | Preview without writing |
| `historyMode` | string | "flatten" | "flatten", "preserve", or "all" |
| `excludeGraphs` | string | null | Comma-separated graph IRIs to exclude |
| `excludePredicates` | string | null | Comma-separated predicate IRIs to exclude |

### When to prune via MCP

Ask Claude to prune when:

- `mercury_stats` shows the store growing beyond what you expect
- You've loaded temporary data that is no longer needed
- You want to flatten history after a period of heavy changes

Claude can also check the store size and suggest pruning proactively:

```
"Check mercury_stats. If there are more than 10,000 quads,
do a dry-run prune to see what can be removed."
```

---

## Monitoring Store Growth

### From the CLI

```
mercury> :stats
Store Statistics:
  Quads:           1,234
  Atoms:           567
  Storage:         12.3 MB
```

### From MCP

The `mercury_stats` tool returns the same information. Check it periodically
to track growth.

### What to watch for

- **Quad count growing faster than expected:** May indicate redundant inserts
  or excessive versioning
- **Storage size disproportionate to quad count:** Soft-deleted data
  accumulating
- **Atom count much higher than quad count:** Many unique strings; consider
  whether all are necessary

---

## Backup Before Pruning

Pruning is destructive -- it permanently removes data from the store. Always
back up first:

```bash
# Stop Mercury first, then copy the store directory
cp -r ~/Library/SkyOmega/stores/cli/ ~/backups/cli-$(date +%Y%m%d)/
```

For MCP stores:

```bash
# Stop mercury-mcp first
cp -r ~/Library/SkyOmega/stores/mcp/ ~/backups/mcp-$(date +%Y%m%d)/
```

Alternatively, use the dry-run flag to preview the effect before committing.

---

## The Pruning API

For programmatic pruning, use the `PruningTransfer` class:

### Basic usage

```csharp
using var target = new QuadStore("/path/to/new/store");
var result = new PruningTransfer(source, target).Execute();
// Soft-deleted quads are now physically gone
```

### With filters

```csharp
var options = new TransferOptions
{
    Filter = CompositeFilter.All(
        GraphFilter.Exclude("<http://temp.data>"),
        PredicateFilter.Exclude("<http://internal/debug>")),
    HistoryMode = HistoryMode.FlattenToCurrent
};

var result = new PruningTransfer(source, target, options).Execute();
```

### Available filters

| Filter | Factory methods | Description |
|--------|----------------|-------------|
| `GraphFilter` | `Include(...)`, `Exclude(...)`, `DefaultGraphOnly()`, `NamedGraphsOnly()` | Filter by graph IRI |
| `PredicateFilter` | `Include(...)`, `Exclude(...)` | Filter by predicate IRI |
| `CompositeFilter` | `All(...)` (AND), `Any(...)` (OR) | Combine multiple filters |

### Verification options

| Option | Default | Description |
|--------|---------|-------------|
| `DryRun` | false | Preview without writing |
| `VerifyAfterTransfer` | false | Re-enumerate and verify counts match |
| `ComputeChecksum` | false | FNV-1a checksum for content verification |
| `AuditLogPath` | null | Write filtered-out quads to N-Quads file |
| `BatchSize` | 10,000 | Quads per batch (higher = faster, more memory) |
| `ProgressInterval` | 100,000 | Report progress every N quads |

### Audit log

Set `AuditLogPath` to write filtered-out quads to a file. This lets you
recover pruned data if needed:

```csharp
var options = new TransferOptions
{
    AuditLogPath = "/path/to/pruned-quads.nq"
};
```

The audit log is written in N-Quads format -- you can re-load it with
`mercury-sparql --load pruned-quads.nq` if you need the data back.

---

## Recipes

### Weekly maintenance

A simple maintenance routine for a production store:

```
mercury> :stats
mercury> :prune --dry-run
mercury> :prune
mercury> :stats
```

Compare the before and after stats to confirm the prune worked as expected.

### Remove temporary graphs

After importing data for analysis, clean up:

```
mercury> :prune --exclude-graph <http://example.org/import-2025-09>
```

### Flatten history for a fresh start

If you don't need temporal queries and want the smallest possible store:

```
mercury> :prune --history flatten
```

This keeps only the current version of each triple, discarding all history.

---

## See Also

- [Temporal RDF](temporal-rdf.md) -- how temporal history accumulates
- [Mercury CLI](mercury-cli.md) -- REPL commands including `:prune`
- [Mercury MCP Server](mercury-mcp.md) -- the `mercury_prune` tool
- [Federation and SERVICE](federation-and-service.md) -- querying across
  instances
