# Getting Started with Sky Omega

A 30-minute walkthrough from clone to first query. By the end you will have
Mercury installed, a working SPARQL REPL, and data loaded from a file.

> **Prerequisites:** .NET 10 SDK and git.

---

## Clone and Build

```bash
git clone https://github.com/your-org/sky-omega.git
cd sky-omega

dotnet build SkyOmega.sln
dotnet test
```

All tests should pass. The build produces the Mercury library, CLI tools,
and supporting projects.

---

## Install the Tools

On macOS or Linux, run the install script from the repository root:

```bash
./tools/install-tools.sh
```

This packs and installs all Mercury global tools. After installation you
have four commands available from any directory:

| Command | Description |
|---------|-------------|
| `mercury` | SPARQL CLI with persistent store |
| `mercury-mcp` | MCP server for Claude |
| `mercury-sparql` | SPARQL query engine demo |
| `mercury-turtle` | Turtle parser demo |

Verify the install:

```bash
mercury --version
```

---

## Your First Session (In-Memory)

Start Mercury in temporary mode. The store lives only for the duration of
the session and is deleted on exit:

```bash
mercury -m
```

You will see the REPL prompt:

```
mercury>
```

### Insert some data

Type (or paste) an INSERT DATA statement. The REPL accepts multi-line input;
it detects the closing brace and executes automatically:

```sparql
INSERT DATA {
  <http://example.org/alice> <http://xmlns.com/foaf/0.1/name> "Alice" .
  <http://example.org/alice> <http://xmlns.com/foaf/0.1/knows> <http://example.org/bob> .
  <http://example.org/bob>   <http://xmlns.com/foaf/0.1/name> "Bob" .
}
```

### Query the data

Common prefixes (rdf, rdfs, xsd, owl, foaf, dc, dcterms, skos, schema, ex)
are pre-registered, so you can use them without declaring PREFIX lines:

```sparql
SELECT ?person ?name WHERE {
  ?person foaf:name ?name .
}
```

Expected output:

```
| person                      | name    |
|-----------------------------|---------|
| <http://example.org/alice>  | "Alice" |
| <http://example.org/bob>    | "Bob"   |
```

### Check store statistics

```
mercury> :stats
```

This shows triple count, atom count, index sizes, and other store metrics.

### Exit

```
mercury> :quit
```

Because you started with `-m`, the temporary store is now deleted.

---

## Your First Persistent Session

Start Mercury without any flags to use the default persistent store:

```bash
mercury
```

The store is created automatically at a platform-specific path:

| Platform | Default store path |
|----------|--------------------|
| macOS | `~/Library/SkyOmega/stores/cli/` |
| Linux | `~/.local/share/SkyOmega/stores/cli/` |
| Windows | `%LOCALAPPDATA%\SkyOmega\stores\cli\` |

Everything you insert in this session persists across restarts. Run the same
INSERT DATA and SELECT from the previous section -- when you quit and
re-launch `mercury`, your data is still there.

You can also point Mercury at a custom store directory:

```bash
mercury ./mydata
```

### HTTP endpoint

When Mercury starts, it automatically exposes a SPARQL HTTP endpoint at
`http://localhost:3031/sparql`. You can query it from another terminal:

```bash
curl -G http://localhost:3031/sparql \
  --data-urlencode "query=SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5"
```

---

## Loading Data from Files

The repository includes a bootstrap knowledge graph at
`docs/knowledge/bootstrap.ttl`. Load it into your store using the SPARQL
LOAD command. File URIs must use absolute paths:

```sparql
LOAD <file:///absolute/path/to/sky-omega/docs/knowledge/bootstrap.ttl>
```

Replace `/absolute/path/to/sky-omega` with the actual path to your clone.
For example, on macOS:

```sparql
LOAD <file:///Users/you/src/sky-omega/docs/knowledge/bootstrap.ttl>
```

### Verify the loaded data

Query the components defined in the bootstrap graph:

```sparql
SELECT ?component ?label ?role WHERE {
  ?component <urn:sky-omega:partOf> <urn:sky-omega:data:sky-omega> ;
             <http://www.w3.org/2000/01/rdf-schema#label> ?label ;
             <urn:sky-omega:role> ?role .
}
```

You should see Mercury, Lucy, James, Mira, and Minerva listed with their
roles.

Check the total triple count:

```
mercury> :count
```

---

## REPL Command Reference

These commands are available at the `mercury>` prompt:

| Command | Alias | Description |
|---------|-------|-------------|
| `:help` | `:h`, `:?` | Show available commands |
| `:stats` | `:s` | Show store statistics |
| `:prefixes` | `:p` | List registered prefixes |
| `:graphs` | | List named graphs |
| `:count` | | Count triples (optionally matching a pattern) |
| `:prune` | | Compact the store (remove soft-deleted data) |
| `:quit` | `:q`, `:exit` | Exit the REPL |

You can also register additional prefixes interactively:

```
mercury> PREFIX proj: <http://example.org/project/>
```

---

## Where to Go Next

- [Mercury CLI reference](mercury-cli.md) -- full CLI options, REPL commands,
  and advanced usage
- [Mercury MCP server](mercury-mcp.md) -- setting up Mercury as an MCP
  server for Claude
- [Mercury SPARQL CLI](mercury-sparql-cli.md) -- batch queries and format conversion
- [Mercury Turtle CLI](mercury-turtle-cli.md) -- validation, conversion, benchmarking
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF for newcomers
- [Installation and Tools](installation-and-tools.md) -- full tool lifecycle
