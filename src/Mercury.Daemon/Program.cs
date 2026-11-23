// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Hosting;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Storage;

// The host of mercuryd (ADR-065 D1): its words, and the Windows service support. Its options, opening the store, the
// HTTP endpoint, the attach socket and the stop handling are in Mercury.Serving and Mercury.Hosting (D11).

var options = DaemonArguments.Read(args);
if (options.Error is { } error)
{
    Console.Error.WriteLine($"Error: {error}");
    return 1;
}

if (options.ShowVersion)
{
    Console.WriteLine(ToolVersion.Line("mercuryd", typeof(Program).Assembly));
    return 0;
}

if (options.ShowHelp)
{
    Console.WriteLine($"""
        mercuryd: one Mercury store, served until it is stopped

        Usage: mercuryd --store <name> [options]
               mercuryd -d <path> [options]

        Options:
          --store <name>         The store of that name under the stores' directory (below)
          -d, --data <path>      The store's directory
          --profile <profile>    The profile of a store that is made: Cognitive (default), Graph,
                                 Reference or Minimal. A store that exists keeps its own.
          --listen <host>        An address to listen on: a name, an IP address, or + or * for
                                 every network. Give it once for each address. Default: localhost.
          -p, --port <port>      The HTTP port (default: {MercuryPorts.Daemon})
          --read-only            Take no updates over HTTP
          --service-connect-timeout <seconds>
                                 How long a SERVICE call waits to connect to its endpoint (default: 5)
          -v, --version          Show version information
          -h, --help             Show this help message

        Serves:
          SPARQL at http://<host>:<port>/sparql, and updates at /sparql/update
          The REPL at the attach socket <store>/mercuryd.sock (mercury-<store name> on Windows,
          or when that path is too long for a socket)

        There is no authentication: whoever reaches an address it listens on may query the
        store and update it. Listen on the tailnet's address. + and * listen on every network
        the machine has, the local one included.

        SERVICE: a query's SERVICE makes mercuryd send it to any address this machine can
        reach, services that listen only on localhost included; a failure says at most
        4 KiB of what such a service answers.

        Stores:
          macOS     ~/Library/SkyOmega/stores/<name>/
          Linux     $XDG_DATA_HOME/SkyOmega/stores/<name>/ (unset: ~/.local/share/SkyOmega/stores/<name>/)
          Windows   %LOCALAPPDATA%\SkyOmega\stores\<name>\

        A store has one process: stop mercuryd before mercury opens its store to load,
        rebuild or convert.

        Stop: SIGTERM or SIGINT, or the service's stop on Windows. The requests in progress
        finish, then the store is closed, and mercuryd exits 0. It exits 1 when it cannot
        start: the store, an address or the attach socket.

        Log:
          mercury.log in the store's directory: each start and stop, where it listens, and every
          warning and error. Standard error goes to the service manager's log.
        """);
    return 0;
}

// A SERVICE call's connect timeout is one for the process, set before the store opens (ADR-066 D5)
if (options.Store.ServiceConnectTimeout is { } serviceConnectTimeout)
    SparqlEngine.ServiceConnectTimeout = serviceConnectTimeout;

var storePath = options.Store.ResolvePath()!;
var version = ToolVersion.Of(typeof(Program).Assembly);

Console.Error.WriteLine($"mercuryd {version} starting, process {Environment.ProcessId}");
Console.Error.WriteLine($"  Store:   {Path.GetFullPath(storePath)}");
Console.Error.WriteLine($"  Log:     {StoreSession.LogPathFor(storePath)}");

StoreSession session;
try
{
    session = StoreSession.Open(storePath, "mercuryd", version,
        new StorageOptions { Profile = options.Store.Profile ?? StoreProfile.Cognitive });
}
catch (StoreInUseException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("A store is served by one process. Stop the process that holds it, or serve another store.");
    return 1;
}
catch (Exception ex)
{
    // The service manager restarts a process that exits with a code other than 0 (D3): an exception let out of Main
    // would end it too, by abort, with a core dump where those are kept.
    Console.Error.WriteLine($"Error: The store cannot be opened: {ex.Message}");
    return 1;
}
if (session.LogWarning is { } logWarning)
    Console.Error.WriteLine(logWarning);
Console.Error.WriteLine($"  Profile: {session.Pool.Active.Schema.Profile}");
Console.Error.WriteLine($"  Updates: {(options.ReadOnly ? "off (--read-only)" : "on")}");

var builder = StoreHost.CreateBuilder(session);
builder.Services.AddWindowsService(service => service.ServiceName = "mercuryd");
builder.AddStoreEndpoints(new StoreEndpointOptions
{
    Hosts = options.Store.Hosts,
    Port = options.Store.Port ?? MercuryPorts.Daemon,
    EnableUpdates = !options.ReadOnly,
    PipeName = MercuryPorts.DaemonPipeName(session.Path),
    PipeWelcome = $"Connected to mercuryd (store: {session.Path})",
    PipePrompt = "mercuryd> ",
});

var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => Console.Error.WriteLine("Ready."));
lifetime.ApplicationStopping.Register(() =>
    Console.Error.WriteLine("Stopping: the requests in progress finish, then the store is closed."));

var exitCode = await StoreHost.RunAsync(host, session);
Console.Error.WriteLine("mercuryd stopped.");
return exitCode;
