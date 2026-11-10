#!/usr/bin/env bash
# Watches worker 2 in run C (20261110T181008Z) of probe 108. Read-only: runs the read script, plus a listing of who
# holds each clr-debug-pipe, at two moments, then exits.
#   early-w2-<pid>.txt  worker 2 exists but has written no marks yet (held before its own managed code)
#   held-worker2.txt    worker 2 has written its facts but not its checkpoint for over 2 s (stopped at line 72)
# Exits when worker 2 is held, when the run ends, or after 20 minutes.
set -u
probe=/Users/bemafred/src/repos/sky-omega/poc/drhook-engine
run=$probe/108-rider-child-hold/bin/Debug/net10.0/marks/20261110T181008Z
out=/private/tmp/claude-501/-Users-bemafred-src-repos-sky-omega/f163c189-105f-49c1-99d2-69ca8fdbb95a/scratchpad/probe108-c
mkdir -p "$out"

snapshot() {
    "$probe/108-rider-child-hold-read.sh"
    echo
    echo "== clr-debug-pipe files held open, by process"
    for p in $(pgrep -f 'JetBrains.Debugger.Worker') $(pgrep -f RiderChildHold); do
        lsof -nP -p "$p" 2>/dev/null | awk -v p="$p" '$NF ~ /clr-debug-pipe/ { print "   " $1 " pid " p " fd " $4 "  " $NF }'
    done
}

early_done=""
since=""
deadline=$(( $(date +%s) + 1200 ))
echo "watching worker 2 of $(basename "$run") from $(date -u +%H:%M:%SZ)"
while [ "$(date +%s)" -lt "$deadline" ]; do
    if grep -q '\] exit (' "$run/"*-supervisor.txt 2>/dev/null; then
        echo "run ended (supervisor exit written) at $(date -u +%H:%M:%SZ)"
        exit 0
    fi
    pid=$(pgrep -f 'RiderChildHold worker 2' | head -1)
    if [ -n "$pid" ] && [ -z "$early_done" ] && ! ls "$run/$pid-"*.txt >/dev/null 2>&1; then
        snapshot > "$out/early-w2-$pid.txt" 2>&1
        early_done=1
        echo "worker 2 (pid $pid) seen before its marks; snapshot at $(date -u +%H:%M:%SZ): $out/early-w2-$pid.txt"
    fi
    w2=$(ls "$run/"*-worker2.txt 2>/dev/null | head -1)
    if [ -n "$w2" ] && grep -q '\] assembly ' "$w2" && ! grep -q '\] checkpoint ' "$w2"; then
        now=$(date +%s)
        [ -z "$since" ] && since=$now
        if [ $(( now - since )) -ge 2 ]; then
            snapshot > "$out/held-worker2.txt" 2>&1
            echo "worker 2 held at the breakpoint; snapshot at $(date -u +%H:%M:%SZ): $out/held-worker2.txt"
            exit 0
        fi
    else
        since=""
    fi
    sleep 0.05
done
echo "gave up after 20 minutes"
