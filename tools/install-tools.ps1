# Install Sky Omega tools as .NET global tools from local source.
# Usage: .\tools\install-tools.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$NupkgDir = Join-Path $ScriptDir "nupkg"

Write-Host "Building and packing Sky Omega tools..."
# Clean both configurations: out-of-solution ProjectReferences (the legacy VSTest
# integration target) can leave stale Debug obj/ state that breaks subsequent
# Release builds with CS0006 "ref/*.dll could not be found".
dotnet clean (Join-Path $ScriptDir "SkyOmega.sln") -c Release -v quiet
dotnet clean (Join-Path $ScriptDir "SkyOmega.sln") -c Debug   -v quiet
# -m:1 serializes project builds — avoids the ref-assembly race on cold builds.
dotnet build (Join-Path $ScriptDir "SkyOmega.sln") -c Release -m:1
dotnet pack (Join-Path $ScriptDir "SkyOmega.sln") -c Release --no-build -o $NupkgDir -p:PackageReadmeFile=""

Write-Host ""
Write-Host "Installing tools..."

$packages = @(
    "SkyOmega.Mercury.Cli",
    "SkyOmega.Mercury.Cli.Sparql",
    "SkyOmega.Mercury.Cli.Turtle",
    "SkyOmega.Mercury.Mcp",
    "SkyOmega.DrHook.Mcp"
)

foreach ($pkg in $packages) {
    Write-Host "  $pkg"
    try {
        dotnet tool install -g $pkg --prerelease --add-source $NupkgDir 2>$null
    } catch {
        dotnet tool update -g $pkg --prerelease --add-source $NupkgDir
    }
}

Write-Host ""
Write-Host "Done. Available commands:"
Write-Host "  mercury         - SPARQL CLI with persistent store"
Write-Host "  mercury-sparql  - SPARQL query engine demo"
Write-Host "  mercury-turtle  - Turtle parser demo"
Write-Host "  mercury-mcp     - MCP server for Claude"
Write-Host "  drhook-mcp      - MCP server for .NET runtime inspection"
