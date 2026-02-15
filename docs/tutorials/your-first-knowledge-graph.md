# Your First Knowledge Graph

Build a knowledge graph from scratch using Mercury. By the end you will
understand triples, write Turtle by hand, load data, and run SPARQL queries
that would be awkward in a spreadsheet or relational database.

> **Prerequisites:** Mercury tools installed. See [Getting Started](getting-started.md)
> if you haven't done that yet.

---

## The Problem with Spreadsheets

Imagine you're tracking a small development team. You start with a spreadsheet:

| Person | Role | Project | Skills | Start Date |
|--------|------|---------|--------|------------|
| Alice | Lead | Atlas | Go, Kubernetes | 2025-03-01 |
| Bob | Dev | Atlas | Go, PostgreSQL | 2025-06-15 |
| Carol | Dev | Beacon | Rust, WebAssembly | 2025-01-10 |

This works until it doesn't. Bob joins a second project. Alice picks up a new
skill. A project decision references another project. You add more columns,
split into multiple sheets, create foreign keys in your head.

The problem isn't the data -- it's the structure. Spreadsheets force you into
rows and columns. When the relationships between things matter more than the
things themselves, rows and columns get in the way.

---

## Thinking in Triples

A knowledge graph stores facts as **triples**: subject-predicate-object
statements.

```
Alice  hasRole    "Lead"
Alice  worksOn    Atlas
Alice  hasSkill   "Go"
Bob    worksOn    Atlas
Atlas  startedOn  "2025-03-01"
```

Each triple is a single fact. There are no columns to add, no schema to
migrate. To record that Bob joined a second project, you add one triple:

```
Bob  worksOn  Beacon
```

No schema change. No null columns. Just another fact.

### URIs as identifiers

In RDF, subjects and predicates are URIs. This avoids ambiguity -- your "Alice"
and someone else's "Alice" are different URIs:

```
<http://example.org/team/alice>  <http://example.org/vocab/hasRole>  "Lead"
```

Literals (strings, numbers, dates) are objects in quotes. URIs are in angle
brackets.

---

## Writing Your First Turtle File

Turtle is the most readable RDF format. It supports prefixes to shorten URIs
and semicolons to group triples about the same subject.

Start your editor and create a file called `team.ttl`:

```turtle
@prefix team: <http://example.org/team/> .
@prefix proj: <http://example.org/project/> .
@prefix v: <http://example.org/vocab/> .
@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

# --- People ---

team:alice
    v:name "Alice" ;
    v:role "Lead" ;
    v:worksOn proj:atlas ;
    v:hasSkill "Go", "Kubernetes" ;
    v:joinedOn "2025-03-01"^^xsd:date .

team:bob
    v:name "Bob" ;
    v:role "Developer" ;
    v:worksOn proj:atlas, proj:beacon ;
    v:hasSkill "Go", "PostgreSQL" ;
    v:joinedOn "2025-06-15"^^xsd:date .

team:carol
    v:name "Carol" ;
    v:role "Developer" ;
    v:worksOn proj:beacon ;
    v:hasSkill "Rust", "WebAssembly" ;
    v:joinedOn "2025-01-10"^^xsd:date .

# --- Projects ---

proj:atlas
    v:name "Atlas" ;
    v:status "active" ;
    v:startedOn "2025-03-01"^^xsd:date .

proj:beacon
    v:name "Beacon" ;
    v:status "active" ;
    v:startedOn "2025-01-10"^^xsd:date ;
    v:dependsOn proj:atlas .
```

A few things to notice:

- **Prefixes** (`@prefix team: ...`) shorten URIs. `team:alice` expands to
  `<http://example.org/team/alice>`.
- **Semicolons** (`;`) add another predicate-object pair to the same subject.
- **Commas** (`,`) add another object to the same subject and predicate.
  `v:hasSkill "Go", "Kubernetes"` creates two triples.
- **Typed literals** (`"2025-03-01"^^xsd:date`) give values a datatype.
- **Comments** start with `#`.

---

## Loading and Querying

### Validate first

Check the syntax before loading:

```bash
mercury-turtle --validate team.ttl
```

```
Validating: team.ttl
Valid Turtle: 18 triples
```

### Load and query in one shot

Use `mercury-sparql` to load the file and run a query:

```bash
mercury-sparql --load team.ttl \
  --query "SELECT ?name ?role WHERE { ?person <http://example.org/vocab/name> ?name . ?person <http://example.org/vocab/role> ?role }"
```

### Interactive exploration

For exploration, use the Mercury REPL with a temporary store:

```bash
mercury -m
```

Then load the file and query interactively:

```sparql
LOAD <file:///absolute/path/to/team.ttl>
```

Replace `/absolute/path/to/` with the actual path to your file.

```sparql
SELECT ?name ?role WHERE {
    ?person <http://example.org/vocab/name> ?name .
    ?person <http://example.org/vocab/role> ?role .
}
```

```
| name    | role        |
|---------|-------------|
| "Alice" | "Lead"      |
| "Bob"   | "Developer" |
| "Carol" | "Developer" |
```

### Who works on which project?

```sparql
SELECT ?person ?project WHERE {
    ?p <http://example.org/vocab/name> ?person .
    ?p <http://example.org/vocab/worksOn> ?proj .
    ?proj <http://example.org/vocab/name> ?project .
}
```

```
| person  | project  |
|---------|----------|
| "Alice" | "Atlas"  |
| "Bob"   | "Atlas"  |
| "Bob"   | "Beacon" |
| "Carol" | "Beacon" |
```

Bob appears twice -- once for each project. No duplicate rows, no schema
gymnastics. The data naturally reflects the relationship.

---

## Named Graphs

As your knowledge graph grows, you may want to separate data by domain. Named
graphs let you put triples into labeled containers.

In the Mercury REPL:

```sparql
INSERT DATA {
    GRAPH <http://example.org/graph/team> {
        <http://example.org/team/dave> <http://example.org/vocab/name> "Dave" .
        <http://example.org/team/dave> <http://example.org/vocab/role> "Intern" .
    }
}
```

```sparql
INSERT DATA {
    GRAPH <http://example.org/graph/decisions> {
        <http://example.org/decision/1> <http://example.org/vocab/title> "Use Go for Atlas" .
        <http://example.org/decision/1> <http://example.org/vocab/decidedBy> <http://example.org/team/alice> .
    }
}
```

Query within a specific graph:

```sparql
SELECT ?name WHERE {
    GRAPH <http://example.org/graph/team> {
        ?person <http://example.org/vocab/name> ?name .
    }
}
```

Query across all graphs:

```sparql
SELECT ?g ?s ?p ?o WHERE {
    GRAPH ?g { ?s ?p ?o }
} LIMIT 10
```

Named graphs are useful for separating concerns -- team data, project
decisions, external imports -- while still being able to query across
everything.

---

## The "Aha" Moment

Here are queries that are natural in SPARQL but painful in SQL or spreadsheets.

### Transitive dependencies

Which projects does Beacon depend on, directly or indirectly?

```sparql
SELECT ?dep ?name WHERE {
    <http://example.org/project/beacon> <http://example.org/vocab/dependsOn>+ ?dep .
    ?dep <http://example.org/vocab/name> ?name .
}
```

The `+` is a property path -- it follows `dependsOn` links transitively. If
Atlas depends on yet another project, that result appears automatically. In
SQL, this requires recursive CTEs. In a spreadsheet, it requires manual
tracing.

### OPTIONAL without NULLs

Find everyone and their skills, including people who haven't listed skills:

```sparql
SELECT ?name ?skill WHERE {
    ?person <http://example.org/vocab/name> ?name .
    OPTIONAL { ?person <http://example.org/vocab/hasSkill> ?skill }
}
```

No NULLs, no LEFT JOINs fighting with GROUP BY. People without skills simply
have fewer rows.

### UNION across different shapes

Find anything with a name, whether it's a person or a project:

```sparql
SELECT ?thing ?name WHERE {
    ?thing <http://example.org/vocab/name> ?name .
}
```

There is no `UNION` needed. Persons and projects both have a `name` predicate.
The query finds all of them because triples have no table boundaries.

### Cross-domain queries

Find people who work on projects that started before they joined:

```sparql
SELECT ?person ?project ?projStart ?joinDate WHERE {
    ?p <http://example.org/vocab/name> ?person .
    ?p <http://example.org/vocab/worksOn> ?proj .
    ?p <http://example.org/vocab/joinedOn> ?joinDate .
    ?proj <http://example.org/vocab/name> ?project .
    ?proj <http://example.org/vocab/startedOn> ?projStart .
    FILTER(?joinDate > ?projStart)
}
```

This crosses "tables" that don't exist. In a relational database, you'd need
a join table, foreign keys, and careful schema design. In a knowledge graph,
the relationships are the data.

---

## Where to Go Next

- [Mercury CLI](mercury-cli.md) -- deep-dive into the interactive REPL
- [Mercury SPARQL CLI](mercury-sparql-cli.md) -- batch queries and scripting
- [Mercury Turtle CLI](mercury-turtle-cli.md) -- validation and format conversion
- [Mercury MCP Server](mercury-mcp.md) -- giving Claude persistent semantic memory
- [Installation and Tools](installation-and-tools.md) -- full tool lifecycle
