# Mercury CLI

A deep-dive reference for the Mercury SPARQL REPL. Covers every startup mode,
REPL command, data loading pattern, HTTP endpoint, MCP attachment, store
management, and pruning workflow.

> **New here?** Start with [Getting Started](getting-started.md) for a
> 30-minute walkthrough from clone to first query.

---

## Starting Modes

Mercury has three starting modes that control where data is stored.

### Persistent store (default)

```bash
mercury
```

Opens (or creates) the default persistent store. Data survives between sessions.
The store location is platform-dependent -- see [Store Management](#store-management)
for details.

### In-memory store

```bash
mercury -m
```

Creates a temporary store that is deleted when you exit the REPL. Useful for
experiments, demos, and throwaway exploration.

### Custom store path

```bash
mercury ./mydata
```

Opens a store at the given directory. If the directory does not exist, Mercury
creates it. You can also use the long-form flag:

```bash
mercury -d /path/to/store
```

### Full CLI reference

```
Usage: mercury [options] [store-path]

Options:
  -v, --version            Show version information
  -h, --help               Show this help message
  -m, --memory             Use temporary in-memory store (deleted on exit)
  -d, --data <path>        Path to data directory
  -p, --port <port>        HTTP port (default: 3031)
  --no-http                Disable HTTP endpoint
  -a, --attach [target]    Attach to running instance (default: mcp)
```

---

## REPL Commands

All REPL commands start with a colon. Type `:help` at any time for a quick
reminder.

| Command | Aliases | Description |
|---------|---------|-------------|
| `:help` | `:h`, `:?` | Show the built-in help text |
| `:prefixes` | `:p` | List all registered prefixes |
| `:stats` | `:s` | Show store statistics (quads, atoms, WAL state) |
| `:clear` | | Clear query history |
| `:reset` | | Reset session -- restores default prefixes, clears history |
| `:history` | | Show numbered query history |
| `:graphs` | | List all named graphs in the store |
| `:count [pattern]` | | Count triples, optionally matching a pattern |
| `:prune [options]` | | Compact the store (see [Pruning](#pruning)) |
| `:quit` | `:q`, `:exit` | Exit the REPL |

### PREFIX and BASE declarations

PREFIX declarations are sticky -- once registered, they apply to every query
for the rest of the session:

```
mercury> PREFIX proj: <http://example.org/project/>
Prefix 'proj:' registered as <http://example.org/project/>

mercury> SELECT ?name WHERE { proj:alpha rdfs:label ?name }
```

BASE works the same way:

```
mercury> BASE <http://example.org/>
Base IRI set to <http://example.org/>
```

To restore only the default prefixes, use `:reset`.

### Pre-registered prefixes

The following prefixes are available without any PREFIX declaration:

| Prefix | Namespace |
|--------|-----------|
| `rdf` | `http://www.w3.org/1999/02/22-rdf-syntax-ns#` |
| `rdfs` | `http://www.w3.org/2000/01/rdf-schema#` |
| `xsd` | `http://www.w3.org/2001/XMLSchema#` |
| `owl` | `http://www.w3.org/2002/07/owl#` |
| `foaf` | `http://xmlns.com/foaf/0.1/` |
| `dc` | `http://purl.org/dc/elements/1.1/` |
| `dcterms` | `http://purl.org/dc/terms/` |
| `skos` | `http://www.w3.org/2004/02/skos/core#` |
| `schema` | `http://schema.org/` |
| `ex` | `http://example.org/` |

### Multi-line input

The REPL detects unclosed braces and switches to continuation mode
automatically. You do not need any special syntax:

```
mercury> INSERT DATA {
      ->   ex:alice foaf:name "Alice" .
      ->   ex:alice foaf:knows ex:bob .
      -> }
```

An empty line or a line ending with `;` also terminates multi-line input.

### The :count command

Without arguments, `:count` returns the total number of triples:

```
mercury> :count
Count: 42
```

With a pattern argument, it counts matching triples:

```
mercury> :count ?s foaf:knows ?o
Count: 7
```

### The :stats command

```
mercury> :stats
Store Statistics:
  Quads:           1,234
  Atoms:           567
  Storage:         12.3 MB

Write-Ahead Log:
  Current TxId:    89
  Last Checkpoint: 85
  Log Size:        128 KB

Session:
  Prefixes:        12
  History:         5 queries
```

---

## Loading Data

Mercury supports loading RDF data from local files and remote URLs using the
SPARQL `LOAD` command.

### Loading from a local file

File URIs must be absolute paths:

```
mercury> LOAD <file:///Users/you/data/people.ttl>
```

Mercury detects the RDF format from the file extension. Supported extensions
include `.ttl` (Turtle), `.nt` (N-Triples), `.nq` (N-Quads), `.trig` (TriG),
`.rdf` (RDF/XML), and `.jsonld` (JSON-LD).

### Loading from a remote URL

```
mercury> LOAD <http://example.org/data/vocabulary.ttl>
```

Remote loading uses content negotiation to determine the format.

### Loading into a named graph

Use the `INTO GRAPH` clause to load data into a specific named graph:

```
mercury> LOAD <file:///Users/you/data/people.ttl> INTO GRAPH <http://example.org/people>
```

### Verifying loaded data

After loading, check the triple count and run a quick query:

```
mercury> :count
Count: 1,234

mercury> SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5
```

If you loaded into a named graph, list the graphs first:

```
mercury> :graphs
Named graphs (1):
  <http://example.org/people>

mercury> SELECT ?s ?p ?o WHERE {
      ->   GRAPH <http://example.org/people> { ?s ?p ?o }
      -> } LIMIT 5
```

---

## Query Workflows

### SELECT queries

SELECT is the most common query type. It returns a table of variable bindings:

```
mercury> SELECT ?person ?name WHERE {
      ->   ?person foaf:name ?name .
      -> } ORDER BY ?name LIMIT 10
| person                     | name    |
|----------------------------|---------|
| <http://example.org/alice> | "Alice" |
| <http://example.org/bob>   | "Bob"   |

2 results (parse: 0.1ms, exec: 1.2ms)
```

**Aggregation:**

```
mercury> SELECT ?type (COUNT(?s) AS ?count) WHERE {
      ->   ?s rdf:type ?type .
      -> } GROUP BY ?type ORDER BY DESC(?count)
```

**OPTIONAL and FILTER:**

```
mercury> SELECT ?person ?name ?email WHERE {
      ->   ?person foaf:name ?name .
      ->   OPTIONAL { ?person foaf:mbox ?email }
      ->   FILTER(CONTAINS(?name, "Al"))
      -> }
```

### ASK queries

ASK returns a boolean -- does any matching data exist?

```
mercury> ASK { ex:alice foaf:knows ex:bob }
true (parse: 0.1ms, exec: 0.3ms)
```

### CONSTRUCT queries

CONSTRUCT builds an RDF graph from query results:

```
mercury> CONSTRUCT {
      ->   ?person schema:name ?name .
      -> } WHERE {
      ->   ?person foaf:name ?name .
      -> }
<http://example.org/alice> <http://schema.org/name> "Alice" .
<http://example.org/bob> <http://schema.org/name> "Bob" .

2 triples (parse: 0.1ms, exec: 0.8ms)
```

### DESCRIBE queries

DESCRIBE returns all known triples about a resource:

```
mercury> DESCRIBE ex:alice
```

### Cross-graph queries

Query across multiple named graphs:

```
mercury> SELECT ?g ?s ?p ?o WHERE {
      ->   GRAPH ?g { ?s ?p ?o }
      -> } LIMIT 10
```

### Temporal queries

Mercury supports temporal SPARQL extensions for querying historical data:

```
mercury> SELECT ?name WHERE {
      ->   ex:alice foaf:name ?name .
      -> } AS OF "2025-06-01"^^xsd:date

mercury> SELECT ?name WHERE {
      ->   ex:alice foaf:name ?name .
      -> } ALL VERSIONS
```

### Federated queries (SERVICE)

If the MCP server is running, you can query it from the CLI using SERVICE:

```
mercury> SELECT ?s ?p ?o WHERE {
      ->   SERVICE <http://localhost:3030/sparql> {
      ->     ?s ?p ?o
      ->   }
      -> } LIMIT 5
```

Mercury automatically detects a running MCP instance and prints a hint at
startup.

---

## The HTTP Endpoint

When Mercury starts, it exposes a SPARQL HTTP endpoint at
`http://localhost:3031/sparql` (unless you pass `--no-http`).

### Query via GET

```bash
curl -G http://localhost:3031/sparql \
  --data-urlencode "query=SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"
```

### Query via POST (form-encoded)

```bash
curl -X POST http://localhost:3031/sparql \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "query=SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"
```

### Query via POST (direct body)

```bash
curl -X POST http://localhost:3031/sparql \
  -H "Content-Type: application/sparql-query" \
  -d "SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"
```

### Content negotiation

Request a specific result format via the Accept header:

```bash
# JSON (default)
curl -G http://localhost:3031/sparql \
  -H "Accept: application/sparql-results+json" \
  --data-urlencode "query=SELECT ?s WHERE { ?s ?p ?o } LIMIT 3"

# XML
curl -G http://localhost:3031/sparql \
  -H "Accept: application/sparql-results+xml" \
  --data-urlencode "query=SELECT ?s WHERE { ?s ?p ?o } LIMIT 3"

# CSV
curl -G http://localhost:3031/sparql \
  -H "Accept: text/csv" \
  --data-urlencode "query=SELECT ?s WHERE { ?s ?p ?o } LIMIT 3"

# TSV
curl -G http://localhost:3031/sparql \
  -H "Accept: text/tab-separated-values" \
  --data-urlencode "query=SELECT ?s WHERE { ?s ?p ?o } LIMIT 3"
```

### SPARQL Update via HTTP

The CLI endpoint supports updates on the `/sparql/update` path:

```bash
curl -X POST http://localhost:3031/sparql/update \
  -H "Content-Type: application/sparql-update" \
  -d 'INSERT DATA { <http://example.org/x> <http://example.org/p> "hello" }'
```

### Service description

A GET to `/sparql` with no query parameter returns a Turtle service description:

```bash
curl http://localhost:3031/sparql
```

### Changing the port

Use `-p` to listen on a different port:

```bash
mercury -p 8080
```

The endpoint will be at `http://localhost:8080/sparql`.

### Disabling HTTP

If you do not need the HTTP endpoint (for example, to avoid port conflicts),
pass `--no-http`:

```bash
mercury --no-http
```

---

## Attaching to MCP

The `--attach` flag connects the CLI REPL to a running Mercury instance via a
named pipe. This lets you inspect and query the store that Claude (or another
MCP client) is using.

### Attach to the MCP server

```bash
mercury -a mcp
```

This connects to the named pipe `mercury-mcp`. You get a full REPL session
against the MCP store -- the same data that Claude reads and writes.

The short form `mercury -a` defaults to `mcp`:

```bash
mercury -a
```

### Attach to another CLI instance

```bash
mercury -a cli
```

This connects to the named pipe `mercury-cli`.

### Custom pipe name

You can pass any pipe name directly:

```bash
mercury -a my-custom-pipe
```

### What you can do in attach mode

Attach mode gives you a full REPL session against the remote store. You can run
queries, inspect statistics, list graphs, and even execute updates. This is
particularly useful for:

- Inspecting what Claude has stored via the MCP server
- Debugging semantic memory issues
- Running ad-hoc queries against MCP data without restarting Claude

```bash
$ mercury -a mcp
Attaching to mcp via pipe 'mercury-mcp'...

mcp> :stats
Store Statistics:
  Quads:           2,456
  Atoms:           891
  ...

mcp> SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5
```

> **Note:** The MCP server must be running before you attach. If it is not
> running, you will see: `Error: Could not connect to mcp. Is it running?`

For details on setting up the MCP server, see
[Mercury MCP Server](mercury-mcp.md).

---

## Store Management

### Store locations

Mercury uses `MercuryPaths.Store(name)` to resolve the platform-specific store
directory. The directory is created automatically on first use.

| Platform | CLI store path | MCP store path |
|----------|---------------|----------------|
| macOS | `~/Library/SkyOmega/stores/cli/` | `~/Library/SkyOmega/stores/mcp/` |
| Linux | `~/.local/share/SkyOmega/stores/cli/` | `~/.local/share/SkyOmega/stores/mcp/` |
| Windows | `%LOCALAPPDATA%\SkyOmega\stores\cli\` | `%LOCALAPPDATA%\SkyOmega\stores\mcp\` |

### What is inside a store directory

A store directory contains several memory-mapped files:

- **Index files** (GSPO, GPOS, GOSP, TGSPO) -- B+Tree indexes for quad lookup
- **Atom store** -- string interning with hash index
- **WAL** -- write-ahead log for crash recovery
- **Trigram index** (optional) -- full-text search support

You should not modify these files directly.

### Backup

To back up a store, copy the entire directory while Mercury is not running:

```bash
# Stop mercury first, then:
cp -r ~/Library/SkyOmega/stores/cli/ ~/backups/cli-backup-$(date +%Y%m%d)/
```

### Fresh start

To start with a clean store, delete the store directory:

```bash
rm -rf ~/Library/SkyOmega/stores/cli/
```

The next time you run `mercury`, a fresh store is created automatically.

### Using a custom location

For project-specific data, point Mercury at a directory inside your project:

```bash
mercury ./data/knowledge-graph
```

This is useful when you want the store to live alongside your code and be
version-controlled (though the binary store files are typically gitignored).

---

## Pruning

Pruning compacts the store by physically removing soft-deleted data and
optionally flattening history. It uses a copy-and-switch pattern: data is
transferred from the primary store to a fresh secondary store, then the stores
are swapped.

### Basic prune

```
mercury> :prune
Prune complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Space saved:   45.2 MB
  Duration:      320ms
```

### Dry run

Preview what a prune would do without writing anything:

```
mercury> :prune --dry-run
Prune dry-run complete:
  Quads scanned: 1,234
  Quads written: 1,100
  Duration:      280ms
```

### History modes

By default, pruning flattens history to keep only the current version of each
triple. Use `--history` to control this:

```
mercury> :prune --history preserve
```

| Mode | Flag value | Behavior |
|------|-----------|----------|
| Flatten to current | *(default)* | Keep only the latest version of each triple |
| Preserve versions | `preserve` | Keep version history but remove soft deletes |
| Preserve all | `all` | Keep everything including soft deletes |

### Excluding graphs

Exclude specific named graphs from the pruned store:

```
mercury> :prune --exclude-graph <http://example.org/temp>
```

Multiple exclusions are supported:

```
mercury> :prune --exclude-graph <http://example.org/temp> --exclude-graph <http://example.org/scratch>
```

### Excluding predicates

Exclude triples with specific predicates:

```
mercury> :prune --exclude-predicate <http://example.org/debug>
```

### Combining options

Options can be combined freely:

```
mercury> :prune --dry-run --history preserve --exclude-graph <http://example.org/temp> --exclude-predicate <http://example.org/debug>
```

---

## Tips and Recipes

### Explore an unknown dataset

```
mercury> :count
mercury> :graphs
mercury> SELECT DISTINCT ?type WHERE { ?s rdf:type ?type }
mercury> SELECT ?p (COUNT(?s) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?p ORDER BY DESC(?count)
```

### Export data as Turtle

Use CONSTRUCT to extract triples, then pipe through the HTTP endpoint:

```bash
curl -G http://localhost:3031/sparql \
  -H "Accept: text/turtle" \
  --data-urlencode "query=CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" \
  > export.ttl
```

### Query both CLI and MCP stores

Run the CLI and use SERVICE to federate across both stores:

```
mercury> SELECT ?s ?p ?o WHERE {
      ->   SERVICE <http://localhost:3030/sparql> {
      ->     ?s ?p ?o
      ->   }
      -> } LIMIT 10
```

### Non-interactive use (piped input)

Mercury detects piped input and runs without prompts or color:

```bash
echo 'SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5' | mercury
```

---

## See Also

- [Getting Started](getting-started.md) -- first-time setup and installation
- [Mercury MCP Server](mercury-mcp.md) -- using Mercury as a Claude MCP tool
