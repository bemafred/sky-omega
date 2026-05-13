#!/usr/bin/env bash
set -euo pipefail

# Launch Mercury 1.7.57 truthy-Wikidata bulk-load + rebuild against the
# substrate generation already production-validated on full Wikidata
# (cycle 10 Phase 3 r4, see docs/validations/cycle10-phase3-r4-21b-2026-05-12.md).
#
# This is the truthy-subset companion measurement to the full run. Same substrate
# version, same CLI flag set, same metric instrumentation. The expected use is
# external Wikidata-community comparison vs published WDBench / QLever / Virtuoso
# numbers (which use the truthy subset, not full) — per
# docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md.
#
# Usage: ./tools/launch-truthy-bulk.sh
# Behavior: refuses to launch unless preflight passes. Detached via nohup; the
# bulk-load + rebuild-indexes chain runs uninterrupted.
#
# DISCIPLINE (HARD RULE): NO `dotnet tool` operations on mercury while this
# chain is active. .NET lazy-loads referenced assemblies; replacing the
# on-disk tool image breaks the running process at the next lazy-load.

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

STORE_NAME=wiki-truthy-ref-r1
STORE_PATH="$HOME/Library/SkyOmega/stores/$STORE_NAME"
SOURCE="$HOME/Library/SkyOmega/datasets/wikidata/truthy/latest-truthy.nt.bz2"
DATE_TAG="$(date -u +%Y-%m-%d)"

BULK_JSONL="docs/validations/truthy-bulk-${DATE_TAG}.jsonl"
REBUILD_JSONL="docs/validations/truthy-rebuild-indexes-${DATE_TAG}.jsonl"
RUN_LOG="docs/validations/truthy-${DATE_TAG}.log"

echo "=== Truthy bulk-load launch preflight ($(date -u +%FT%TZ)) ==="

# 1. Verify mercury tool is installed and at expected version line.
if ! command -v mercury >/dev/null 2>&1; then
    echo "ERROR: 'mercury' not on PATH. Run ./tools/install-tools.sh first." >&2
    exit 1
fi
MERCURY_VERSION_LINE="$(mercury --version)"
echo "  mercury: $MERCURY_VERSION_LINE"
case "$MERCURY_VERSION_LINE" in
    "mercury 1.7.57"*) ;;
    *)
        echo "ERROR: expected mercury 1.7.57+, got '$MERCURY_VERSION_LINE'." >&2
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

# 4. Verify free disk — truthy is ~0.67× full Wikidata, so peak working set
# is proportionally smaller (~3.5 TiB vs full's ~5.5 TiB). Require 2.5 TiB
# minimum headroom — still comfortable for the merge phase + intermediate
# chunks before cleanup-hook reclaim at end-of-merge.
FREE_TB="$(df -k "$HOME/Library/SkyOmega" | tail -1 | awk '{printf "%.1f", $4/1024/1024/1024}')"
MIN_FREE_TIB=2.5
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

echo "Truthy bulk-load launched with PID $PID."
echo
echo "Expected ETA (scaled from cycle 10 r4's 23h57m at full Wikidata):"
echo "  parse:           ~6h (vs 9h17m at full)"
echo "  merge + MPHF:    ~2h 30m (vs 3h35m at full)"
echo "  drain + rebuild: ~6h (vs 10h45m at full)"
echo "  total:           ~14h 30m (rough estimate at ~0.65× full scale)"
echo
echo "Tail progress with:"
echo "  tail -f $RUN_LOG"
echo
echo "Check process state with:"
echo "  ps -p $PID -o pid,etime,rss,pcpu"
