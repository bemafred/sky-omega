using System.Reflection;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.ConsoleView;

// DrHook console visualizer — tails the live debug-state stream from the rendezvous socket. A thin shim over
// DrHook.Viz (mirrors Mercury.Cli.Sparql over Mercury.Sparql.Tool), and a clean, simple first DrHook debuggee.

string? socketPath = null;
bool follow = false;
foreach (string arg in args)
{
    if (arg is "-h" or "--help")
    {
        Console.WriteLine("""
            DrHook console visualizer — tail the live debug-state stream.

            Usage: drhook-viz-console [options] [socket-path]

              socket-path     the rendezvous socket (default: the well-known per-host path)
              -f, --follow    wait for a session, and for the next one after it ends, until Ctrl+C
              -v, --version   show version information
              -h, --help      show this help

            Connects to a DrHook debug-state transport, prints the snapshot-on-connect, then the
            live delta stream. Without --follow it exits when the session ends, or at once when
            no session is up. Ctrl+C to quit.
            """);
        return 0;
    }
    if (arg is "-v" or "--version")
    {
        Console.WriteLine($"drhook-viz-console {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");
        return 0;
    }
    if (arg is "-f" or "--follow") { follow = true; continue; }
    if (arg.StartsWith('-')) { Console.Error.WriteLine($"unknown option: {arg}"); return 2; }
    socketPath = arg;
}

var options = new DebugStateClientOptions { Reconnect = follow };
if (socketPath is not null) options.SocketPath = socketPath;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"drhook-viz-console → {options.SocketPath}  (Ctrl+C to quit)");
await new DebugStateClient(options).RunAsync(new ConsoleDebugStateView(Console.Out, waitsForNextSession: follow), cts.Token);
return 0;
