# Initialize and update git submodules (W3C conformance test data).
# Usage: .\tools\update-submodules.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "Updating git submodules..."
git -C $ScriptDir submodule update --init --recursive

Write-Host ""
Write-Host "Done. Submodules initialized:"
# No quotes nested in git's argument: Windows PowerShell 5.1 mangles them when calling a native program,
# and the list printed "$sm_path" literally. The indent is added here instead.
git -C $ScriptDir submodule foreach --quiet 'echo $sm_path' | ForEach-Object { Write-Host "  $_" }
