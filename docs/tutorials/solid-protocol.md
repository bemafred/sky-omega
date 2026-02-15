# Solid Protocol

Mercury.Solid implements the W3C Solid Protocol, turning a QuadStore into a
personal online data store (Pod). Resources are stored as RDF in named graphs,
accessible over HTTP with content negotiation, LDP container semantics, N3
Patch modification, and pluggable access control.

> **Prerequisites:** .NET 10 SDK. Familiarity with Mercury and basic RDF
> concepts. See [Your First Knowledge Graph](your-first-knowledge-graph.md)
> for RDF basics and [Embedding Mercury](embedding-mercury.md) for the
> QuadStore API.

---

## What Is Solid

Solid (Social Linked Data) is a W3C specification for decentralized data
storage. The core idea: your data lives in a Pod that you control, not in
application silos. Applications read and write to your Pod using standard
HTTP and RDF.

Mercury is a natural fit for Solid because its QuadStore already provides
named graph support. Each resource URI maps to a named graph, giving you
isolated storage per resource with SPARQL queryability across the entire Pod.

Mercury.Solid is BCL-only, like the rest of Mercury. No external web
framework required.

---

## Starting a Server

Create a `SolidServer` with a `QuadStore` and a base URI:

```csharp
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Solid;

using var store = new QuadStore("/path/to/pod-store");
var server = new SolidServer(store, "http://localhost:8080/");
server.Start();

Console.WriteLine("Solid server running. Press Enter to stop.");
Console.ReadLine();

await server.StopAsync();
```

### Server Options

Configure the server with `SolidServerOptions`:

```csharp
var options = new SolidServerOptions
{
    EnableCors = true,
    CorsOrigin = "https://myapp.example.com",
    MaxRequestBodySize = 5 * 1024 * 1024,  // 5 MB
    RequestTimeoutMs = 15000
};

var server = new SolidServer(store, "http://localhost:8080/", options);
```

| Option | Default | Description |
|--------|---------|-------------|
| `AccessPolicy` | `null` (allow all) | Access control policy (WAC, ACP, or custom) |
| `EnableCors` | `true` | Enable CORS headers |
| `CorsOrigin` | `"*"` | Allowed CORS origin |
| `MaxRequestBodySize` | 10 MB | Maximum request body in bytes |
| `RequestTimeoutMs` | 30000 | Request timeout in milliseconds |

---

## Resource Operations

All examples use curl against a server running at `http://localhost:8080/`.

### PUT -- Create or Replace

Create a resource by PUTting Turtle data:

```bash
curl -X PUT http://localhost:8080/profile/card \
  -H "Content-Type: text/turtle" \
  -d '
@prefix foaf: <http://xmlns.com/foaf/0.1/> .
<#me> a foaf:Person ;
    foaf:name "Alice" ;
    foaf:mbox <mailto:alice@example.com> .
'
```

PUT is idempotent. If the resource exists, all its triples are replaced. The
server responds with `201 Created` for new resources or `204 No Content` for
updates.

Supported request content types:

| Content-Type | Format |
|--------------|--------|
| `text/turtle` | Turtle (default) |
| `application/n-triples` | N-Triples |
| `application/ld+json` | JSON-LD |
| `application/rdf+xml` | RDF/XML |

### GET -- Read

Read a resource with content negotiation:

```bash
# Default (Turtle)
curl http://localhost:8080/profile/card

# Request N-Triples
curl -H "Accept: application/n-triples" http://localhost:8080/profile/card
```

The response includes metadata headers:

```
HTTP/1.1 200 OK
Content-Type: text/turtle
ETag: "a1b2c3d4"
Last-Modified: Sat, 15 Feb 2026 12:00:00 GMT
Link: <http://www.w3.org/ns/ldp#Resource>; rel="type",
      <http://www.w3.org/ns/ldp#RDFSource>; rel="type",
      <http://localhost:8080/profile/card.acl>; rel="acl"
Accept-Patch: text/n3, application/n3-patch
```

### DELETE -- Remove

```bash
curl -X DELETE http://localhost:8080/profile/card
```

Returns `204 No Content` on success, `404 Not Found` if the resource does
not exist.

### HEAD -- Metadata Only

```bash
curl -I http://localhost:8080/profile/card
```

Returns the same headers as GET without a response body.

### Conditional Requests

Use ETags for optimistic concurrency:

```bash
# Only update if ETag matches (prevent lost updates)
curl -X PUT http://localhost:8080/profile/card \
  -H "Content-Type: text/turtle" \
  -H 'If-Match: "a1b2c3d4"' \
  -d '@updated-profile.ttl'

# Only create if resource does not exist
curl -X PUT http://localhost:8080/profile/card \
  -H "Content-Type: text/turtle" \
  -H "If-None-Match: *" \
  -d '@new-profile.ttl'
```

If the precondition fails, the server returns `412 Precondition Failed`.

---

## Containers

Containers are LDP BasicContainers that hold other resources. A container
URI always ends with `/`.

### Creating a Container

POST to a parent container with a Link header declaring the container type:

```bash
curl -X POST http://localhost:8080/ \
  -H "Content-Type: text/turtle" \
  -H 'Link: <http://www.w3.org/ns/ldp#BasicContainer>; rel="type"' \
  -H "Slug: documents" \
  -d ''
```

This creates the container at `http://localhost:8080/documents/`.

### Creating Resources in a Container

POST to a container to create a child resource. Use the `Slug` header to
suggest a name:

```bash
curl -X POST http://localhost:8080/documents/ \
  -H "Content-Type: text/turtle" \
  -H "Slug: meeting-notes" \
  -d '
@prefix dc: <http://purl.org/dc/terms/> .
<> dc:title "Sprint Planning Notes" ;
   dc:created "2026-02-15"^^<http://www.w3.org/2001/XMLSchema#date> .
'
```

The server creates the resource at `http://localhost:8080/documents/meeting-notes`
(or a generated name if the slug is already taken) and returns `201 Created`
with a `Location` header pointing to the new resource.

### Listing Container Contents

GET a container to see its members:

```bash
curl http://localhost:8080/documents/
```

The response includes `ldp:contains` triples for each member:

```turtle
@prefix ldp: <http://www.w3.org/ns/ldp#> .
@prefix dcterms: <http://purl.org/dc/terms/> .

<http://localhost:8080/documents/>
    a ldp:Container, ldp:BasicContainer ;
    dcterms:modified "2026-02-15T12:00:00Z"^^<http://www.w3.org/2001/XMLSchema#dateTime> ;
    ldp:contains <http://localhost:8080/documents/meeting-notes> .
```

---

## N3 Patch

N3 Patch modifies a resource without replacing it entirely. It uses a
WHERE/DELETE/INSERT pattern with variable binding.

### Patch Syntax

An N3 Patch document declares a `solid:InsertDeletePatch` with up to three
clauses:

```n3
@prefix solid: <http://www.w3.org/ns/solid/terms#> .
@prefix foaf: <http://xmlns.com/foaf/0.1/> .

_:patch a solid:InsertDeletePatch ;
  solid:where   { ?person foaf:name "Alice" } ;
  solid:deletes { ?person foaf:mbox <mailto:alice@example.com> } ;
  solid:inserts { ?person foaf:mbox <mailto:alice@newdomain.com> } .
```

| Clause | Purpose |
|--------|---------|
| `solid:where` | Pattern to match -- binds variables |
| `solid:deletes` | Triples to remove (variables bound from WHERE) |
| `solid:inserts` | Triples to add (variables bound from WHERE) |

All three clauses are optional, but at least one of `solid:deletes` or
`solid:inserts` must be present.

### Variable Binding Example

The WHERE clause matches against existing triples and binds variables.
Those variables are then substituted into the DELETE and INSERT clauses:

```bash
curl -X PATCH http://localhost:8080/profile/card \
  -H "Content-Type: text/n3" \
  -d '
@prefix solid: <http://www.w3.org/ns/solid/terms#> .
@prefix foaf: <http://xmlns.com/foaf/0.1/> .

_:patch a solid:InsertDeletePatch ;
  solid:where   { ?person foaf:name ?name } ;
  solid:deletes { ?person foaf:mbox ?old } ;
  solid:inserts { ?person foaf:mbox <mailto:updated@example.com> } .
'
```

If the WHERE clause matches three people, the DELETE and INSERT apply once
per binding -- removing each person's old mbox and adding the new one.

### Insert-Only Patch

Omit the WHERE and DELETE clauses to add triples without conditions:

```n3
@prefix solid: <http://www.w3.org/ns/solid/terms#> .
@prefix foaf: <http://xmlns.com/foaf/0.1/> .

_:patch a solid:InsertDeletePatch ;
  solid:inserts { <#me> foaf:nick "ally" } .
```

### Delete-Only Patch

Omit the INSERT clause to remove triples:

```n3
@prefix solid: <http://www.w3.org/ns/solid/terms#> .
@prefix foaf: <http://xmlns.com/foaf/0.1/> .

_:patch a solid:InsertDeletePatch ;
  solid:where   { ?person foaf:name "Alice" } ;
  solid:deletes { ?person foaf:nick ?nick } .
```

---

## Access Control

Mercury.Solid supports two W3C access control models through a pluggable
`IAccessPolicy` interface.

### Access Modes

| Mode | Flag | HTTP Methods |
|------|------|--------------|
| Read | `AccessMode.Read` | GET, HEAD |
| Write | `AccessMode.Write` | PUT, DELETE, PATCH |
| Append | `AccessMode.Append` | POST (subset of Write) |
| Control | `AccessMode.Control` | Modify ACL documents |

### WAC (Web Access Control)

WAC uses `.acl` documents alongside resources. Each ACL document contains
`acl:Authorization` triples granting access:

```turtle
@prefix acl: <http://www.w3.org/ns/auth/acl#> .
@prefix foaf: <http://xmlns.com/foaf/0.1/> .

<#owner>
    a acl:Authorization ;
    acl:agent <https://alice.example.com/profile/card#me> ;
    acl:accessTo <http://localhost:8080/profile/card> ;
    acl:mode acl:Read, acl:Write, acl:Control .

<#public>
    a acl:Authorization ;
    acl:agentClass foaf:Agent ;
    acl:accessTo <http://localhost:8080/profile/card> ;
    acl:mode acl:Read .
```

WAC agent matching:

| ACL Predicate | Matches |
|---------------|---------|
| `acl:agent <webid>` | Specific agent |
| `acl:agentClass foaf:Agent` | Everyone (public) |
| `acl:agentClass acl:AuthenticatedAgent` | Any authenticated agent |
| `acl:agentGroup <group>` | Members of a group |

Container defaults apply to child resources via `acl:default`.

### ACP (Access Control Policy)

ACP provides more flexible policy composition with matcher combinators:

```turtle
@prefix acp: <http://www.w3.org/ns/solid/acp#> .

<#policy>
    a acp:Policy ;
    acp:allow acp:Read, acp:Write ;
    acp:allOf [ acp:agent <https://alice.example.com/profile/card#me> ] .
```

ACP combinators:

| Combinator | Semantics |
|------------|-----------|
| `acp:allOf` | All matchers must match (AND) |
| `acp:anyOf` | Any matcher must match (OR) |
| `acp:noneOf` | No matcher may match (NOT) |

Explicit deny takes precedence: effective modes = `allow & ~deny`.

### Configuring Access Control

```csharp
// Development: allow everything
var options = new SolidServerOptions
{
    AccessPolicy = AccessPolicyFactory.CreateAllowAll()
};

// Production: WAC
var options = new SolidServerOptions
{
    AccessPolicy = AccessPolicyFactory.CreateWac(documentProvider)
};

// Production: ACP
var options = new SolidServerOptions
{
    AccessPolicy = AccessPolicyFactory.CreateAcp(documentProvider)
};
```

The `IAccessControlDocumentProvider` interface supplies ACL/ACP documents
and WebID profiles. Implement it to load access control documents from
your QuadStore or another source.

### AllowAll for Development

When no `AccessPolicy` is set (or when using `CreateAllowAll()`), the server
permits all operations. This is useful during development but must not be
used in production.

---

## Graph-to-Pod Mapping

Each Solid resource URI maps to a named graph in the QuadStore:

| Resource URI | Named Graph |
|-------------|-------------|
| `http://localhost:8080/profile/card` | `http://localhost:8080/profile/card` |
| `http://localhost:8080/documents/` | `http://localhost:8080/documents/` |

This mapping means you can query across all Pod resources using SPARQL:

```sparql
SELECT ?resource ?title WHERE {
  GRAPH ?resource {
    ?s <http://purl.org/dc/terms/title> ?title .
  }
}
```

Or query a specific resource's graph:

```sparql
SELECT ?p ?o WHERE {
  GRAPH <http://localhost:8080/profile/card> {
    <http://localhost:8080/profile/card#me> ?p ?o .
  }
}
```

---

## Embedding in .NET

A complete embedded server:

```csharp
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Solid;

using var store = new QuadStore("/path/to/pod");

var options = new SolidServerOptions
{
    EnableCors = true,
    CorsOrigin = "https://app.example.com",
    MaxRequestBodySize = 10 * 1024 * 1024,
    RequestTimeoutMs = 30000
};

var server = new SolidServer(store, "http://localhost:8080/", options);
server.Start();

// Server runs until stopped
await server.StopAsync();
```

The server implements both `IDisposable` and `IAsyncDisposable`:

```csharp
await using var server = new SolidServer(store, "http://localhost:8080/");
server.Start();
// Disposed automatically at end of scope
```

---

## Limitations

Mercury.Solid implements the core Solid Protocol but does not yet include:

- **Solid-OIDC authentication.** The current implementation accepts an
  `X-WebID` header for development but does not validate Solid-OIDC tokens.
  Production deployments require an authentication layer.
- **WebSocket notifications.** Resource change notifications are not yet
  implemented.
- **SPARQL endpoints.** The `EnableSparql` and `EnableSparqlUpdate` options
  exist but are not functional. Use `SparqlHttpServer` from
  `Mercury.Sparql.Protocol` for SPARQL access to the same QuadStore.
- **Binary (non-RDF) resources.** Only RDF sources are supported. Binary
  file storage requires a separate mechanism.

---

## See Also

- [Embedding Mercury](embedding-mercury.md) -- QuadStore API for programmatic access
- [ADR-015: Solid Architecture](../adrs/mercury/ADR-015-solid-architecture.md) -- design decisions and implementation phases
