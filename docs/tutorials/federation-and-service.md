# Federation and SERVICE

Mercury supports querying across multiple instances using SPARQL SERVICE
clauses. The CLI and MCP server run as separate instances with separate stores,
and you can query one from the other -- or connect directly via named pipes.

> **Prerequisites:** Mercury tools installed with at least the CLI working.
> See [Getting Started](getting-started.md) for setup. Familiarity with
> SPARQL basics from [Your First Knowledge Graph](your-first-knowledge-graph.md)
> is helpful.

---

## The Architecture

Mercury can run multiple instances simultaneously, each with its own store:

| Instance | Default port | Store path | Pipe name |
|----------|-------------|------------|-----------|
| `mercury` (CLI) | 3031 | `~/Library/SkyOmega/stores/cli/` | `mercury-cli` |
| `mercury-mcp` (MCP) | 3030 | `~/Library/SkyOmega/stores/mcp/` | `mercury-mcp` |

Each instance exposes a SPARQL HTTP endpoint. This means any instance can
query any other instance using the SPARQL SERVICE clause.

---

## SERVICE Queries

The SERVICE clause sends a sub-query to a remote SPARQL endpoint and
incorporates the results into the local query.

### Querying MCP from CLI

Start the CLI and query the MCP store:

```sparql
SELECT ?s ?p ?o WHERE {
    SERVICE <http://localhost:3030/sparql> {
        ?s ?p ?o
    }
} LIMIT 10
```

This sends the pattern `?s ?p ?o` to the MCP endpoint, retrieves the results,
and returns them in the CLI session.

### Combining local and remote data

The power of SERVICE is combining data from multiple stores in a single query:

```sparql
SELECT ?localName ?remoteName WHERE {
    ?person <http://xmlns.com/foaf/0.1/name> ?localName .
    SERVICE <http://localhost:3030/sparql> {
        ?person <http://xmlns.com/foaf/0.1/name> ?remoteName .
    }
}
```

This finds people who exist in both the CLI store and the MCP store, matching
on the same URI.

### SERVICE SILENT

If the remote endpoint might be unavailable, use SERVICE SILENT to avoid
query failure:

```sparql
SELECT ?s ?p ?o WHERE {
    ?s ?p ?o .
    OPTIONAL {
        SERVICE SILENT <http://localhost:3030/sparql> {
            ?s ?p ?remote_o .
        }
    }
} LIMIT 10
```

With SILENT, the query returns local results even if the MCP endpoint is down.

---

## Auto-Detection

When the CLI starts, it checks whether the MCP instance is running. If it
detects an active MCP endpoint, it prints a hint:

```
MCP instance detected - SERVICE <http://localhost:3030/sparql>
```

This tells you the MCP store is available for federated queries without
needing to look up the port.

---

## Pipe Attachment

SERVICE queries go through HTTP, which works but adds network overhead. For
direct access to another instance's store, use pipe attachment.

### Attach to MCP

```bash
mercury -a mcp
```

This connects to the MCP server's named pipe (`mercury-mcp`) and gives you
a full REPL session against the MCP store -- the same data that Claude reads
and writes.

```
Attaching to mcp via pipe 'mercury-mcp'...

mcp> :stats
Store Statistics:
  Quads:           2,456
  Atoms:           891
  ...

mcp> SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5
```

The short form `mercury -a` defaults to `mcp`:

```bash
mercury -a
```

### Attach to another CLI instance

```bash
mercury -a cli
```

### Custom pipe name

```bash
mercury -a my-custom-pipe
```

### What you can do in attach mode

Attach mode gives you a full REPL with all commands: queries, updates,
`:stats`, `:graphs`, `:count`, even `:prune`. This is particularly useful for:

- **Inspecting what Claude has stored** via the MCP server
- **Debugging semantic memory** issues
- **Running ad-hoc queries** against MCP data without restarting Claude
- **Loading data** into the MCP store manually

---

## Practical Workflows

### Claude accumulates, developer curates

The typical workflow with CLI and MCP running side by side:

1. Claude stores decisions, observations, and context via MCP during
   conversations
2. The developer attaches from the CLI to inspect what Claude has stored:
   ```bash
   mercury -a mcp
   ```
3. The developer runs queries to review, correct, or extend the knowledge:
   ```sparql
   SELECT ?title WHERE {
       ?d a <http://sky-omega.org/braid/Decision> ;
          <http://sky-omega.org/braid/title> ?title .
   }
   ```
4. The developer can also prune stale data or load additional context

### Cross-instance joins

Find resources that appear in both stores. Start the CLI with its own store
and query across:

```sparql
-- Find people in my local store who are also in the MCP store
SELECT ?person ?localRole ?mcpRole WHERE {
    ?person <http://example.org/role> ?localRole .
    SERVICE <http://localhost:3030/sparql> {
        ?person <http://example.org/role> ?mcpRole .
    }
}
```

### Loading shared data into MCP

Attach to MCP and load a shared knowledge file:

```bash
mercury -a mcp
```

```sparql
LOAD <file:///path/to/shared-vocabulary.ttl>
```

This makes the vocabulary available to Claude in all future sessions.

### Comparing stores

Use SERVICE to find triples that exist in one store but not the other:

```sparql
-- What's in MCP that's not in CLI?
SELECT ?s ?p ?o WHERE {
    SERVICE <http://localhost:3030/sparql> {
        ?s ?p ?o .
    }
    FILTER NOT EXISTS { ?s ?p ?o }
} LIMIT 20
```

---

## The Vision

Today, Mercury runs as personal instances -- one CLI, one MCP -- on a single
machine. The federation architecture is designed to scale:

- **Personal:** CLI and MCP on your laptop, federated queries between them
- **Team:** Multiple developers each running Mercury, with SERVICE queries
  across instances for shared knowledge
- **Organizational:** Central Mercury instances with curated vocabularies and
  decisions, queryable from any team member's local instance

The SPARQL SERVICE clause is the federation primitive. It works the same
whether the endpoint is on localhost or across the network.

---

## Port Configuration

If you need to change the default ports (for example, to avoid conflicts):

```bash
mercury -p 8080           # CLI on port 8080
```

Then adjust SERVICE URIs accordingly:

```sparql
SERVICE <http://localhost:8080/sparql> { ... }
```

To disable the HTTP endpoint entirely:

```bash
mercury --no-http
```

---

## See Also

- [Mercury CLI](mercury-cli.md) -- attach mode and HTTP endpoint details
- [Mercury MCP Server](mercury-mcp.md) -- the MCP server and its endpoint
- [Semantic Braid](semantic-braid.md) -- structured conversation memory
- [Installation and Tools](installation-and-tools.md) -- port conventions
