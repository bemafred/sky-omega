#!/usr/bin/env bash
# Watches run C of probe 108. Read-only: runs the probe's read script at two moments and exits.
#   early-<pid>.txt  a worker process exists but has written no marks yet (held before its own managed code?)
#   held-worker1.txt worker 1 has written its facts but not its checkpoint for over 1 s (stopped at line 72)
# Exits when worker 1 is held, when the run ends without a hold, or after 20 minutes.
set -u
probe=/Users/bemafred/src/repos/sky-omega/poc/drhook-engine
marks=$probe/108-rider-child-hold/bin/Debug/net10.0/marks
out=/private/tmp/claude-501/-Users-bemafred-src-repos-sky-omega/f163c189-105f-49c1-99d2-69ca8fdbb95a/scratchpad/probe108-c
mkdir -p "$out"
baseline=$(ls "$marks" | sort | tail -1)
seen=" "
since=""
deadline=$(( $(date +%s) + 1200 ))
echo "watching from $(date -u +%H:%M:%S.%NZ 2>/dev/null || date -u), newest existing run $baseline"
while [ "$(date +%s)" -lt "$deadline" ]; do
    run=$(ls "$marks" | sort | tail -1)
    [ "$run" = "$baseline" ] && run=""
    for pid in $(pgrep -f 'RiderChildHold worker'); do
        case "$seen" in *" $pid "*) continue ;; esac
        seen="$seen$pid "
        if [ -z "$run" ] || ! ls "$marks/$run/$pid-"*.txt >/dev/null 2>&1; then
            "$probe/108-rider-child-hold-read.sh" > "$out/early-$pid.txt" 2>&1
            echo "early snapshot of worker pid $pid at $(date -u +%H:%M:%SZ): $out/early-$pid.txt"
        fi
    done
    if [ -n "$run" ]; then
        if ls "$marks/$run/"*-supervisor.txt >/dev/null 2>&1 && grep -q '\] exit (' "$marks/$run/"*-supervisor.txt; then
            echo "run $run ended without a held worker 1 at $(date -u +%H:%M:%SZ)"
            exit 0
        fi
        w1=$(ls "$marks/$run/"*-worker1.txt 2>/dev/null | head -1)
        if [ -n "$w1" ] && grep -q '\] assembly ' "$w1" && ! grep -q '\] checkpoint ' "$w1"; then
            now=$(date +%s)
            [ -z "$since" ] && since=$now
            if [ $(( now - since )) -ge 2 ]; then
                "$probe/108-rider-child-hold-read.sh" > "$out/held-worker1.txt" 2>&1
                echo "worker 1 held at the breakpoint in run $run; snapshot at $(date -u +%H:%M:%SZ): $out/held-worker1.txt"
                exit 0
            fi
        else
            since=""
        fi
    fi
    sleep 0.05
done
echo "gave up after 20 minutes, newest run ${run:-none}"
