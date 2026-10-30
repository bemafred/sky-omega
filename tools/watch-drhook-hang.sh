#!/bin/bash
# Records the stacks of the DrHook integration-test host when it runs long enough to be hung.
#
# On the GitHub macOS runners a DrHook integration test can hang inside DebugSession.AttachAndOwn, and every
# attach and launch after it then hangs too (ADR-007, Phase 8 note, 2026-10-30). It has never happened on our
# machines. The CI workflow runs this beside `dotnet test` on macOS. Once the host has run THRESHOLD seconds, it
# records native stacks of every thread (sample) and managed stacks (dotnet-stack), of the host and of each
# debug target, and records them again 3 minutes later. Temporary: removed once the hang's cause is known.
#
# Usage: watch-drhook-hang.sh <output directory> [threshold in seconds, default 90] [dotnet-stack path]
# The suite takes 35-50 s on the macOS runners when nothing hangs.

out=$1
threshold=${2:-90}
dotnet_stack=${3:-dotnet-stack}
mkdir -p "$out"

# sample needs to inspect another process; use sudo where it is passwordless (the GitHub runners), else plain.
if sudo -n true 2>/dev/null; then as_root="sudo -n"; else as_root=""; fi

record() {
    local label=$1 pid=$2 seconds=$3
    echo "$(date -u +%H:%M:%S) $label: pid $pid, $(ps -o command= -p "$pid" | cut -c1-160)"
    $as_root sample "$pid" "$seconds" -file "$out/$label-sample-$pid.txt" > /dev/null 2>&1
    "$dotnet_stack" report -p "$pid" > "$out/$label-stack-$pid.txt" 2>&1
}

host=""
since=0
records=0
while [ "$records" -lt 2 ]; do
    sleep 5
    current=$(pgrep -f 'net10.0/DrHook.Engine.IntegrationTests' | head -1)
    if [ "$current" != "$host" ]; then
        host=$current
        since=$(date +%s)
        continue
    fi
    [ -z "$host" ] && continue
    ran=$(( $(date +%s) - since ))
    if [ "$ran" -ge $(( threshold + records * 180 )) ]; then
        records=$(( records + 1 ))
        echo "$(date -u +%H:%M:%S) record $records: the integration-test host has run $ran s"
        record "record$records-host" "$host" 3
        for target in $(pgrep -f 'testhost.dll|net10.0/DrHook.Engine.IntegrationTargets.Mtp'); do
            record "record$records-target" "$target" 1
        done
    fi
done
echo "$(date -u +%H:%M:%S) done"
