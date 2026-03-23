using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SkyOmega.DrHook.Mcp;
using SkyOmega.DrHook.Stepping;

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
          - Controlled stepping via DAP/netcoredbg (breakpoints, step-through, variables)

        Requires netcoredbg for stepping operations:
          https://github.com/Samsung/netcoredbg (MIT license)
          Set DRHOOK_NETCOREDBG_PATH if not in standard locations.

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

// Register SteppingSessionManager as singleton (stateful — one session at a time)
builder.Services.AddSingleton<SteppingSessionManager>();

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
    })
    .WithStdioServerTransport()
    .WithTools<DrHookTools>();

Console.Error.WriteLine();
Console.Error.WriteLine("Ready. Waiting for MCP messages on stdin...");
Console.Error.WriteLine();

await builder.Build().RunAsync();

Console.Error.WriteLine("DrHook MCP Server shutting down...");

return 0;
