using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SkyOmega.DrHook.Mcp;

// Parse command line arguments before building host
bool showHelp = false;
bool showVersion = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h":
        case "--help":
            showHelp = true;
            break;
        case "-v":
        case "--version":
            showVersion = true;
            break;
        default:
            Console.Error.WriteLine($"Error: Unknown option '{args[i]}'.");
            Console.Error.WriteLine("Use --help for usage information.");
            return 1;
    }
}

if (showVersion)
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    Console.WriteLine($"drhook-mcp {version}");
    return 0;
}

if (showHelp)
{
    Console.Error.WriteLine("""
        DrHook MCP Server

        Usage: drhook-mcp [options]

        Options:
          -v, --version           Show version information
          -h, --help              Show this help message

        DrHook provides .NET runtime inspection for AI coding agents:
          - Passive observation via EventPipe (thread sampling, GC, exceptions)
          - Controlled stepping via DrHook.Engine — BCL-only ICorDebug interop
            (no netcoredbg dependency; libdbgshim bundled per-RID via NuGet).

        Optional override:
          Set DBGSHIM_PATH to a custom libdbgshim build (testing only;
          the bundled per-RID shim is the default).

        The MCP server communicates via stdin/stdout (JSON-RPC 2.0).
        """);
    return 0;
}

Console.Error.WriteLine("DrHook MCP Server starting...");

// Build host with MCP SDK
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register EngineSteppingSession as singleton (stateful — one session at a time, BCL-only via
// DrHook.Engine's ICorDebug interop; replaces the netcoredbg-DAP-backed SteppingSessionManager
// per finding 51).
builder.Services.AddSingleton<EngineSteppingSession>();

// Register MCP server with stdio transport and tools
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "drhook-mcp",
            Version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown"
        };
        // Sent to the client during the initialization handshake and surfaced as an LLM system message:
        // orient the agent to DrHook as the runtime-observation substrate and point to the canonical doc
        // (the MCP-layer counterpart to CLAUDE.md -> DRHOOK.md).
        options.ServerInstructions =
            """
            DrHook is Sky Omega's .NET runtime-observation substrate — ICorDebug interop via DrHook.Engine
            (BCL + P/Invoke). Use it to observe what code actually does: set breakpoints at decision points,
            step, and inspect locals/arguments — rather than changing code you have not watched run. Every
            state-changing tool and every inspection that reads target state takes a `hypothesis` parameter:
            state what you expect BEFORE you observe (Sky Omega epistemic discipline).

            Full debugging workflow, the complete tool reference, how to run each test kind, and the probe
            corpus: DRHOOK.md in the Sky Omega repo —
            https://github.com/bemafred/sky-omega/blob/main/DRHOOK.md
            """;
    })
    .WithStdioServerTransport()
    .WithTools<DrHookTools>();

Console.Error.WriteLine();
Console.Error.WriteLine("Ready. Waiting for MCP messages on stdin...");
Console.Error.WriteLine();

await builder.Build().RunAsync();

Console.Error.WriteLine("DrHook MCP Server shutting down...");

return 0;
