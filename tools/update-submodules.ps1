# Initialize and update git submodules (W3C conformance test data).
# Usage: .\tools\update-submodules.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "Updating git submodules..."
git -C $ScriptDir submodule update --init --recursive

Write-Host ""
Write-Host "Done. Submodules initialized:"
git -C $ScriptDir submodule foreach --quiet 'echo "  $sm_path"' | ForEach-Object { Write-Host $_ }
