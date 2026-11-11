#!/usr/bin/env bash
# Compares each W3C test-data submodule's pin with the head of its upstream default branch, so an upstream change is
# seen when it lands rather than when an IDE happens to pull it (ADR-059 D6).
#
#   tools/check-w3c-upstream.sh          each pin and upstream head, and the commits between them if this clone has them
#   tools/check-w3c-upstream.sh --fetch  fetches upstream first, so the commits are listed
#   tools/check-w3c-upstream.sh --run    also checks out upstream's head in each submodule whose test data changed,
#                                        runs the W3C suite against it, and puts the pins back
#
# It never moves a pin. A pin moves in one commit, after a full W3C run against the new data (ADR-059 D6); --run shows
# what that run will find. Exits 1 when --run has failing tests, 0 otherwise. The weekly workflow
# .github/workflows/w3c-upstream.yml runs it with --run and shows the report in the run's summary.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

fetch=false
run=false
case "${1:-}" in
  "") ;;
  --fetch) fetch=true ;;
  --run) fetch=true; run=true ;;
  *) echo "usage: $0 [--fetch | --run]" >&2; exit 2 ;;
esac

SUBMODULES=(tests/w3c-rdf-tests tests/w3c-json-ld-api)

# The directories of each submodule that hold test data. A commit only elsewhere (the spec, reports, the README)
# changes no test, and json-ld-api is mostly the specification.
test_data() {
  case "$1" in
    tests/w3c-rdf-tests) echo "rdf sparql ns" ;;
    tests/w3c-json-ld-api) echo "tests" ;;
  esac
}

report="$(mktemp)"
say() { printf '%s\n' "$*" >> "$report"; }

say "## W3C test data upstream"
say ""

# Submodules whose test data upstream changed, as path:commit
changed=()

for path in "${SUBMODULES[@]}"; do
  url="$(git -C "$ROOT" config -f .gitmodules "submodule.$path.url")"
  pin="$(git -C "$ROOT" rev-parse "HEAD:$path")"
  if $fetch; then
    git -C "$ROOT/$path" fetch --quiet origin HEAD
    upstream="$(git -C "$ROOT/$path" rev-parse FETCH_HEAD)"
  else
    upstream="$(git ls-remote "$url" HEAD | cut -f1)"
  fi

  if [ "$pin" = "$upstream" ]; then
    say "- **$path**: the pin, ${pin:0:7}, is upstream's head."
    continue
  fi
  if ! git -C "$ROOT/$path" cat-file -e "$upstream^{commit}" 2> /dev/null; then
    say "- **$path**: pinned at ${pin:0:7}, upstream's head is ${upstream:0:7}; this clone does not have upstream's commits (\`--fetch\` lists them)."
    continue
  fi
  if ! git -C "$ROOT/$path" merge-base --is-ancestor "$pin" "$upstream"; then
    say "- **$path**: pinned at ${pin:0:7}, upstream's head is ${upstream:0:7}, which does not descend from the pin (upstream rewrote its history)."
    changed+=("$path:$upstream")
    continue
  fi

  dirs="$(test_data "$path")"   # unquoted below: one path argument per directory
  all="$(git -C "$ROOT/$path" rev-list --count "$pin..$upstream")"
  data="$(git -C "$ROOT/$path" rev-list --count "$pin..$upstream" -- $dirs)"
  say "- **$path**: pinned at ${pin:0:7}, upstream's head is ${upstream:0:7}: $all commits, $data of them in the test data ($dirs)."
  git -C "$ROOT/$path" log --format='  - %h %ad %s' --date=short "$pin..$upstream" -- $dirs >> "$report"
  if [ "$data" -gt 0 ]; then
    changed+=("$path:$upstream")
  fi
done

status=0
if $run && [ "${#changed[@]}" -gt 0 ]; then
  restore() {
    for entry in "${changed[@]}"; do
      git -C "$ROOT" submodule update --quiet --init -- "${entry%%:*}"
    done
  }
  trap restore EXIT

  for entry in "${changed[@]}"; do
    git -C "$ROOT/${entry%%:*}" checkout --quiet --detach "${entry#*:}"
  done

  results="$(mktemp -d)"
  dotnet test --project "$ROOT/tests/Mercury.Tests" --filter "FullyQualifiedName~SkyOmega.Mercury.Tests.W3C" \
    --results-directory "$results" > "$results/w3c-upstream.txt" 2>&1 || status=1

  say ""
  say "### The W3C suite against upstream's head"
  say ""
  say "The pinned suite's code, $(git -C "$ROOT" rev-parse --short HEAD), run against the new test data; the guard (ADR-059 D4) is part of it."
  say ""
  # The runner's summary: "  total: 2840", "  failed: 0", "  succeeded: 2685", "  skipped: 155"
  grep -E '^ +(total|failed|succeeded|skipped): ' "$results/w3c-upstream.txt" | sed -E 's/^ +/- /' >> "$report" || true
  if [ "$status" -ne 0 ]; then
    say ""
    say "Failed:"
    # "failed Turtle NegativeSyntax(test: W3CTestCase { Id = <iri>, …" becomes the method and the test IRI; any other
    # "failed <name> (<duration>)" its name
    grep -E '^failed ' "$results/w3c-upstream.txt" |
      sed -E -e 's/^failed ([^(]*)\(test: W3CTestCase \{ Id = <([^>]*)>.*/- \1 <\2>/' -e 's/^failed (.*) \([^()]*\)$/- \1/' >> "$report" || true
    cat "$results/w3c-upstream.txt"
  fi
  say ""
  say "The run's output: $results/w3c-upstream.txt"
elif $run; then
  say ""
  say "No test data changed upstream: nothing to run."
fi

cat "$report"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  cat "$report" >> "$GITHUB_STEP_SUMMARY"
fi
rm -f "$report"
exit "$status"
