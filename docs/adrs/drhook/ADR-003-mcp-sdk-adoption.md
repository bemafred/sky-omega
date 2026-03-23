# ADR-003 - MCP SDK Adoption

## Status
Accepted (2026-03-23) — retroactive

## Context

The DrHook.Poc implemented its own JSON-RPC 2.0 server (`McpStdioServer`, ~180 lines) with manual tool registration, request dispatch, input parsing (`ToolArgs`), and response framing. This was appropriate for a proof of concept — it validated BCL-only sovereignty and kept external dependencies to zero.

When DrHook moved into sky-omega (ADR-004), a decision was needed: keep the hand-rolled server, or adopt the official `ModelContextProtocol` NuGet SDK that Mercury.Mcp already uses.

### Hand-Rolled Server (PoC)

```csharp
// Manual registration
server.RegisterTool("drhook:processes", "...", inputSchema, async (args, ct) => { ... });

// Manual dispatch
var response = method switch
{
    "initialize"  => HandleInitialize(id),
    "tools/list"  => HandleToolsList(id),
    "tools/call"  => await HandleToolsCallAsync(id, @params, stderr, ct),
    _             => HandleUnknownMethod(id, method)
};

// Manual argument parsing
var pid = args.GetInt("pid");
var hypothesis = args.GetStringOrDefault("hypothesis", null);
```

### SDK-Based Server (Current)

```csharp
// DI + attribute-based registration
builder.Services
    .AddMcpServer(options => { options.ServerInfo = new() { Name = "drhook-mcp" }; })
    .WithStdioServerTransport()
    .WithTools<DrHookTools>();

// Attribute-based tools with native parameter binding
[McpServerTool(Name = "drhook_step_launch"), Description("...")]
public async Task<string> StepLaunch(
    [Description("Target process ID")] int pid,
    [Description("Source file path")] string sourceFile,
    ...)
```

## Decision

Adopt the official `ModelContextProtocol` SDK (v0.8.0-preview.1) with `Microsoft.Extensions.Hosting`, following the pattern established by Mercury.Mcp.

The hand-rolled `McpStdioServer` and `ToolArgs` classes are not migrated.

## Rationale

1. **Consistency** — Mercury.Mcp already uses the SDK. Two MCP servers in the same solution should follow the same pattern.

2. **Correctness** — The SDK handles protocol version negotiation, capability advertisement, error formatting, and edge cases that the hand-rolled server would need to track as the MCP spec evolves.

3. **Maintenance** — Attribute-based tool registration with automatic schema generation eliminates ~180 lines of boilerplate and removes the need to manually synchronize input schemas with handler signatures.

4. **DI integration** — `SteppingSessionManager` as a singleton service eliminates manual lifecycle management. Future services (breakpoint registry, expression evaluator) can be injected the same way.

5. **Sky Omega convention** — External dependencies are acceptable at the host/MCP layer. Only the core libraries (Mercury, DrHook) maintain BCL-only discipline. The MCP server is a surface, not a substrate.

## Consequences

### Positive
- Tool definitions are self-documenting (descriptions + parameter types in one place)
- Protocol compliance maintained by SDK vendor, not by us
- Input schema generated automatically from C# method signatures
- `CancellationToken` injection handled automatically

### Trade-offs
- Lost BCL-only purity at the MCP layer (acceptable per sky-omega convention)
- SDK is preview (0.8.0-preview.1) — API may change, but Mercury.Mcp tracks the same version
- Tool names changed from colon-separated (`drhook:processes`) to underscore-separated (`drhook_processes`) per SDK convention

### What Was Preserved
- **Core library (`DrHook`) remains BCL-only** + 2 first-party Microsoft NuGet packages
- **DAP client is still hand-rolled** — DrHook owns the DAP wire protocol, not an SDK
- **All 13 tools migrated** with identical semantics and descriptions

## References
- [ADR-004 (top-level)](../ADR-004-drhook-runtime-observation-substrate.md) — DrHook intent and scope
- [Mercury.Mcp](../../src/Mercury.Mcp/) — established SDK pattern
- [ModelContextProtocol SDK](https://www.nuget.org/packages/ModelContextProtocol) — official Microsoft package
