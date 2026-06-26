#!/usr/bin/env bash
# Pre-James MVP: deterministic wake-up trigger (proto-James fires proto-Lucy).
# Pure text to stdout -> added to context. No store access; recall is the agent's
# act via the mercury MCP (its direct channel to its own memory). The `mercury` CLI
# is Martin's inspection surface for this store, and the agent's path to OTHER stores.
cat <<'EOF'
[Lucy / pre-James -- session start]
Orient to memory through your direct channel, the mercury MCP:
1. mercury_version -- confirm the memory channel is live.
2. Recall the active frontier: invoke the `lucy` skill (the recall method), or for a quick
   check mercury_query the most recent labelled nodes across the GRAPH ?g union
   (ORDER BY DESC(?date)).
Recognition before action.
EOF
