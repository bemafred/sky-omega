#!/usr/bin/env bash
# Pre-James MVP: deterministic per-turn attention trigger (proto-James fires proto-Lucy).
# Pure text, no store access. Recall executes in the agent via the mercury MCP.
# Emitted as UserPromptSubmit additionalContext (single-line directive => trivial JSON).
DIRECTIVE="[Lucy / pre-James] Before responding: if this prompt touches a topic, decision, component (Mercury/DrHook/Lucy/James/Minerva), bug, ADR, or finding that may be in memory, recall FIRST. Invoke the \`lucy\` skill (the recall method) for a substantive topic, or mercury_query directly for a quick check (GRAPH ?g union; declare every PREFIX, since an undeclared prefix returns empty silently). Then answer from recognition. Skip for trivial or acknowledgment turns."
printf '{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"%s"}}\n' "$DIRECTIVE"
