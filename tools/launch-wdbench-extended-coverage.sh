#!/usr/bin/env bash
set -euo pipefail

# Run the three WDBench categories (single_bgps, multiple_bgps, opts) that
# were silently excluded from cycle 8 → cycle 9 → cycle 10 r4 → truthy r1
# WDBench cold baselines. Those cycles ran only paths + c2rpqs — a property-
# path-focused scope from cycle 8 that drifted into inertia.
#
# This script closes the coverage gap by running the three skipped categories
# against BOTH substrates (truthy r1 and cycle 10 r4 full) sequentially.
# Substrates are read-only during these runs; no risk of cross-corruption.
#
# Sequence:
#   1. Wait for any currently-running wdbench process to exit (PID-aware)
#   2. truthy r1 × {single_bgps, multiple_bgps, opts}
#   3. cycle 10 r4 full × {single_bgps, multiple_bgps, opts}
#
# Total: 6 wdbench invocations. Expected compute ~15-25h depending on
# category complexity and substrate scale. Each invocation writes its own
# JSONL artifact for clean per-run analysis.
#
# DISCIPLINE: no `dotnet tool` operations during the run (.NET lazy-load
# assembly failure mode).
#
# Usage: ./tools/launch-wdbench-extended-coverage.sh [WAIT_PID]
#   WAIT_PID — optional. If given, wait for this PID to exit before starting.
#              Use to chain after the current c2rpqs run.

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

DATE_TAG="$(date -u +%Y-%m-%d)"
TRUTHY_STORE="$HOME/Library/SkyOmega/stores/wiki-truthy-ref-r1"
FULL_STORE="$HOME/Library/SkyOmega/stores/wiki-21b-ref-r4"
QUERIES_BASE="$HOME/Library/SkyOmega/datasets/wdbench/queries"

if [ $# -ge 1 ]; then
    WAIT_PID="$1"
    echo "Waiting for PID $WAIT_PID to exit before starting..."
    while kill -0 "$WAIT_PID" 2>/dev/null; do sleep 30; done
    echo "PID $WAIT_PID exited. Proceeding."
fi

# Preflight: substrates exist + queryable
for store in "$TRUTHY_STORE" "$FULL_STORE"; do
    if [ ! -d "$store" ]; then
        echo "ERROR: store not found: $store" >&2
        exit 1
    fi
done

# Preflight: no other wdbench in flight
if pgrep -f "Mercury.Benchmarks.*wdbench" > /dev/null; then
    echo "ERROR: another wdbench process is running. Refusing to overlap." >&2
    pgrep -lf "Mercury.Benchmarks.*wdbench" >&2
    exit 2
fi

# Preflight: mercury at expected version
MERCURY_VERSION_LINE="$(mercury --version)"
case "$MERCURY_VERSION_LINE" in
    "mercury 1.7.57"*) ;;
    *) echo "ERROR: expected mercury 1.7.57+, got '$MERCURY_VERSION_LINE'." >&2; exit 3 ;;
esac
echo "mercury: $MERCURY_VERSION_LINE"
echo

run_wdbench() {
    local substrate_label="$1"   # e.g. "truthy" or "cycle10r4"
    local store_path="$2"
    local category="$3"          # single_bgps / multiple_bgps / opts
    local jsonl="docs/validations/wdbench-${category}-${substrate_label}-${DATE_TAG}.jsonl"
    local log="docs/validations/wdbench-${category}-${substrate_label}-${DATE_TAG}.log"

    echo "=== ${substrate_label} / ${category} ==="
    echo "  store:   $store_path"
    echo "  queries: $QUERIES_BASE/$category"
    echo "  jsonl:   $jsonl"
    echo "  log:     $log"
    echo "  started: $(date -u +%FT%TZ)"
    echo

    dotnet run --project benchmarks/Mercury.Benchmarks -c Release --no-build -- wdbench \
        --store "$store_path" \
        --queries "$QUERIES_BASE/$category" \
        --metrics-out "$jsonl" \
        --timeout 60 > "$log" 2>&1

    echo "  finished: $(date -u +%FT%TZ)"
    echo
    tail -10 "$log"
    echo
}

# truthy substrate first (smaller, faster)
for cat in single_bgps multiple_bgps opts; do
    run_wdbench "truthy" "$TRUTHY_STORE" "$cat"
done

# then cycle 10 r4 full substrate
for cat in single_bgps multiple_bgps opts; do
    run_wdbench "cycle10r4" "$FULL_STORE" "$cat"
done

echo "=== all extended-coverage WDBench runs complete ==="
date -u +%FT%TZ
