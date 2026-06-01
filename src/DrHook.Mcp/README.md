# DrHook MCP Server

MCP server for .NET runtime inspection. 20 tools spanning EventPipe observation, ICorDebug-native stepping, breakpoint management with conditional/hit-count/logpoint policies, locals inspection, session lifecycle (stop / detach / kill), and substrate-anomaly streaming. Backed entirely by `DrHook.Engine` (BCL + P/Invoke + source-gen COM). Tool names follow established IDE-debugger convention per [ADR-010](../../docs/adrs/drhook/ADR-010-mcp-tool-surface-redesign.md).
