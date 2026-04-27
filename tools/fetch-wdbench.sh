#!/usr/bin/env bash
# Download the WDBench query suite (Buil-Aranda, Hernández, Hogan et al.) from
# github.com/MillenniumDB/WDBench and split into per-query .sparql files for
# the Mercury.Benchmarks WdBench harness.
#
# WDBench publishes 2,658 SPARQL queries across five categories
# (single_bgps, multiple_bgps, paths, opts, c2rpqs) as one query per CSV line
# in five .txt files. Each line is `<id>,<WHERE-clause-body>`. This script
# downloads the originals, splits them, wraps each as `SELECT * WHERE { ... }`,
# and writes them to the dataset directory.
#
# Default destination: ~/Library/SkyOmega/datasets/wdbench/{raw,queries}.
# Override via WDBENCH_DIR env var.

set -euo pipefail

DEST="${WDBENCH_DIR:-$HOME/Library/SkyOmega/datasets/wdbench}"
RAW="$DEST/raw"
OUT="$DEST/queries"

CATEGORIES=(single_bgps multiple_bgps paths opts c2rpqs)
BASE_URL="https://raw.githubusercontent.com/MillenniumDB/WDBench/master/Queries"

mkdir -p "$RAW" "$OUT"

echo "Downloading WDBench query files to $RAW ..."
for cat in "${CATEGORIES[@]}"; do
    url="$BASE_URL/$cat.txt"
    echo "  $cat.txt"
    curl -sSL "$url" -o "$RAW/$cat.txt"
done

echo "Splitting into per-query .sparql files at $OUT ..."
python3 - <<EOF
import os
RAW = "$RAW"
OUT = "$OUT"
total = 0
for cat in [${CATEGORIES[@]/#/\"}${CATEGORIES[@]/%/\"}]:
    src = os.path.join(RAW, f"{cat}.txt")
    dst = os.path.join(OUT, cat)
    os.makedirs(dst, exist_ok=True)
    with open(src, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n").rstrip("\r")
            if not line: continue
            comma = line.find(",")
            if comma < 0: continue
            qid, body = line[:comma], line[comma + 1:]
            with open(os.path.join(dst, f"{int(qid):05d}.sparql"), "w", encoding="utf-8") as out:
                out.write(f"SELECT * WHERE {{ {body} }}\n")
            total += 1
print(f"  {total} queries written")
EOF

echo
echo "Per-category counts:"
for cat in "${CATEGORIES[@]}"; do
    count=$(ls -1 "$OUT/$cat" 2>/dev/null | wc -l | tr -d ' ')
    printf "  %-20s %5d\n" "$cat" "$count"
done
echo
echo "Done. Run with:"
echo "  dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \\"
echo "    wdbench --store <STORE> --queries $OUT --metrics-out <ARTIFACT>"
