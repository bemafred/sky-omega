#!/usr/bin/env bash
set -euo pipefail

# Install Sky Omega tools as .NET global tools from local source.
# Usage: ./tools/install-tools.sh

SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
NUPKG_DIR="$SCRIPT_DIR/nupkg"

echo "Building and packing Sky Omega tools..."
dotnet pack "$SCRIPT_DIR/SkyOmega.sln" -c Release -o "$NUPKG_DIR"

echo ""
echo "Installing tools..."

for pkg in SkyOmega.Mercury.Cli SkyOmega.Mercury.Cli.Sparql \
           SkyOmega.Mercury.Cli.Turtle SkyOmega.Mercury.Mcp; do
    echo "  $pkg"
    dotnet tool install -g "$pkg" --add-source "$NUPKG_DIR" 2>/dev/null \
        || dotnet tool update -g "$pkg" --add-source "$NUPKG_DIR"
done

echo ""
echo "Done. Available commands:"
echo "  mercury         - SPARQL CLI with persistent store"
echo "  mercury-sparql  - SPARQL query engine demo"
echo "  mercury-turtle  - Turtle parser demo"
echo "  mercury-mcp     - MCP server for Claude"
