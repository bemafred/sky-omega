#!/usr/bin/env bash
set -euo pipefail

# Launch Cycle 10 Phase 3 — 21.3 B Wikidata Reference + Sorted bulk-load on 1.7.55.
# Substrate fixes in this cycle: ADR-037 pipelined spill (1.7.50), B1 prefix-compress
# intermediate chunks + B2 readahead (1.7.52/1.7.54), B3 BBHash MPHF with
# MaxLevels=40 + dense final-level fallback (1.7.55), --rebuild-mphf recovery
# surface. Cycle 9 baseline 35h35m end-to-end on 1.7.50.
#
# Prior attempts (preserved as docs/validations/*aborted-1.7.5{3,4}.jsonl):
#   1.7.52: int32 overflow in BBHashBuilder at ~4 B atoms     → fixed 1.7.53 (SkyOmega.Bcl)
#   1.7.54: BBHash non-convergence after 24 iterative levels  → fixed 1.7.55 (40 + dense)
#
# Usage: ./tools/launch-cycle10-phase3.sh
# Behavior: refuses to launch if substrate is not in clean state. Exits with
# clear diagnostic on each precondition failure; only spawns the background
# bulk-load+rebuild-indexes chain when all checks pass.

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

STORE_NAME=wiki-21b-ref-r3
STORE_PATH="$HOME/Library/SkyOmega/stores/$STORE_NAME"
SOURCE="$HOME/Library/SkyOmega/datasets/wikidata/full/latest-all.ttl.bz2"
DATE_TAG="$(date -u +%Y-%m-%d)"

BULK_JSONL="docs/validations/cycle10-phase3-21b-bulk-${DATE_TAG}.jsonl"
REBUILD_JSONL="docs/validations/cycle10-phase3-21b-rebuild-indexes-${DATE_TAG}.jsonl"
RUN_LOG="docs/validations/cycle10-phase3-21b-${DATE_TAG}.log"

echo "=== Cycle 10 Phase 3 launch preflight ($(date -u +%FT%TZ)) ==="

# 1. Verify mercury tool is installed and at expected version line.
if ! command -v mercury >/dev/null 2>&1; then
    echo "ERROR: 'mercury' not on PATH. Run ./tools/install-tools.sh first." >&2
    exit 1
fi
MERCURY_VERSION_LINE="$(mercury --version)"
echo "  mercury: $MERCURY_VERSION_LINE"
case "$MERCURY_VERSION_LINE" in
    "mercury 1.7.55"*) ;;
    *)
        echo "ERROR: expected mercury 1.7.55+, got '$MERCURY_VERSION_LINE'." >&2
        echo "       Run ./tools/install-tools.sh to update global tool." >&2
        exit 2
        ;;
esac

# 2. Verify source dataset exists.
if [ ! -f "$SOURCE" ]; then
    echo "ERROR: source dataset not found at $SOURCE" >&2
    exit 3
fi
SOURCE_SIZE_HUMAN="$(ls -lh "$SOURCE" | awk '{print $5}')"
echo "  source:  $SOURCE ($SOURCE_SIZE_HUMAN)"

# 3. Verify target store does NOT exist — refuse to clobber an existing run.
if [ -e "$STORE_PATH" ]; then
    echo "ERROR: target store $STORE_PATH already exists." >&2
    echo "       Delete it explicitly before relaunching: rm -rf $STORE_PATH" >&2
    exit 4
fi
echo "  store:   $STORE_PATH (will be created)"

# 4. Verify free disk — cycle 9 peaked at ~6 TB; require 5.5 TiB minimum headroom.
FREE_TB="$(df -k "$HOME/Library/SkyOmega" | tail -1 | awk '{printf "%.1f", $4/1024/1024/1024}')"
MIN_FREE_TIB=5.5
echo "  disk:    ${FREE_TB} TiB free (min required: ${MIN_FREE_TIB} TiB)"
if awk -v f="$FREE_TB" -v m="$MIN_FREE_TIB" 'BEGIN { exit (f >= m) ? 0 : 1 }'; then :; else
    echo "ERROR: insufficient free disk (${FREE_TB} TiB < ${MIN_FREE_TIB} TiB)." >&2
    exit 5
fi

# 5. Verify no other foreground mercury bulk-load is in flight against this store.
if pgrep -lf "mercury.*--store $STORE_NAME" | grep -v "mercury-mcp" | grep -q "."; then
    echo "ERROR: another mercury process is already targeting $STORE_NAME:" >&2
    pgrep -lf "mercury.*--store $STORE_NAME" | grep -v "mercury-mcp" >&2
    exit 6
fi

# All preconditions pass — launch the bulk-load + rebuild-indexes chain.
echo
echo "=== Preflight passed. Launching detached. ==="
echo "  bulk metrics:    $BULK_JSONL"
echo "  rebuild metrics: $REBUILD_JSONL"
echo "  run log:         $RUN_LOG (gitignored)"
echo

nohup bash -c "mercury --store $STORE_NAME --bulk-load $SOURCE --profile Reference --min-free-space 100 --metrics-out $BULK_JSONL --metrics-state-interval 30 --no-http --no-repl && mercury --store $STORE_NAME --rebuild-indexes --metrics-out $REBUILD_JSONL --metrics-state-interval 30 --no-http --no-repl" > "$RUN_LOG" 2>&1 &
disown
PID=$!

echo "Phase 3 launched with PID $PID."
echo
echo "Cycle 9 baseline: 35 h 35 m end-to-end."
echo "Cycle 10 expected: cycle 9 + ~55 min MPHF − any wins from B1/B2/B3 (this run measures them)."
echo
echo "Tail progress with:"
echo "  tail -f $RUN_LOG"
echo
echo "Check process state with:"
echo "  ps -p $PID -o pid,etime,rss,pcpu"
