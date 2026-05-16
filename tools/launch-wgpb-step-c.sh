#!/usr/bin/env bash
# Step C of the Wikidata-publication arc: WGPB (Wikidata Graph Pattern Benchmark)
# from MillenniumDB / Zenodo 4035223.
#
# Sequence (all detached, sequential — no overlap with each other):
#   1. Wait for wikidata-wcg-filtered.nt.bz2 download to complete
#   2. Split bgps/*.txt (17 files, ~50 queries each) into per-query .sparql files
#      under queries/{pattern}/00001.sparql, ... — same shape as the WDBench
#      harness expects
#   3. Bulk-load wikidata-wcg-filtered.nt.bz2 into wiki-wgpb-ref-r1 substrate
#      (1.7.57 Reference profile, same flags as cycle 10 r4 + truthy r1)
#   4. Rebuild-indexes (GPOS + trigram)
#   5. Run wdbench harness against each of the 17 WGPB categories
#
# Defensive design: NO set -e. Each step checks success via observable
# artifact (file exists, summary present, etc.) rather than exit code.
#
# Usage: ./tools/launch-wgpb-step-c.sh [DOWNLOAD_PID]

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

DATE_TAG="$(date -u +%Y-%m-%d)"
WGPB_DIR="$HOME/Library/SkyOmega/datasets/wgpb"
WGPB_BZ2="$WGPB_DIR/wikidata-wcg-filtered.nt.bz2"
WGPB_QUERIES_DIR="$WGPB_DIR/queries"
STORE_NAME=wiki-wgpb-ref-r1
STORE_PATH="$HOME/Library/SkyOmega/stores/$STORE_NAME"

WGPB_BULK_JSONL="docs/validations/wgpb-bulk-${DATE_TAG}.jsonl"
WGPB_REBUILD_JSONL="docs/validations/wgpb-rebuild-indexes-${DATE_TAG}.jsonl"
WGPB_LOG="docs/validations/wgpb-${DATE_TAG}.log"
WGPB_MASTER_LOG="docs/validations/wgpb-step-c-${DATE_TAG}.log"

# ---------------------------------------------------------------------------
# STEP 1: wait for download to complete
# ---------------------------------------------------------------------------
if [ $# -ge 1 ]; then
    DOWNLOAD_PID="$1"
    echo "=== STEP 1: waiting for download PID $DOWNLOAD_PID to complete ==="
    while kill -0 "$DOWNLOAD_PID" 2>/dev/null; do
        sleep 30
        size=$(stat -f%z "$WGPB_BZ2" 2>/dev/null || echo 0)
        size_mb=$(awk -v s="$size" 'BEGIN{printf "%.0f", s/1024/1024}')
        echo "  ($(date -u +%H:%M:%S))  downloaded ${size_mb} MB"
    done
fi

# Verify download size + integrity
if [ ! -f "$WGPB_BZ2" ]; then
    echo "ERROR: $WGPB_BZ2 not found." >&2
    exit 1
fi
SIZE=$(stat -f%z "$WGPB_BZ2")
EXPECTED=622657618  # 593.7 MB approximately
if [ "$SIZE" -lt 500000000 ]; then
    echo "ERROR: download appears incomplete: ${SIZE} bytes (expected ~593 MB)" >&2
    exit 2
fi
echo "  download: $(ls -lh $WGPB_BZ2 | awk '{print $5}')"
echo

# ---------------------------------------------------------------------------
# STEP 2: split bgps/*.txt into per-query .sparql files
# ---------------------------------------------------------------------------
echo "=== STEP 2: splitting WGPB queries into per-query .sparql files ==="
mkdir -p "$WGPB_QUERIES_DIR"
python3 - <<EOF
import os, glob
BGPS = "$WGPB_DIR/bgps"
OUT = "$WGPB_QUERIES_DIR"
total = 0
for txt in sorted(glob.glob(os.path.join(BGPS, "*.txt"))):
    cat = os.path.splitext(os.path.basename(txt))[0]
    out_dir = os.path.join(OUT, cat)
    os.makedirs(out_dir, exist_ok=True)
    with open(txt, "r", encoding="utf-8") as f:
        lines = [line.rstrip("\n\r") for line in f if line.strip()]
    for i, query in enumerate(lines, start=1):
        with open(os.path.join(out_dir, f"{i:05d}.sparql"), "w", encoding="utf-8") as out:
            out.write(query + "\n")
        total += 1
    print(f"  {cat}: {len(lines)} queries")
print(f"  TOTAL: {total} queries across $(ls $BGPS | wc -l | tr -d ' ') categories")
EOF
echo

# ---------------------------------------------------------------------------
# STEP 3 + 4: bulk-load + rebuild-indexes
# ---------------------------------------------------------------------------
if [ -e "$STORE_PATH" ]; then
    echo "  store $STORE_PATH already exists — skipping bulk-load+rebuild"
else
    echo "=== STEP 3+4: bulk-load + rebuild-indexes ==="
    MERCURY_VERSION="$(mercury --version)"
    case "$MERCURY_VERSION" in
        "mercury 1.7.57"*) ;;
        *)
            echo "ERROR: expected mercury 1.7.57+, got '$MERCURY_VERSION'" >&2
            exit 3
            ;;
    esac
    echo "  mercury: $MERCURY_VERSION"
    echo "  source:  $WGPB_BZ2 ($(ls -lh $WGPB_BZ2 | awk '{print $5}'))"
    echo "  store:   $STORE_PATH (will be created)"
    echo "  bulk:    $WGPB_BULK_JSONL"
    echo "  rebuild: $WGPB_REBUILD_JSONL"
    echo "  log:     $WGPB_LOG"
    echo "  started: $(date -u +%FT%TZ)"

    mercury --store "$STORE_NAME" --bulk-load "$WGPB_BZ2" --profile Reference \
            --min-free-space 100 --metrics-out "$WGPB_BULK_JSONL" \
            --metrics-state-interval 30 --no-http --no-repl >> "$WGPB_LOG" 2>&1
    BULK_RC=$?
    echo "  bulk-load finished: $(date -u +%FT%TZ)  (exit code $BULK_RC)"

    if grep -q '"phase":"load.summary"' "$WGPB_BULK_JSONL" 2>/dev/null; then
        echo "  ✅ load.summary present — bulk-load complete"
    else
        echo "  ❌ NO load.summary — bulk-load failed mid-flight"
        exit 4
    fi

    mercury --store "$STORE_NAME" --rebuild-indexes \
            --metrics-out "$WGPB_REBUILD_JSONL" \
            --metrics-state-interval 30 --no-http --no-repl >> "$WGPB_LOG" 2>&1
    REBUILD_RC=$?
    echo "  rebuild-indexes finished: $(date -u +%FT%TZ)  (exit code $REBUILD_RC)"

    if grep -q '"phase":"rebuild_complete"' "$WGPB_REBUILD_JSONL" 2>/dev/null; then
        echo "  ✅ rebuild_complete present"
    else
        echo "  ❌ NO rebuild_complete — rebuild failed"
        exit 5
    fi
    echo
fi

# ---------------------------------------------------------------------------
# STEP 5: run wdbench harness against each of the 17 WGPB categories
# ---------------------------------------------------------------------------
echo "=== STEP 5: running WGPB queries (17 categories) ==="
for cat_dir in "$WGPB_QUERIES_DIR"/*/; do
    cat=$(basename "$cat_dir")
    jsonl="docs/validations/wgpb-${cat}-${DATE_TAG}.jsonl"
    log="docs/validations/wgpb-${cat}-${DATE_TAG}.log"

    echo "=== WGPB / ${cat} ==="
    echo "  started: $(date -u +%FT%TZ)"

    # idempotent: skip if already complete
    if [ -f "$jsonl" ] && grep -q '"phase":"wdbench_summary"' "$jsonl" 2>/dev/null; then
        echo "  ⏭  $jsonl already has wdbench_summary — skipping"
        continue
    fi

    dotnet run --project benchmarks/Mercury.Benchmarks -c Release --no-build -- wdbench \
        --store "$STORE_PATH" \
        --queries "$cat_dir" \
        --metrics-out "$jsonl" \
        --timeout 60 > "$log" 2>&1
    rc=$?
    echo "  finished: $(date -u +%FT%TZ)  (exit code $rc)"

    if grep -q '"phase":"wdbench_summary"' "$jsonl" 2>/dev/null; then
        echo "  ✅ wdbench_summary present"
        tail -8 "$log"
    else
        echo "  ❌ NO wdbench_summary — this category failed"
    fi
    echo
done

echo "=== step C / WGPB complete ==="
date -u +%FT%TZ
