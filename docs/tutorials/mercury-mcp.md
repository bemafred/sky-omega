# Mercury MCP -- Persistent Memory for Claude

Mercury MCP gives Claude Code a persistent semantic memory that survives between
sessions. It exposes a Mercury triple store as five MCP tools, backed by a
durable on-disk store. What Claude writes in one session, a future session can
query. The store belongs to you, not to any platform.

> **New here?** Start with [Getting Started](getting-started.md) for installation,
> then [Mercury CLI](mercury-cli.md) for the REPL. This tutorial covers the MCP
> integration -- how Claude uses Mercury as memory.

---

## What Mercury MCP Is

Mercury MCP is a Model Context Protocol server that wraps a Mercury QuadStore.
Claude interacts with it through five tools:

| Tool | Description |
|------|-------------|
| `mercury_query` | Execute SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE queries |
| `mercury_update` | Execute SPARQL UPDATE (INSERT DATA, DELETE DATA, LOAD, CLEAR, etc.) |
| `mercury_stats` | Get store statistics -- quad count, atom count, storage size, WAL status |
| `mercury_graphs` | List all named graphs in the store |
| `mercury_prune` | Compact the store with options for history, graph exclusion, and dry-run |

The server also exposes:

- A SPARQL HTTP endpoint at `http://localhost:3030/sparql`
- A named pipe `mercury-mcp` for CLI attachment

---

## Configuring Claude Code

### Dev-time: `.mcp.json` (already in repo)

The repository includes an `.mcp.json` at the root that Claude Code picks up
automatically:

```json
{
  "mcpServers": {
    "mercury": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/Mercury.Mcp"]
    }
  }
}
```

When you open this repository in Claude Code, the MCP server starts
automatically. No manual setup required -- the five `mercury_*` tools appear
in the tool list.

### Global install (any repository)

To use Mercury MCP outside this repository, install the global tool and register
it with Claude:

```bash
# Install the tool (from repo root, after building)
./tools/install-tools.sh

# Register with Claude Code as a user-scoped MCP server
claude mcp add --transport stdio --scope user mercury -- mercury-mcp
```

After registration, the Mercury tools are available in every Claude Code session,
regardless of which repository you have open.

---

## Worked Example

This walkthrough stores a project decision as structured triples, queries it
back, and demonstrates persistence across sessions.

### Store a decision

Claude uses `mercury_update` to write triples into a named graph:

```sparql
PREFIX sky: <urn:sky-omega:>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

INSERT DATA {
  GRAPH <urn:sky-omega:session:2026-02-15-001> {
    <urn:sky-omega:session:2026-02-15-001> a sky:Session ;
      sky:agent "claude-code" ;
      sky:timestamp "2026-02-15T10:30:00Z"^^xsd:dateTime ;
      rdfs:comment "Decided on B+Tree page size after benchmarking" .

    <urn:sky-omega:data:decision-page-size> a sky:Decision ;
      rdfs:label "B+Tree page size: 4096 bytes" ;
      sky:about <urn:sky-omega:data:mercury> ;
      sky:rationale "4KB aligns with OS page size. Benchmarks showed 15% better throughput vs 8KB on SSD random reads." ;
      sky:status "established" ;
      sky:timestamp "2026-02-15T10:30:00Z"^^xsd:dateTime .
  }
}
```

### Query it back

In the same session or any future session, Claude uses `mercury_query`:

```sparql
PREFIX sky: <urn:sky-omega:>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?decision ?label ?rationale WHERE {
  ?decision a sky:Decision ;
    rdfs:label ?label ;
    sky:rationale ?rationale ;
    sky:about <urn:sky-omega:data:mercury> .
}
```

Result:

```
?decision                                  ?label                           ?rationale
<urn:sky-omega:data:decision-page-size>    "B+Tree page size: 4096 bytes"   "4KB aligns with OS page size. ..."

1 result(s)
```

### Persistence across sessions

Close the Claude Code session entirely. Open a new session -- even days later.
The `mercury_query` tool returns the same results. The store at
`~/Library/SkyOmega/stores/mcp/` (macOS) or `~/.local/share/SkyOmega/stores/mcp/`
(Linux) persists independently of the conversation.

This is the core value proposition: Claude can build up structured knowledge
over time, and that knowledge survives context window boundaries.

---

## Bootstrap: Three Paths

An empty MCP store is a valid starting point, but loading the bootstrap file
provides grounded context -- component definitions, EEE methodology, and
architectural principles as queryable triples.

The bootstrap file lives at `docs/knowledge/bootstrap.ttl` in the repository.

### Path 1: Claude Code auto-load

When Claude reads CLAUDE.md (which references MERCURY.md), it finds the
bootstrap instructions. Claude checks `mercury_stats` and, if the store is
empty, loads the bootstrap:

```sparql
LOAD <file:///Users/you/src/sky-omega/docs/knowledge/bootstrap.ttl>
```

Replace `/Users/you/src/sky-omega` with the actual path to your clone.

### Path 2: Manual via CLI attachment

Attach the CLI to the running MCP server and load directly:

```bash
mercury -a mcp
```

Then at the `mcp>` prompt:

```sparql
LOAD <file:///Users/you/src/sky-omega/docs/knowledge/bootstrap.ttl>
```

This loads the file into the same store that Claude reads and writes.

### Path 3: Standalone CLI with MCP store path

Point the CLI directly at the MCP store directory:

```bash
mercury ~/Library/SkyOmega/stores/mcp/
```

Then load as above. Note: the MCP server must not be running when you open
the store directly, since Mercury uses exclusive file locks. Use `mercury -a mcp`
(Path 2) if the MCP server is already running.

### Verify the bootstrap loaded

After loading, query the system components:

```sparql
PREFIX sky: <urn:sky-omega:>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?component ?label ?role WHERE {
  ?component sky:partOf <urn:sky-omega:data:sky-omega> ;
    rdfs:label ?label ;
    sky:role ?role .
}
```

You should see Mercury, Lucy, James, Mira, and Minerva with their roles.

---

## The HTTP Endpoint (3030)

The MCP server exposes a SPARQL HTTP endpoint at `http://localhost:3030/sparql`.
This runs alongside the MCP protocol on stdin/stdout -- they share the same
underlying store.

### Query from the command line

```bash
curl -G http://localhost:3030/sparql \
  --data-urlencode "query=SELECT ?g (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } } GROUP BY ?g"
```

### Content negotiation

```bash
# JSON (default)
curl -G http://localhost:3030/sparql \
  -H "Accept: application/sparql-results+json" \
  --data-urlencode "query=SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"

# CSV
curl -G http://localhost:3030/sparql \
  -H "Accept: text/csv" \
  --data-urlencode "query=SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"
```

### SPARQL Update via HTTP

By default, the MCP server does not accept updates over HTTP (only via the MCP
tools). To enable HTTP updates, start with the `--enable-http-updates` flag:

```bash
mercury-mcp --enable-http-updates
```

Then POST to the update endpoint:

```bash
curl -X POST http://localhost:3030/sparql/update \
  -H "Content-Type: application/sparql-update" \
  -d 'INSERT DATA { <http://example.org/x> <http://example.org/p> "hello" }'
```

### Federated queries from the CLI

When both the CLI and MCP server are running, you can query across both stores
from the CLI using SERVICE:

```bash
mercury
```

```sparql
SELECT ?s ?p ?o WHERE {
  SERVICE <http://localhost:3030/sparql> {
    ?s ?p ?o
  }
} LIMIT 10
```

---

## CLI Attachment (`mercury -a mcp`)

The `--attach` flag connects the Mercury CLI to the running MCP server via
the named pipe `mercury-mcp`. You get a full REPL against the same store that
Claude uses.

```bash
mercury -a mcp
```

```
Attaching to mcp via pipe 'mercury-mcp'...

mcp> :stats
Store Statistics:
  Quads:           2,456
  Atoms:           891
  ...

mcp> :graphs
Named graphs (3):
  <urn:sky-omega:session:2026-02-15-001>
  <urn:sky-omega:session:2026-02-14-001>
  <urn:sky-omega:data:bootstrap>

mcp> SELECT ?s ?label WHERE {
  ->   ?s <http://www.w3.org/2000/01/rdf-schema#label> ?label .
  -> } LIMIT 5
```

This is useful for:

- **Inspecting** what Claude has stored in the MCP server
- **Debugging** queries that return unexpected results
- **Loading** data files into the MCP store while Claude is running
- **Ad-hoc exploration** without going through the MCP tool interface

The MCP server must be running before you attach. If it is not running, you
will see: `Error: Could not connect to mcp. Is it running?`

---

## Pruning via Claude

Over time, the store accumulates session graphs, provisional assertions, and
soft-deleted data. The `mercury_prune` tool compacts the store using a
copy-and-switch pattern.

### Dry run first

Always preview before pruning:

```
mercury_prune(dryRun: true)
```

Output:

```
Prune dry-run complete:
  Quads scanned: 5,678
  Quads written: 5,200
  Duration: 120ms
```

### Basic prune

Remove soft-deleted data, flatten history to current versions:

```
mercury_prune()
```

### Preserve history

Keep version history but still remove soft deletes:

```
mercury_prune(historyMode: "preserve")
```

### Exclude graphs

Remove temporary or scratch graphs during pruning:

```
mercury_prune(excludeGraphs: "<urn:sky-omega:session:scratch>,<urn:sky-omega:data:temp>")
```

### Exclude predicates

Filter out debug predicates:

```
mercury_prune(excludePredicates: "<http://example.org/debug>,<http://example.org/internal>")
```

### Combined options

```
mercury_prune(
  dryRun: true,
  historyMode: "preserve",
  excludeGraphs: "<urn:sky-omega:data:temp>",
  excludePredicates: "<http://example.org/debug>"
)
```

### History modes

| Mode | `historyMode` value | Behavior |
|------|---------------------|----------|
| Flatten to current | `"flatten"` (default) | Keep only the latest version of each triple |
| Preserve versions | `"preserve"` | Keep version history, remove soft deletes |
| Preserve all | `"all"` | Keep everything including soft deletes |

---

## Store Locations

| Platform | MCP store path |
|----------|----------------|
| macOS | `~/Library/SkyOmega/stores/mcp/` |
| Linux | `~/.local/share/SkyOmega/stores/mcp/` |
| Windows | `%LOCALAPPDATA%\SkyOmega\stores\mcp\` |

The store directory is created automatically on first run. It contains
memory-mapped B+Tree indexes, an atom store, and a write-ahead log.

To start fresh, stop the MCP server and delete the store directory:

```bash
rm -rf ~/Library/SkyOmega/stores/mcp/
```

---

## See Also

- [Getting Started](getting-started.md) -- first-time setup and installation
- [Mercury CLI](mercury-cli.md) -- full CLI reference, REPL commands, store management
- [MERCURY.md](../../MERCURY.md) -- semantic memory discipline, EEE patterns, provenance conventions
- [CLAUDE.md](../../CLAUDE.md) -- build commands, architecture overview, MCP configuration
