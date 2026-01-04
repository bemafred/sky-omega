# ADR-003: Dual-Mode Store Access (Local Pool + Remote Connection)

## Status

Proposed

## Context

Mercury.Cli and Mercury.Mcp serve different roles:

- **Mercury.Mcp**: Owns stores, communicates with Claude Code via stdin/stdout (MCP protocol)
- **Mercury.Cli**: Human REPL interface for queries, experimentation, administration

The question: How does a human (via Cli) and Claude Code (via Mcp) work with the same data?

## Decision

Mercury.Cli operates in **dual mode**:

1. **Local StorePool** - always available, owned by Cli process
2. **Remote Connection** - optional, connects to Mercury.Mcp via named pipe/unix socket

Both modes active simultaneously. User switches context with REPL commands.

```
┌─────────────────────────────────────────────────────────────┐
│ Mercury.Cli                                                 │
│                                                             │
│  ┌─────────────────────┐    ┌─────────────────────┐        │
│  │ Local StorePool     │    │ Remote Connection   │        │
│  │                     │    │                     │        │
│  │  scratch            │    │  ──► Mercury.Mcp   │        │
│  │  experiment         │    │      │              │        │
│  │  temp (SERVICE)     │    │      ├─► primary   │        │
│  │                     │    │      └─► secondary │        │
│  └─────────────────────┘    └─────────────────────┘        │
│           │                          │                      │
│           └──────────┬───────────────┘                      │
│                      ▼                                      │
│               Active Context                                │
│              (one at a time)                                │
│                      │                                      │
│                      ▼                                      │
│                    REPL                                     │
└─────────────────────────────────────────────────────────────┘
```

## REPL Commands

```
# Local pool management
:local <name>              Switch to local store (creates if needed)
:local                     List local stores
:local drop <name>         Clear and release local store

# Remote connection
:connect [path]            Connect to Mcp (default: well-known path)
:disconnect                Disconnect from Mcp
:remote <name>             Switch to remote store
:remote                    List remote stores (via Mcp)

# Context info
:status                    Show current context (local/remote, store name)
:ping                      Test Mcp connection

# Cross-context operations
SERVICE <mcp://store> { }  Query remote from local context
SERVICE <local://store> { } Query local from remote context (if supported)
```

## Example Session

```
$ mercury-cli

mercury> :status
Context: local/default (empty)
Mcp: not connected

mercury> :local scratch
Switched to local store: scratch

mercury> LOAD <experiment.ttl>
Loaded 1,547 triples

mercury> SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }
┌───────┐
│ n     │
├───────┤
│ 1547  │
└───────┘

mercury> :connect
Connected to Mcp at /tmp/mercury-mcp.sock

mercury> :remote primary
Switched to remote store: primary (via Mcp)

mercury> SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }
┌───────┐
│ n     │
├───────┤
│ 84291 │
└───────┘

mercury> :local scratch
Switched to local store: scratch

mercury> # Query remote FROM local context
mercury> SELECT * WHERE {
           ?local <http://ex/source> ?src .
           SERVICE <mcp://primary> {
             ?src <http://ex/label> ?label
           }
         }
┌─────────────────┬─────────────────┬─────────────┐
│ local           │ src             │ label       │
├─────────────────┼─────────────────┼─────────────┤
│ ...             │ ...             │ ...         │
└─────────────────┴─────────────────┴─────────────┘
```

## Architecture

### IStoreContext Interface

```csharp
/// <summary>
/// Unified interface for store operations.
/// Implemented by both local and remote contexts.
/// </summary>
public interface IStoreContext
{
    string Name { get; }
    bool IsRemote { get; }
    
    QueryResult Query(string sparql);
    void Update(string sparqlUpdate);
    void Load(Stream turtle, string? baseUri = null);
    long TripleCount { get; }
}
```

### Local Context

```csharp
public sealed class LocalStoreContext : IStoreContext
{
    private readonly QuadStore _store;
    
    public string Name { get; }
    public bool IsRemote => false;
    
    public QueryResult Query(string sparql)
    {
        // Direct execution against local store
        var parser = new SparqlParser(sparql);
        var executor = new QueryExecutor(_store, ...);
        return executor.Execute();
    }
}
```

### Remote Context

```csharp
public sealed class RemoteStoreContext : IStoreContext
{
    private readonly MpcConnection _connection;
    private readonly string _storeName;
    
    public string Name => _storeName;
    public bool IsRemote => true;
    
    public QueryResult Query(string sparql)
    {
        // Send to Mcp, receive results
        var request = new QueryRequest(_storeName, sparql);
        return _connection.Send<QueryResult>(request);
    }
}
```

### ReplSession

```csharp
public sealed class ReplSession
{
    private readonly LocalStorePool _localPool;
    private MpcConnection? _remoteConnection;
    
    private IStoreContext _activeContext;
    
    public void SwitchToLocal(string name)
    {
        var store = _localPool.GetOrCreate(name);
        _activeContext = new LocalStoreContext(name, store);
    }
    
    public void SwitchToRemote(string name)
    {
        if (_remoteConnection == null)
            throw new InvalidOperationException("Not connected to Mcp");
        
        _activeContext = new RemoteStoreContext(_remoteConnection, name);
    }
    
    public QueryResult Execute(string sparql)
    {
        return _activeContext.Query(sparql);
    }
}
```

## Mcp Internal Protocol

Mercury.Cli ↔ Mercury.Mcp communication uses a simple request/response protocol over named pipe or unix socket. **Not MCP protocol** - that's reserved for Claude Code.

```csharp
// Simple JSON-based internal protocol
{ "type": "query", "store": "primary", "sparql": "SELECT ..." }
{ "type": "result", "bindings": [...] }

{ "type": "list-stores" }
{ "type": "stores", "names": ["primary", "secondary"] }

{ "type": "update", "store": "primary", "sparql": "INSERT ..." }
{ "type": "ok" }
```

## SERVICE Resolution

When executing SERVICE clauses, the endpoint URI determines routing:

| URI scheme | Resolution |
|------------|------------|
| `mcp://store` | Route to Mcp connection → named store |
| `local://store` | Route to local pool → named store |
| `http://...` | HTTP request to SPARQL endpoint (remote or local) |
| `file://...` | Load file into temp store, query |

Both Mercury.Cli and Mercury.Mcp can activate `SparqlHttpServer` to expose stores via HTTP:

```
┌─────────────────────────────────────────────────────────────┐
│                                                             │
│  Mercury.Cli                      Mercury.Mcp               │
│  ════════════                     ═══════════               │
│                                                             │
│  :http start 8080                 --http-port 9090          │
│       │                                │                    │
│       ▼                                ▼                    │
│  SparqlHttpServer              SparqlHttpServer             │
│  localhost:8080/sparql         localhost:9090/sparql        │
│       │                                │                    │
│       ▼                                ▼                    │
│  Local StorePool               Shared StorePool             │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

This enables:
- Standard SPARQL clients (Protégé, curl, etc.) to query any store
- SERVICE federation between Cli and Mcp via HTTP
- External tools to participate in the ecosystem

### REPL Commands for HTTP

```
:http start [port]         Start HTTP endpoint (default: 8080)
:http stop                 Stop HTTP endpoint
:http status               Show HTTP endpoint status
:http expose <store>       Expose specific store (default: active context)
```

### Example: Cross-process federation via HTTP

```
# Terminal 1: Mcp with HTTP
$ mercury-mcp --store /data/main --http-port 9090

# Terminal 2: Cli with local experimentation
$ mercury-cli
mercury> :local scratch
mercury> LOAD <experiment.ttl>
mercury> # Query Mcp's store via standard HTTP SERVICE
mercury> SELECT * WHERE {
           ?local <http://ex/ref> ?id .
           SERVICE <http://localhost:9090/sparql> {
             ?id <http://ex/label> ?label
           }
         }
```

```csharp
public QuadStore ResolveServiceEndpoint(string uri)
{
    if (uri.StartsWith("mcp://"))
    {
        var storeName = uri.Substring(6);
        return _remoteConnection.GetStore(storeName);  // Proxy
    }
    
    if (uri.StartsWith("local://"))
    {
        var storeName = uri.Substring(8);
        return _localPool.Get(storeName);
    }
    
    // Standard HTTP SERVICE
    return MaterializeHttpService(uri);
}
```

## Benefits

| Capability | Enabled by |
|------------|------------|
| Safe experimentation | Local pool - isolated, disposable |
| Shared data with Claude Code | Remote connection to Mcp |
| Cross-store queries | SERVICE with mcp:// and local:// |
| No file locking issues | Mcp owns files, Cli connects via socket |
| Offline work | Local pool works without Mcp |
| Live collaboration | Human and Claude Code see same stores |
| External tool integration | SparqlHttpServer on both Cli and Mcp |
| Standard SPARQL federation | HTTP SERVICE between any endpoints |

## Consequences

- Mercury.Cli is always useful (local mode), optionally powerful (remote mode)
- No file locking conflicts - clear ownership model
- SERVICE becomes unified routing mechanism for all store types
- Simple internal protocol - no over-engineering
- Same REPL code regardless of context (IStoreContext abstraction)

## Implementation Order

1. **IStoreContext interface** - unify local/remote access pattern
2. **LocalStoreContext** - wrap existing QuadStore
3. **REPL commands** - :local, :status
4. **Internal protocol definition** - simple JSON request/response
5. **MpcConnection** - named pipe / unix socket client
6. **Mcp internal endpoint** - listen on socket, handle requests
7. **RemoteStoreContext** - proxy via MpcConnection
8. **REPL commands** - :connect, :remote, :disconnect
9. **SERVICE routing** - mcp:// and local:// schemes
10. **Cross-context SERVICE** - query remote from local and vice versa

## Future Extensions

- `mcp://host:port/store` - remote Mcp over TCP
- Store aliasing - `:alias prod mcp://primary`
- Context stacking - push/pop for nested operations
- Transaction boundaries across contexts