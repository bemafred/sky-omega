# Mercury SPARQL CLI

The batch/scripting tool for non-interactive SPARQL workflows. Load RDF data,
run queries, convert formats, and inspect execution plans -- all from a single
command line invocation.

> **New here?** Start with [Getting Started](getting-started.md) for installation
> and the interactive REPL. This tutorial covers `mercury-sparql`, the stateless
> batch tool designed for scripting and one-shot queries.

---

## Quick Examples

Load a Turtle file and run a query (temporary store, auto-deleted):

```bash
mercury-sparql --load data.ttl --query "SELECT * WHERE { ?s ?p ?o } LIMIT 10"
```

Export SELECT results as CSV:

```bash
mercury-sparql --load data.ttl \
  -q "SELECT ?s ?p ?o WHERE { ?s ?p ?o }" --format csv
```

Show a query execution plan:

```bash
mercury-sparql --explain "SELECT * WHERE { ?s <http://ex.org/knows> ?o }"
```

---

## Command Reference

| Flag | Long form | Argument | Description |
|------|-----------|----------|-------------|
| `-l` | `--load` | `FILE` | Load an RDF file into the store |
| `-q` | `--query` | `SPARQL` | Execute a SPARQL query |
| `-f` | `--query-file` | `FILE` | Execute a SPARQL query read from a file |
| `-s` | `--store` | `PATH` | Use a persistent store at the given directory |
| `-e` | `--explain` | `SPARQL` | Show the query execution plan |
| `-r` | `--repl` | | Start interactive REPL mode |
| | `--format` | `FORMAT` | SELECT output format: `json` (default), `csv`, `tsv`, `xml` |
| | `--rdf-format` | `FORMAT` | CONSTRUCT/DESCRIBE output format: `nt` (default), `ttl`, `rdf`, `nq`, `trig` |
| `-v` | `--version` | | Show version information |
| `-h` | `--help` | | Show help message |

---

## Store Modes

### Temporary store (default)

When you run `mercury-sparql` without `--store`, it creates a temporary store
in the system temp directory. The store is automatically deleted when the
command finishes. This is the right choice for one-shot queries and pipelines
where you don't need the data again.

```bash
mercury-sparql --load data.ttl --query "SELECT ?name WHERE { ?s foaf:name ?name }"
```

### Persistent store

Use `--store` to keep the data between invocations. The directory is created
if it doesn't exist:

```bash
# First run: load data
mercury-sparql --store ./mydb --load data.ttl

# Later runs: query the same data
mercury-sparql --store ./mydb --query "SELECT * WHERE { ?s ?p ?o } LIMIT 5"
```

This is useful when loading is expensive and you want to run multiple queries
against the same dataset.

---

## Loading RDF Files

The `--load` flag accepts any RDF file. The format is detected from the file
extension:

| Extension | Format |
|-----------|--------|
| `.ttl`, `.turtle` | Turtle |
| `.nt`, `.ntriples` | N-Triples |
| `.rdf`, `.xml` | RDF/XML |
| `.nq`, `.nquads` | N-Quads |
| `.trig` | TriG |
| `.jsonld` | JSON-LD |

Example with N-Quads (preserves named graphs):

```bash
mercury-sparql --load dataset.nq --query "SELECT ?g WHERE { GRAPH ?g { ?s ?p ?o } }"
```

---

## Query Execution

### SELECT

SELECT queries return variable bindings. Use `--format` to control the output
format:

```bash
# JSON (default)
mercury-sparql --load data.ttl \
  -q "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"

# CSV -- easy to pipe into other tools
mercury-sparql --load data.ttl \
  -q "SELECT ?s ?p ?o WHERE { ?s ?p ?o }" --format csv

# TSV -- preserves RDF syntax (brackets, quotes)
mercury-sparql --load data.ttl \
  -q "SELECT ?s ?p ?o WHERE { ?s ?p ?o }" --format tsv

# XML -- W3C SPARQL Results XML format
mercury-sparql --load data.ttl \
  -q "SELECT ?s ?p ?o WHERE { ?s ?p ?o }" --format xml
```

### ASK

ASK queries return `true` or `false`:

```bash
mercury-sparql --load data.ttl \
  -q "ASK { <http://example.org/alice> <http://xmlns.com/foaf/0.1/knows> ?someone }"
```

```
true
```

### CONSTRUCT and DESCRIBE

CONSTRUCT and DESCRIBE produce RDF output. Use `--rdf-format` to choose the
serialization:

```bash
# N-Triples (default)
mercury-sparql --load data.ttl \
  -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format nt

# Turtle
mercury-sparql --load data.ttl \
  -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format ttl

# RDF/XML
mercury-sparql --load data.ttl \
  -q "DESCRIBE <http://example.org/alice>" --rdf-format rdf
```

| Value | Format |
|-------|--------|
| `nt`, `ntriples` | N-Triples |
| `ttl`, `turtle` | Turtle |
| `rdf`, `rdfxml`, `xml` | RDF/XML |
| `nq`, `nquads` | N-Quads |
| `trig` | TriG |

---

## Query Plans (EXPLAIN)

The `--explain` flag shows the execution plan without running the query. This
is useful for understanding how Mercury will evaluate a complex query:

```bash
mercury-sparql --explain "SELECT ?x ?y WHERE { ?x <http://ex.org/knows> ?y . ?y <http://ex.org/name> ?n }"
```

The plan uses operator symbols from the SPARQL execution engine:

| Symbol | Operator | Description |
|--------|----------|-------------|
| ⊳ | TriplePatternScan | Scan index for a triple pattern |
| ⋈ | NestedLoopJoin | Join two patterns |
| ⟕ | LeftOuterJoin | OPTIONAL pattern |
| ∪ | Union | UNION alternatives |
| σ | Filter | FILTER expression |
| γ | GroupBy | GROUP BY with aggregation |
| ↑ | Sort | ORDER BY |
| ⌊ | Slice | LIMIT/OFFSET |
| π | Project | SELECT projection |

---

## Interactive REPL

Use `--repl` to start an interactive session. This is similar to the `mercury`
REPL but without the persistent store, HTTP endpoint, or pre-registered
prefixes:

```bash
mercury-sparql --store ./mydb --repl
```

```
Mercury SPARQL REPL
Type SPARQL queries (end with ';' to execute), or use dot commands.
Type .help for available commands, .quit to exit.

sparql>
```

Queries span multiple lines. End with a semicolon to execute:

```
sparql> SELECT ?s ?p ?o
     -> WHERE { ?s ?p ?o }
     -> LIMIT 5;
```

### Dot commands

| Command | Alias | Description |
|---------|-------|-------------|
| `.help` | `.h` | Show available commands |
| `.quit` | `.exit`, `.q` | Exit REPL |
| `.load <file>` | `.l` | Load an RDF file into the store |
| `.format [fmt]` | `.f` | Get or set SELECT output format (`json`, `csv`, `tsv`, `xml`) |
| `.rdf-format [fmt]` | `.rf` | Get or set CONSTRUCT output format (`nt`, `ttl`, `rdf`, `nq`, `trig`) |
| `.count` | `.c` | Count triples in the store |
| `.store` | `.s` | Show the store path |
| `.explain <query>` | `.e` | Show a query execution plan |

---

## Scripting Patterns

### Pipe output to other tools

```bash
mercury-sparql --load data.ttl \
  -q "SELECT ?name WHERE { ?s foaf:name ?name }" --format csv \
  | tail -n +2 | sort
```

### Read a query from a file

Write your query in a `.rq` file:

```bash
mercury-sparql --store ./mydb --query-file complex-query.rq --format json
```

### Format conversion (RDF to RDF)

Convert Turtle to N-Triples by constructing everything:

```bash
mercury-sparql --load data.ttl \
  -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format nt > data.nt
```

### Persistent store for repeated queries

Load once, query many times:

```bash
# Load data into persistent store
mercury-sparql --store ./project-db --load large-dataset.ttl

# Run multiple queries without re-loading
mercury-sparql --store ./project-db -q "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }"
mercury-sparql --store ./project-db -q "SELECT DISTINCT ?type WHERE { ?s a ?type }"
mercury-sparql --store ./project-db -q "SELECT ?p (COUNT(*) AS ?c) WHERE { ?s ?p ?o } GROUP BY ?p ORDER BY DESC(?c)"
```

### Inline query as positional argument

The query can also be passed as a bare positional argument:

```bash
mercury-sparql --load data.ttl "SELECT * WHERE { ?s ?p ?o } LIMIT 5"
```

---

## See Also

- [Getting Started](getting-started.md) -- first-time setup and the interactive REPL
- [Mercury CLI](mercury-cli.md) -- the persistent interactive REPL with HTTP endpoint
- [Mercury Turtle CLI](mercury-turtle-cli.md) -- validation, conversion, benchmarking
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF for newcomers
- [Installation and Tools](installation-and-tools.md) -- full tool lifecycle
