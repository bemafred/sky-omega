#!/usr/bin/env bash
# ADR-055 Q3 / validation step 4: struct sizes, parse and execute frames, and the smallest completing stack.
# Run from anywhere; writes listings to ./out/. arm64 prologues only (frames.py parses stp/sub sp).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out
dll() { dotnet build "$1" -c Release -v n 2>&1 | grep -E "$1 -> " | sed 's/.*-> //'; }

echo "== struct sizes"
dotnet run -c Release --no-cache struct-sizes.cs 2>/dev/null

W=$(dll jit-workload.cs)
for mode in opt tier0; do
  if [ $mode = opt ]; then TC=0; else TC=1; fi
  DOTNET_TieredCompilation=$TC DOTNET_TieredPGO=0 DOTNET_JitDisasm='*' dotnet "$W" > out/jit-$mode.txt 2>&1
done
echo "== frames, fully optimized"
python3 frames.py out/jit-opt.txt 'SparqlParser:|SparqlEngine:|QueryExecutor:|TreeJoinExecutor:' 25
echo "== frames, Tier 0"
python3 frames.py out/jit-tier0.txt 'SparqlParser:|SparqlEngine:|QueryExecutor:|TreeJoinExecutor:' 15

echo "== smallest completing stack"
python3 min-stack.py simple,complex,update,nest1,nest5,nest10,optnest5,optnest10 "$(dll min-stack.cs)"
