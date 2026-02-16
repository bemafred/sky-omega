#!/usr/bin/env bash
set -euo pipefail

# Initialize and update git submodules (W3C conformance test data).
# Usage: ./tools/update-submodules.sh

SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

echo "Updating git submodules..."
git -C "$SCRIPT_DIR" submodule update --init --recursive

echo ""
echo "Done. Submodules initialized:"
echo "  tests/w3c-rdf-tests     - W3C RDF conformance test data"
echo "  tests/w3c-json-ld-api   - W3C JSON-LD conformance test data"
