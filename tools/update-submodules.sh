#!/usr/bin/env bash
set -euo pipefail

# Initialize and update git submodules (W3C conformance test data).
# Usage: ./tools/update-submodules.sh

SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

echo "Updating git submodules..."
git -C "$SCRIPT_DIR" submodule update --init --recursive

echo ""
echo "Done. Submodules initialized:"
git -C "$SCRIPT_DIR" submodule foreach --quiet 'echo "  $sm_path"'
