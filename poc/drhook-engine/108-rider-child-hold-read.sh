#!/usr/bin/env bash
# DrHook probe 108's read side (macOS): what a RiderChildHold process cannot see about itself, read while Rider holds
# a worker at its breakpoint (run C in 108-rider-child-hold/Program.cs). Read-only: ps, lsof, ls. It stops nothing,
# signals nothing and writes nothing but its output.
#
# For each RiderChildHold process (the apphost, or `dotnet RiderChildHold.dll`):
#   - pid, parent, process group, state, start time, executable, thread count
#   - its ancestry up to launchd, by executable name only (never arguments, which may carry tokens)
#   - the runtime's files for that pid in the user's temp dir: clr-debug-pipe-<pid>-* (a debugger's transport) and
#     dotnet-diagnostic-<pid>-* (the default diagnostics port)
#   - its unix sockets, and for each connected one the process holding the other end and that socket's path
# Then every JetBrains process by pid, parent and executable (no arguments).
#
# Usage: ./108-rider-child-hold-read.sh        (bash 3.2, as shipped with macOS)

set -u

tmp=$(getconf DARWIN_USER_TEMP_DIR)
echo "probe 108 read at $(date -u +%Y-%m-%dT%H:%M:%SZ) on $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)"
echo "user temp dir $tmp"

targets=""
for pid in $(pgrep -f RiderChildHold); do
    exe=$(ps -o comm= -p "$pid" 2>/dev/null) || continue
    base=$(basename "$exe")
    if [ "$base" = "RiderChildHold" ]; then
        targets="$targets $pid"
    elif [ "$base" = "dotnet" ] && ps -o args= -p "$pid" | grep -q 'RiderChildHold\.dll'; then
        targets="$targets $pid"
    fi
done

if [ -z "$targets" ]; then
    echo "no RiderChildHold process found"
fi

all_unix=$(lsof -nP -U 2>/dev/null)

for pid in $targets; do
    echo
    echo "== RiderChildHold $pid"
    ps -o pid=,ppid=,pgid=,stat=,lstart=,comm= -p "$pid" | sed 's/^/   ps: /'
    echo "   threads: $(( $(ps -M -p "$pid" | wc -l) - 1 ))"

    printf '   ancestry:'
    p=$pid
    while [ -n "$p" ] && [ "$p" -gt 1 ] 2>/dev/null; do
        parent=$(ps -o ppid= -p "$p" | tr -d ' ')
        [ -z "$parent" ] && break
        printf ' %s(%s) <-' "$parent" "$(basename "$(ps -o comm= -p "$parent" 2>/dev/null)")"
        p=$parent
    done
    echo

    echo "   runtime files in the temp dir:"
    ls -l "$tmp" 2>/dev/null | grep -E "(clr-debug-pipe|dotnet-diagnostic)-$pid-" | sed 's/^/     /' || true

    echo "   unix sockets (DEVICE, FD, NAME; -> marks a connected socket and its peer's address):"
    lsof -nP -a -U -p "$pid" 2>/dev/null | awk 'NR > 1 { print "     " $6 "  fd " $4 "  " $NF }'

    for peer in $(lsof -nP -a -U -p "$pid" 2>/dev/null | awk 'NR > 1 && $NF ~ /^->0x/ { sub(/^->/, "", $NF); print $NF }'); do
        echo "$all_unix" | awk -v peer="$peer" 'NR > 1 && $6 == peer { print "     peer " peer ": " $1 " pid " $2 " fd " $4 "  " $NF }'
    done
done

echo
echo "== JetBrains processes (pid, parent, executable)"
ps -axo pid=,ppid=,comm= | grep -i -E 'jetbrains|rider|resharper' | grep -v -E 'RiderChildHold|grep' | sed 's/^/   /'
