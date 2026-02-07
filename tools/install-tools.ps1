# Install Sky Omega tools as .NET global tools from local source.
# Usage: .\tools\install-tools.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$NupkgDir = Join-Path $ScriptDir "nupkg"

Write-Host "Building and packing Sky Omega tools..."
dotnet pack (Join-Path $ScriptDir "SkyOmega.sln") -c Release -o $NupkgDir

Write-Host ""
Write-Host "Installing tools..."

$packages = @(
    "SkyOmega.Mercury.Cli",
    "SkyOmega.Mercury.Cli.Sparql",
    "SkyOmega.Mercury.Cli.Turtle",
    "SkyOmega.Mercury.Mcp"
)

foreach ($pkg in $packages) {
    Write-Host "  $pkg"
    try {
        dotnet tool install -g $pkg --add-source $NupkgDir 2>$null
    } catch {
        dotnet tool update -g $pkg --add-source $NupkgDir
    }
}

Write-Host ""
Write-Host "Done. Available commands:"
Write-Host "  mercury         - SPARQL CLI with persistent store"
Write-Host "  mercury-sparql  - SPARQL query engine demo"
Write-Host "  mercury-turtle  - Turtle parser demo"
Write-Host "  mercury-mcp     - MCP server for Claude"
