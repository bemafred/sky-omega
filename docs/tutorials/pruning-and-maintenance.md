# Pruning and Maintenance

A Cognitive store keeps what it has forgotten: a delete ends a memory's period, and the period stays in the store.
Pruning compacts the store by copying what is remembered into a fresh store, each memory with the time it was
memorized, and leaving out what is forgotten. It can also filter out specific graphs or predicates, and verify the
result.

> **Prerequisites:** A working Mercury installation with data in the store.
> See [Getting Started](getting-started.md) for setup and
> [Memory Time](temporal-rdf.md) for how forgotten memories accumulate.

---

## Why Pruning Matters

When a fact is deleted it is forgotten: the store keeps the period in which it was remembered, with both times, and
`FORGOTTEN` and `INCLUDING FORGOTTEN` still find it. Over time, the store accumulates:

- Forgotten memories you no longer need to ask about
- Earlier periods of facts that were forgotten and memorized again
- Temporary or debug data you no longer need
- Graphs that have served their purpose

Pruning removes this data by copying what is remembered to a fresh store and swapping it in. The old store is then
discarded.

---

## What a Prune Keeps

There is one way to prune ([ADR-061](../adrs/mercury/ADR-061-a-facts-own-times.md) D12):

- **What is remembered is kept**, each memory with the time it was memorized. "What did we memorize in January"
  answers on the pruned store as it did on its source.
- **Nothing forgotten is kept.** A `FORGOTTEN` question on the pruned store finds nothing, and `INCLUDING FORGOTTEN`
  finds nothing more than the default view. A store in which everything is forgotten prunes to an empty store.
- **A prune never changes a time.**
- **A graph in which nothing is remembered** is not carried over.

To keep every period, forgotten ones included, copy the store directory instead of pruning it.

---

## How Pruning Works

Pruning uses a **copy-and-switch** pattern:

1. A fresh secondary store is created
2. What the primary store remembers is filtered and written to the secondary, with its memorized times
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
cli> :prune
Prune complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Space saved:   45.2 MB
  Duration:      320ms
```

### Dry run

Always preview first. A dry run enumerates and filters without writing:

```
cli> :prune --dry-run
Prune dry-run complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Duration:      280ms
```

The "quads written" count shows how many *would* be kept. The difference
(1,234 - 1,100 = 134) is how many remembered quads the filters would remove.

### Excluding graphs

Remove an entire named graph from the pruned store:

```
cli> :prune --exclude-graph <http://example.org/temp>
```

Multiple exclusions:

```
cli> :prune --exclude-graph <http://example.org/temp> --exclude-graph <http://example.org/scratch>
```

### Excluding predicates

Remove all triples with specific predicates:

```
cli> :prune --exclude-predicate <http://example.org/debug>
```

### Combining options

Options compose freely:

```
cli> :prune --dry-run --exclude-graph <http://example.org/temp> --exclude-predicate <http://example.org/debug>
```

The `--history` option of earlier versions is gone, and `mercury` refuses it with the reason: a prune that ignored it
would remove the forgotten memories its user asked to keep.

---

## MCP and Pruning

Pruning is not exposed over MCP ([ADR-006](../adrs/ADR-006-mcp-surface-discipline.md)): an AI agent should not start
an operation that removes memories on its own. Stop `mercury-mcp`, open its store in the CLI with
`mercury --store mcp`, and run `:prune` there.

---

## Monitoring Store Growth

### From the CLI

```
cli> :stats
Store Statistics:
  Quads:           1,234
  Atoms:           567
  Storage:         12.3 MB
```

### From MCP

The `mercury_stats` tool returns the same information. Check it periodically
to track growth.

### What to watch for

- **Quad count growing faster than expected:** May indicate redundant inserts,
  or many facts forgotten and memorized again
- **Storage size disproportionate to quad count:** Forgotten memories
  accumulating
- **Atom count much higher than quad count:** Many unique strings; consider
  whether all are necessary

---

## Backup Before Pruning

Pruning is destructive -- it permanently removes forgotten memories from the store. Always
back up first:

```bash
# Stop Mercury first, then copy the store directory
STORES=~/Library/SkyOmega/stores         # macOS
# STORES=~/.local/share/SkyOmega/stores  # Linux
cp -r "$STORES/cli/" ~/backups/cli-$(date +%Y%m%d)/
```

For MCP stores:

```bash
# Stop mercury-mcp first
STORES=~/Library/SkyOmega/stores         # macOS
# STORES=~/.local/share/SkyOmega/stores  # Linux
cp -r "$STORES/mcp/" ~/backups/mcp-$(date +%Y%m%d)/
```

Alternatively, use the dry-run flag to preview the effect before committing.

---

## The Pruning API

For programmatic pruning, use the `PruningTransfer` class:

### Basic usage

```csharp
using var target = new QuadStore("/path/to/new/store");
var result = new PruningTransfer(source, target).Execute();
// The target holds what the source remembers, with its memorized times, and nothing forgotten
```

### With filters

```csharp
var options = new TransferOptions
{
    Filter = CompositeFilter.All(
        GraphFilter.Exclude("<http://temp.data>"),
        PredicateFilter.Exclude("<http://internal/debug>"))
};

var result = new PruningTransfer(source, target, options).Execute();
```

### Available filters

| Filter | Factory methods | Description |
|--------|----------------|-------------|
| `GraphFilter` | `Include(...)`, `Exclude(...)`, `DefaultGraphOnly()`, `NamedGraphsOnly()` | Filter by graph IRI |
| `PredicateFilter` | `Include(...)`, `Exclude(...)` | Filter by predicate IRI |
| `CompositeFilter` | `All(...)` (AND), `Any(...)` (OR) | Combine multiple filters |

A filter selects among the remembered memories; a forgotten one never reaches it.

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

Set `AuditLogPath` to write the remembered quads the filters excluded to a file. This lets you
recover them if needed:

```csharp
var options = new TransferOptions
{
    AuditLogPath = "/path/to/pruned-quads.nq"
};
```

The audit log is written in N-Quads format -- you can re-load it with
`mercury-sparql --load pruned-quads.nq` if you need the data back. Loaded again, the quads are memorized at the time
of the load. Forgotten memories are not in the audit log.

---

## Recipes

### Weekly maintenance

A simple maintenance routine for a production store:

```
cli> :stats
cli> :prune --dry-run
cli> :prune
cli> :stats
```

Compare the before and after stats to confirm the prune worked as expected.

### Remove temporary graphs

After importing data for analysis, clean up:

```
cli> :prune --exclude-graph <http://example.org/import-2025-09>
```

---

## See Also

- [Memory Time](temporal-rdf.md) -- memorizing, forgetting, and the words that ask about them
- [Mercury CLI](mercury-cli.md) -- REPL commands including `:prune`
- [Mercury MCP Server](mercury-mcp.md) -- the MCP tools
- [Federation and SERVICE](federation-and-service.md) -- querying across
  instances
