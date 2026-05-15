#!/usr/bin/env bash
# Resume the extended-coverage WDBench chain from where launch-wdbench-extended-coverage.sh
# died (after truthy multiple_bgps completed). NO `set -e` — intermittent dotnet-run
# non-zero exits should NOT stop the chain. Each step logs its own outcome.
#
# Remaining queue:
#   1. truthy opts
#   2. cycle10r4 single_bgps
#   3. cycle10r4 multiple_bgps
#   4. cycle10r4 opts

set -uo pipefail   # NO -e — we want to continue on individual step failures

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

DATE_TAG="$(date -u +%Y-%m-%d)"
TRUTHY_STORE="$HOME/Library/SkyOmega/stores/wiki-truthy-ref-r1"
FULL_STORE="$HOME/Library/SkyOmega/stores/wiki-21b-ref-r4"
QUERIES_BASE="$HOME/Library/SkyOmega/datasets/wdbench/queries"

run_wdbench() {
    local substrate_label="$1"
    local store_path="$2"
    local category="$3"
    local jsonl="docs/validations/wdbench-${category}-${substrate_label}-${DATE_TAG}.jsonl"
    local log="docs/validations/wdbench-${category}-${substrate_label}-${DATE_TAG}.log"

    echo "=== ${substrate_label} / ${category} ==="
    echo "  started: $(date -u +%FT%TZ)"

    # Skip if the JSONL already has a summary (resumes are idempotent at the run-boundary)
    if [ -f "$jsonl" ] && grep -q '"phase":"wdbench_summary"' "$jsonl" 2>/dev/null; then
        echo "  ⏭  $jsonl already has a wdbench_summary — skipping"
        echo
        return 0
    fi

    dotnet run --project benchmarks/Mercury.Benchmarks -c Release --no-build -- wdbench \
        --store "$store_path" \
        --queries "$QUERIES_BASE/$category" \
        --metrics-out "$jsonl" \
        --timeout 60 > "$log" 2>&1
    local rc=$?
    echo "  finished: $(date -u +%FT%TZ)  (exit code $rc)"

    # Verify the summary fired regardless of exit code — wdbench may exit non-zero
    # after clean completion (intermittent, root-cause unidentified 2026-05-14/15).
    if grep -q '"phase":"wdbench_summary"' "$jsonl" 2>/dev/null; then
        echo "  ✅ wdbench_summary present — treating as success"
    else
        echo "  ❌ NO wdbench_summary — this run failed mid-flight"
    fi
    echo
    tail -10 "$log"
    echo
}

# Queue
run_wdbench "truthy"    "$TRUTHY_STORE"  opts
run_wdbench "cycle10r4" "$FULL_STORE"    single_bgps
run_wdbench "cycle10r4" "$FULL_STORE"    multiple_bgps
run_wdbench "cycle10r4" "$FULL_STORE"    opts

echo "=== resume chain complete ==="
date -u +%FT%TZ
