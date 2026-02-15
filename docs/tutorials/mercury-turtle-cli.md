# Mercury Turtle CLI

Validate Turtle syntax, convert between RDF formats, measure parser throughput,
and load data into persistent stores -- all from the command line.

> **New here?** Start with [Getting Started](getting-started.md) for installation.
> This tutorial covers `mercury-turtle`, the validation and conversion tool.

---

## Quick Examples

Validate a Turtle file:

```bash
mercury-turtle --validate input.ttl
```

Convert Turtle to N-Triples:

```bash
mercury-turtle --input data.ttl --output data.nt
```

Show file statistics:

```bash
mercury-turtle --stats data.ttl
```

Run a parser benchmark with generated data:

```bash
mercury-turtle --benchmark --count 100000
```

---

## Command Reference

| Flag | Long form | Argument | Description |
|------|-----------|----------|-------------|
| `-i` | `--input` | `FILE` | Input Turtle file |
| `-o` | `--output` | `FILE` | Output file (format detected from extension) |
| | `--output-format` | `FORMAT` | Output format: `nt`, `nq`, `trig`, `ttl` |
| | `--validate` | | Validate syntax only (report errors) |
| | `--stats` | | Show statistics (triple count, predicates) |
| `-b` | `--benchmark` | | Run performance benchmark |
| `-n` | `--count` | `N` | Number of triples for generated benchmark data (default: 10000) |
| `-s` | `--store` | `PATH` | Load into a persistent QuadStore |
| `-v` | `--version` | | Show version information |
| `-h` | `--help` | | Show help message |

The input file can also be passed as a positional argument:

```bash
mercury-turtle data.ttl --validate
```

Running `mercury-turtle` with no arguments prints a demo with inline examples.

---

## Validation

Use `--validate` to check Turtle syntax without producing output. Exit code 0
means valid; exit code 1 means a syntax error was found:

```bash
mercury-turtle --validate input.ttl
```

```
Validating: input.ttl
Valid Turtle: 1,234 triples
```

If the file contains errors:

```
Validating: broken.ttl
Syntax error: Expected '.' or ';' at line 15
```

### CI integration

The exit code makes `mercury-turtle` suitable for CI pipelines:

```bash
# Fail the build if any Turtle file is invalid
for f in data/*.ttl; do
  mercury-turtle --validate "$f" || exit 1
done
```

---

## Format Conversion

Convert Turtle to another RDF format by specifying an output file or an
explicit output format.

### Auto-detection from extension

The output format is detected from the file extension:

```bash
mercury-turtle --input data.ttl --output data.nt       # N-Triples
mercury-turtle --input data.ttl --output data.nq       # N-Quads
mercury-turtle --input data.ttl --output data.trig     # TriG
```

### Explicit format override

Use `--output-format` when writing to stdout or when the extension doesn't
match the desired format:

```bash
mercury-turtle data.ttl --output-format nt > data.nt
```

### Output formats

| Value | Format |
|-------|--------|
| `nt`, `ntriples` | N-Triples |
| `nq`, `nquads` | N-Quads |
| `trig` | TriG |
| `ttl`, `turtle` | Turtle |

### Conversion matrix

Turtle input can be converted to any of the supported output formats:

| Input | Output | Command |
|-------|--------|---------|
| Turtle | N-Triples | `mercury-turtle data.ttl -o data.nt` |
| Turtle | N-Quads | `mercury-turtle data.ttl -o data.nq` |
| Turtle | TriG | `mercury-turtle data.ttl -o data.trig` |
| Turtle | Turtle | `mercury-turtle data.ttl -o normalized.ttl` |

For converting from other formats (N-Triples, RDF/XML, N-Quads, TriG, JSON-LD),
use `mercury-sparql` with CONSTRUCT:

```bash
mercury-sparql --load data.rdf \
  -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format ttl > data.ttl
```

---

## Statistics

Use `--stats` to analyze a Turtle file without loading it into a store:

```bash
mercury-turtle --stats data.ttl
```

```
Analyzing: data.ttl

Total triples:    1,234
Unique subjects:  456
Unique objects:   789
Unique predicates: 12

Predicate distribution:
       423 ( 34.3%) <http://www.w3.org/1999/02/22-rdf-syntax-ns#type>
       312 ( 25.3%) <http://xmlns.com/foaf/0.1/name>
       198 ( 16.0%) <http://xmlns.com/foaf/0.1/knows>
       ...
```

The predicate distribution shows the top 10 predicates by frequency, sorted
from most to least common. This is useful for understanding the shape of a
dataset before writing queries.

---

## Benchmarking

### Generated data

Run a benchmark with generated Turtle data:

```bash
mercury-turtle --benchmark --count 100000
```

```
=== Mercury Turtle Parser Benchmark ===

Source: generated (100,000 triples)
Size: 3,456 KB

Results:
  Triples:     100,000
  Time:        45 ms
  Throughput:  2,222,222 triples/sec
  Throughput:  76,800.00 KB/sec

GC Collections:
  Gen 0:       0
  Gen 1:       0
  Gen 2:       0
  Memory:      +12.50 KB

Zero GC collections during parse!
```

### Real files

Benchmark your own Turtle files:

```bash
mercury-turtle --benchmark --input large-dataset.ttl
```

The benchmark reports throughput (triples/sec, KB/sec), GC collection counts
across all generations, and memory delta. Zero GC collections confirms the
parser's zero-allocation design.

---

## Loading into a Store

Use `--store` to parse a Turtle file and load it directly into a persistent
QuadStore:

```bash
mercury-turtle --input data.ttl --store ./mydb
```

```
Loading: data.ttl
Store: ./mydb
  Loaded 100,000 triples...
  Loaded 200,000 triples...

Loaded 234,567 triples in 2,345 ms
Throughput: 100,028 triples/sec
Store ready at: ./mydb
```

Progress is reported every 100,000 triples. The store can then be queried with
`mercury-sparql`:

```bash
mercury-sparql --store ./mydb -q "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }"
```

---

## Scripting Patterns

### CI validation

Validate all Turtle files in a directory:

```bash
#!/usr/bin/env bash
set -e
for f in data/*.ttl; do
  mercury-turtle --validate "$f"
done
echo "All files valid."
```

### Batch conversion

Convert all Turtle files to N-Triples:

```bash
for f in data/*.ttl; do
  mercury-turtle "$f" --output-format nt > "${f%.ttl}.nt"
done
```

### Performance regression testing

Track parse throughput across builds:

```bash
mercury-turtle --benchmark --input test-fixture.ttl 2>&1 | grep "Throughput:"
```

---

## See Also

- [Getting Started](getting-started.md) -- first-time setup and installation
- [Mercury CLI](mercury-cli.md) -- the interactive REPL with persistent store
- [Mercury SPARQL CLI](mercury-sparql-cli.md) -- batch queries and format conversion
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF for newcomers
- [Installation and Tools](installation-and-tools.md) -- full tool lifecycle
