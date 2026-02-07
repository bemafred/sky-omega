# ADR-019: Global Tool Packaging and Persistent Stores

**Status:** Accepted
**Date:** 2026-02-07

## Context

Sky Omega reached v1.0 with four CLI/MCP executables runnable only via `dotnet run --project` from within the repo. For v2.0, these tools need to be globally accessible from any terminal, any repo, on all platforms. Mercury.Mcp must also be connectable from Claude Code as an MCP server with persistent semantic memory.

**Problems addressed:**
1. Tools only runnable from repo root via `dotnet run`
2. MCP server defaulted to `./mcp-store` (relative, ephemeral)
3. CLI defaulted to temp store (deleted on exit)
4. Hand-rolled MCP protocol (~494 lines) instead of official SDK
5. No Claude Code integration (`.mcp.json`)

## Decision

### 1. `dotnet tool` packaging

All four executables are packaged as .NET global tools via `PackAsTool`:

| Project | PackageId | ToolCommandName | Version |
|---------|-----------|-----------------|---------|
| Mercury.Cli | SkyOmega.Mercury.Cli | `mercury` | 2.0.0-preview.1 |
| Mercury.Cli.Sparql | SkyOmega.Mercury.Cli.Sparql | `mercury-sparql` | 2.0.0-preview.1 |
| Mercury.Cli.Turtle | SkyOmega.Mercury.Cli.Turtle | `mercury-turtle` | 2.0.0-preview.1 |
| Mercury.Mcp | SkyOmega.Mercury.Mcp | `mercury-mcp` | 2.0.0-preview.1 |

### 2. Persistent store paths via `MercuryPaths`

New `MercuryPaths.Store(name)` in Mercury.Runtime resolves to:
- **macOS:** `~/Library/SkyOmega/stores/{name}/`
- **Linux/WSL:** `~/.local/share/SkyOmega/stores/{name}/`
- **Windows:** `%LOCALAPPDATA%\SkyOmega\stores\{name}\`

Default stores: `"mcp"` for Mercury.Mcp, `"cli"` for Mercury.Cli.

### 3. Microsoft ModelContextProtocol SDK

Replaced hand-rolled `McpProtocol.cs` (~494 lines) with official `ModelContextProtocol` NuGet package (0.8.0-preview.1). Benefits:
- Protocol compliance maintained by Microsoft
- `[McpServerToolType]` / `[McpServerTool]` attribute-based tool registration
- Hosted service model via `Microsoft.Extensions.Hosting`
- Automatic stdio transport handling

### 4. Claude Code integration

`.mcp.json` at repo root enables dev-time MCP access:
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

For production use across all repos:
```bash
claude mcp add --transport stdio --scope user mercury -- mercury-mcp
```

## Consequences

### Positive
- All tools accessible globally after `./tools/install-tools.sh`
- Persistent semantic memory survives across sessions
- Official MCP SDK handles protocol evolution
- Claude Code can use Mercury as MCP server out of the box

### Negative
- External NuGet dependency for Mercury.Mcp (`ModelContextProtocol`, `Microsoft.Extensions.Hosting`)
- Mercury core library remains BCL-only; only the MCP host adds dependencies

## Files Changed

| File | Action |
|------|--------|
| `src/Mercury.Runtime/IO/MercuryPaths.cs` | Created |
| `src/Mercury.Cli/Mercury.Cli.csproj` | PackAsTool |
| `src/Mercury.Cli.Sparql/Mercury.Cli.Sparql.csproj` | PackAsTool |
| `src/Mercury.Cli.Turtle/Mercury.Cli.Turtle.csproj` | PackAsTool |
| `src/Mercury.Mcp/Mercury.Mcp.csproj` | PackAsTool + NuGet refs |
| `src/Mercury.Cli/Program.cs` | Persistent default |
| `src/Mercury.Mcp/Program.cs` | Hosting model rewrite |
| `src/Mercury.Mcp/McpProtocol.cs` | Deleted |
| `src/Mercury.Mcp/MercuryTools.cs` | Created |
| `src/Mercury.Mcp/Services/PipeServerHostedService.cs` | Created |
| `src/Mercury.Mcp/Services/HttpServerHostedService.cs` | Created |
| `tools/install-tools.sh` | Created |
| `tools/install-tools.ps1` | Created |
| `.mcp.json` | Created |
| `.gitignore` | Added nupkg/ |
