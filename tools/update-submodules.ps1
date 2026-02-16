# Initialize and update git submodules (W3C conformance test data).
# Usage: .\tools\update-submodules.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "Updating git submodules..."
git -C $ScriptDir submodule update --init --recursive

Write-Host ""
Write-Host "Done. Submodules initialized:"
Write-Host "  tests/w3c-rdf-tests     - W3C RDF conformance test data"
Write-Host "  tests/w3c-json-ld-api   - W3C JSON-LD conformance test data"
