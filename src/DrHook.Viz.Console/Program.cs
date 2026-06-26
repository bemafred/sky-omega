using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.ConsoleView;

// DrHook console visualizer — tails the live debug-state stream from the rendezvous socket. A thin shim over
// DrHook.Viz (mirrors Mercury.Cli.Sparql over Mercury.Sparql.Tool), and a clean, simple first DrHook debuggee.

string? socketPath = null;
foreach (string arg in args)
{
    if (arg is "-h" or "--help")
    {
        Console.WriteLine("""
            DrHook console visualizer — tail the live debug-state stream.

            Usage: drhook-viz-console [socket-path]

              socket-path   the rendezvous socket (default: the well-known per-host path)
              -h, --help    show this help

            Connects to a DrHook debug-state transport, prints the snapshot-on-connect, then the
            live delta stream. Ctrl+C to quit.
            """);
        return 0;
    }
    if (arg.StartsWith('-')) { Console.Error.WriteLine($"unknown option: {arg}"); return 2; }
    socketPath = arg;
}

var options = new DebugStateClientOptions();
if (socketPath is not null) options.SocketPath = socketPath;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"drhook-viz-console → {options.SocketPath}  (Ctrl+C to quit)");
await new DebugStateClient(options).RunAsync(new ConsoleDebugStateView(Console.Out), cts.Token);
return 0;
