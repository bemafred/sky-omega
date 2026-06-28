using SkyOmega.DrHook.Capture;
using SkyOmega.DrHook.Viz;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;

// DrHook capture — persists the live debug-state stream (the hypothesis-observation braid) to a dedicated
// Graph-profile Mercury store, one named graph per session, for later analysis (mercury-cli / SERVICE
// federation from the cognition store). A transport consumer like drhook-viz-console, but it RECORDS instead
// of rendering — the capture half of "one capture, two consumers". No DrHook.Engine dependency: the engine
// stays substrate-independent; this leaf tool owns the Mercury dependency. RAW capture only — consolidation
// is a later concern for whoever owns the whole (Omega / James), not here.

string? socketPath = null;
string? storeDir = null;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg is "-h" or "--help")
    {
        Console.WriteLine("""
            DrHook capture — record the live debug-state stream to a Mercury store for later analysis.

            Usage: drhook-capture [socket-path] [--store <dir>]

              socket-path    the rendezvous socket (default: the well-known per-host path)
              --store <dir>  the capture store directory (default: ~/Library/SkyOmega/stores/drhook)
              -h, --help     show this help

            Connects to a DrHook debug-state transport and persists each snapshot/delta — including the
            (hypothesis, observation) braid — as raw, append-only triples (one named graph per session)
            in a Graph-profile Mercury store. Query it with mercury-cli, or SERVICE-federate from the
            cognition store. Ctrl+C to quit.
            """);
        return 0;
    }
    if (arg is "--store") { if (i + 1 < args.Length) storeDir = args[++i]; else { Console.Error.WriteLine("--store needs a directory"); return 2; } continue; }
    if (arg.StartsWith('-')) { Console.Error.WriteLine($"unknown option: {arg}"); return 2; }
    socketPath ??= arg;
}

// Default to the canonical named-store path (alongside cli/mcp) — cross-platform-correct via the same helper
// Mercury itself uses, rather than a hand-rolled path. There is no store registry; this naming convention is
// the closest thing to "registration".
storeDir ??= MercuryPaths.Store("drhook");
Directory.CreateDirectory(storeDir);

using var store = new QuadStore(storeDir, null, null, new StorageOptions { Profile = StoreProfile.Graph });

var options = new DebugStateClientOptions();
if (socketPath is not null) options.SocketPath = socketPath;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.Error.WriteLine($"drhook-capture → store {storeDir} (profile Graph) ← {options.SocketPath}  (Ctrl+C to quit)");
await new DebugStateClient(options).RunAsync(new MercuryCaptureView(store, Console.Error), cts.Token);
return 0;
