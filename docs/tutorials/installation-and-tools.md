# Installation and Tools

A comprehensive guide to installing, updating, and managing the Mercury tool
suite. Covers install scripts, manual installation, platform-specific store
locations, port conventions, and troubleshooting.

> **Just want to get started?** The [Getting Started](getting-started.md)
> tutorial covers the quick-install happy path. This tutorial covers the full
> tool lifecycle.

---

## What Gets Installed

Mercury ships four .NET global tools:

| Command | Package ID | Description |
|---------|------------|-------------|
| `mercury` | `SkyOmega.Mercury.Cli` | Interactive SPARQL REPL with persistent store and HTTP endpoint |
| `mercury-mcp` | `SkyOmega.Mercury.Mcp` | MCP server for Claude with persistent store |
| `mercury-sparql` | `SkyOmega.Mercury.Cli.Sparql` | Batch SPARQL query tool for scripting |
| `mercury-turtle` | `SkyOmega.Mercury.Cli.Turtle` | Turtle validation, conversion, benchmarking |

All four are installed as .NET global tools via `dotnet tool install -g`.

---

## Installing from Source

### Using the install script

The install script builds the solution, packs NuGet packages, and installs
(or updates) all four tools:

```bash
# macOS / Linux
./tools/install-tools.sh

# Windows (PowerShell)
.\tools\install-tools.ps1
```

### What the script does

1. Runs `dotnet pack SkyOmega.sln -c Release -o ./nupkg` to build and package
   all tool projects
2. For each of the four tool packages, runs `dotnet tool install -g` (or
   `dotnet tool update -g` if already installed) using the local `nupkg/`
   directory as the package source

### Manual installation

If you prefer to install manually, or need to install a single tool:

```bash
# Build and pack
dotnet pack SkyOmega.sln -c Release -o ./nupkg

# Install a specific tool
dotnet tool install -g SkyOmega.Mercury.Cli --prerelease --add-source ./nupkg
```

Replace `SkyOmega.Mercury.Cli` with any of the four package IDs listed above.

---

## Updating Tools

After pulling new code from the repository, re-run the install script:

```bash
git pull
./tools/install-tools.sh
```

The script handles updates automatically -- it tries `install` first, falls
back to `update` if the tool is already installed.

To update a single tool manually:

```bash
dotnet pack SkyOmega.sln -c Release -o ./nupkg
dotnet tool update -g SkyOmega.Mercury.Cli --prerelease --add-source ./nupkg
```

---

## Uninstalling

Remove individual tools:

```bash
dotnet tool uninstall -g SkyOmega.Mercury.Cli
dotnet tool uninstall -g SkyOmega.Mercury.Cli.Sparql
dotnet tool uninstall -g SkyOmega.Mercury.Cli.Turtle
dotnet tool uninstall -g SkyOmega.Mercury.Mcp
```

This removes the commands but does not delete store data. See
[Store Locations](#store-locations) to locate and remove stored data.

---

## Store Locations

Each tool that uses a persistent store resolves its path via
`MercuryPaths.Store(name)`, which maps to the platform's local application data
directory:

| Platform | Base path | CLI store | MCP store |
|----------|-----------|-----------|-----------|
| macOS | `~/Library/SkyOmega/stores/` | `~/Library/SkyOmega/stores/cli/` | `~/Library/SkyOmega/stores/mcp/` |
| Linux | `~/.local/share/SkyOmega/stores/` | `~/.local/share/SkyOmega/stores/cli/` | `~/.local/share/SkyOmega/stores/mcp/` |
| Windows | `%LOCALAPPDATA%\SkyOmega\stores\` | `%LOCALAPPDATA%\SkyOmega\stores\cli\` | `%LOCALAPPDATA%\SkyOmega\stores\mcp\` |

The store directory is created automatically on first use. It contains
memory-mapped B+Tree indexes, an atom store, and a write-ahead log.

Both `mercury` and `mercury-sparql` also support custom store paths:

```bash
mercury ./my-project-data
mercury-sparql --store ./my-project-data --query "..."
```

---

## Port Conventions

Tools that expose HTTP endpoints use these default ports:

| Tool | Default port | Endpoint |
|------|-------------|----------|
| `mercury-mcp` | 3030 | `http://localhost:3030/sparql` |
| `mercury` | 3031 | `http://localhost:3031/sparql` |

Override the port with `--port` (or `-p`):

```bash
mercury -p 8080
```

Named pipes for inter-process communication:

| Tool | Pipe name |
|------|-----------|
| `mercury-mcp` | `mercury-mcp` |
| `mercury` | `mercury-cli` |

---

## Verifying Installation

### Version checks

```bash
mercury --version
mercury-sparql --version
mercury-turtle --version
mercury-mcp --version
```

Each command prints its version and exits.

### Store test

Verify the CLI can create and query a store:

```bash
mercury -m
```

At the `mercury>` prompt, type:

```sparql
INSERT DATA { <http://example.org/test> <http://example.org/status> "ok" }
```

```sparql
SELECT * WHERE { ?s ?p ?o }
```

You should see one result. Type `:quit` to exit. The temporary store is deleted.

### HTTP health

While `mercury` is running, verify the HTTP endpoint from another terminal:

```bash
curl -s http://localhost:3031/sparql \
  -G --data-urlencode "query=ASK { ?s ?p ?o }"
```

A JSON response with `"boolean": true` (if data exists) or `"boolean": false`
(if the store is empty) confirms the endpoint is working.

---

## Troubleshooting

### Command not found

If `mercury` is not recognized after installation, the .NET tools directory is
not in your PATH.

**macOS / Linux:**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

Add this line to your shell profile (`~/.bashrc`, `~/.zshrc`, or equivalent)
to make it permanent.

**Windows:**

The .NET SDK installer typically adds `%USERPROFILE%\.dotnet\tools` to PATH.
If not, add it manually via System Properties > Environment Variables.

### Port conflicts

If Mercury fails to start with a port error, another process is using the
default port:

```bash
# Check what's using port 3031
lsof -i :3031    # macOS/Linux
netstat -ano | findstr 3031   # Windows
```

Use `--port` to pick a different port, or `--no-http` to disable the HTTP
endpoint entirely:

```bash
mercury --port 9090
mercury --no-http
```

### Store permissions

If Mercury fails to open a store, check directory permissions:

```bash
ls -la ~/Library/SkyOmega/stores/cli/
```

The store directory and its contents must be readable and writable by your user.

### Stale store after upgrade

If Mercury fails to open a store after a major upgrade, the store format may
have changed. Back up the old store and start fresh:

```bash
mv ~/Library/SkyOmega/stores/cli/ ~/Library/SkyOmega/stores/cli-backup/
mercury   # creates a fresh store
```

---

## See Also

- [Getting Started](getting-started.md) -- quick-start walkthrough
- [Mercury CLI](mercury-cli.md) -- interactive REPL reference
- [Mercury SPARQL CLI](mercury-sparql-cli.md) -- batch queries and scripting
- [Mercury Turtle CLI](mercury-turtle-cli.md) -- validation and conversion
- [Mercury MCP Server](mercury-mcp.md) -- Claude integration
