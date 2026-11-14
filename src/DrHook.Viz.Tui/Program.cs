using System.Reflection;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.Tui;

// DrHook TUI dashboard — a full-screen, live view of the active debug session (ADR-012 Phase 4). A thin shim over
// DrHook.Viz, like drhook-viz-console; that one TAILS the stream, this one redraws a fixed layout in place.

string? socketPath = null;
foreach (string arg in args)
{
    if (arg is "-h" or "--help")
    {
        Console.WriteLine("""
            DrHook TUI dashboard — a live, full-screen view of the active debug session.

            Usage: drhook-viz-tui [socket-path]

              socket-path     the rendezvous socket (default: the well-known per-host path)
              -v, --version   show version information
              -h, --help      show this help

            Panes: stack, source-on-step, locals/args, breakpoints, the (hypothesis, observation) braid and
            lifecycle events, the target's console output, anomalies. Read-only — the session is driven by the
            agent through the DrHook MCP; closing the dashboard never affects it. q or Ctrl+C to quit.

            It can be started before any session and left running: it waits for a session, shows it, keeps the
            last state on screen when the session ends, and connects to the next one.
            """);
        return 0;
    }
    if (arg is "-v" or "--version")
    {
        Console.WriteLine($"drhook-viz-tui {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");
        return 0;
    }
    if (arg.StartsWith('-')) { Console.Error.WriteLine($"unknown option: {arg}"); return 2; }
    socketPath = arg;
}

if (Console.IsOutputRedirected || Console.IsInputRedirected)
{
    Console.Error.WriteLine("drhook-viz-tui needs an interactive terminal; to capture the stream as text use drhook-viz-console.");
    return 2;
}

var options = new DebugStateClientOptions { Reconnect = true }; // a dashboard stays up across sessions
if (socketPath is not null) options.SocketPath = socketPath;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var terminal = new AnsiTerminal(Console.Out);
var view = new DashboardDebugStateView(terminal, () => (Console.WindowWidth, Console.WindowHeight), reconnects: true);
terminal.Enter();
try
{
    var render = Task.Factory.StartNew(() => view.RunRenderLoop(cts.Token), TaskCreationOptions.LongRunning);
    var client = new DebugStateClient(options).RunAsync(view, cts.Token);
    while (!cts.IsCancellationRequested)
    {
        if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q) cts.Cancel();
        else cts.Token.WaitHandle.WaitOne(50);
    }
    await Task.WhenAll(render, client.ContinueWith(_ => { }));
}
finally
{
    terminal.Exit();
}
return 0;
