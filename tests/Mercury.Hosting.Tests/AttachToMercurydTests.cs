// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// <c>mercury --attach</c> with a store given attaches to the mercuryd that serves that store, on the pipe mercuryd
/// made for it (ADR-065 D7, Step 5); <c>mercury --attach</c> alone attaches to mercury-mcp, and with a pipe's name to
/// that pipe, as before. mercury and mercuryd each run as a process of their own.
/// </summary>
/// <remarks>
/// Both processes have the test's own stores' directory and temporary directory: <c>--store</c> names a store of the
/// test's, and a pipe named by a name rather than a path, mercury-mcp's among them, is looked for in the test's
/// directory, where none is. The test's data directory is not made beforehand, as for a user that has never kept data
/// there: the account a service runs as (MercuryPaths.Store). On Windows the stores' directory is the user's, and a
/// pipe's name is the machine's.
/// </remarks>
public sealed class AttachToMercurydTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly string _name = $"s{Guid.NewGuid():N}"[..9];
    private readonly string _home;
    private readonly string _store;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public AttachToMercurydTests()
    {
        _dir = TempPath.Test("attach");
        _dir.MarkOwnership();
        _home = Path.Combine(_dir.FullPath, "home");
        _store = Path.Combine(_dir.FullPath, _name);
        _environment = new Dictionary<string, string>
        {
            // The stores' directory is under HOME on macOS, and under XDG_DATA_HOME on Linux (MercuryPaths.Store).
            [OperatingSystem.IsMacOS() ? "HOME" : "XDG_DATA_HOME"] = _home,
            ["TMPDIR"] = _dir.FullPath,
        };
    }

    public void Dispose() => _dir.Cleanup();

    private string StoreNamed => OperatingSystem.IsMacOS()
        ? Path.Combine(_home, "Library", "SkyOmega", "stores", _name)
        : Path.Combine(_home, "SkyOmega", "stores", _name);

    [Theory]
    [InlineData("--attach --store <name>")]
    [InlineData("--store <name> --attach")]
    [InlineData("-a --store <name>")]
    [InlineData("--attach -d <path>")]
    [InlineData("<path> --attach")]
    public async Task WithAStoreGiven_ItAttachesToTheMercurydThatServesIt(string form)
    {
        bool byName = form.Contains("<name>");
        Assert.SkipWhen(byName && OperatingSystem.IsWindows(), "The stores' directory on Windows is the user's.");
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var mercuryd = byName
            ? Mercuryd.Start(_environment, "--store", _name, "-p", port)
            : Mercuryd.Start(_environment, "-d", _store, "-p", port);
        await mercuryd.WhenReady(ct);
        var store = byName ? StoreNamed : _store;

        using var mercury = MercuryCli.Start(_environment,
            [.. form.Split(' ').Select(arg => arg switch { "<name>" => _name, "<path>" => _store, _ => arg })]);
        await mercury.WhenSaid("Connected to mercuryd", ct);
        mercury.Type(":store");
        mercury.EndInput();

        Assert.Equal(0, await mercury.Ended(ct));
        var output = mercury.Output.Split(Environment.NewLine);
        Assert.Equal($"Attaching to the mercuryd of {store} via pipe '{MercuryPorts.DaemonPipeName(store)}'...", output[0]);
        Assert.Equal($"Connected to mercuryd (store: {store})", output[1]);
        Assert.Contains(output, line => line.Contains(store) && line != output[0] && line != output[1]); // :store's answer
    }

    [Fact]
    public async Task APipesName_IsAttachedToAsBefore_MercurydsAmongThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = Mercuryd.Start(_environment, "-d", _store, "-p", FreePort());
        await mercuryd.WhenReady(ct);
        var pipe = MercuryPorts.DaemonPipeName(_store);

        using var mercury = MercuryCli.Start(_environment, "--attach", pipe);
        await mercury.WhenSaid("Connected to mercuryd", ct);
        mercury.EndInput();

        Assert.Equal(0, await mercury.Ended(ct));
        Assert.StartsWith($"Attaching to {pipe} via pipe '{pipe}'...", mercury.Output);
    }

    [Fact]
    public async Task WithNoStoreAndNoPipe_ItAttachesToMercuryMcp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A pipe's name on Windows is the machine's: a mercury-mcp may answer.");
        var ct = TestContext.Current.CancellationToken;

        using var mercury = MercuryCli.Start(_environment, "--attach");

        Assert.Equal(1, await mercury.Ended(ct));
        Assert.Equal(
            $"Attaching to mcp via pipe 'mercury-mcp'...{Environment.NewLine}Error: Could not connect to mcp. Is it running?",
            mercury.Output);
    }

    [Fact]
    public async Task AStoreNothingServes_IsSaidSo_AndItIsNotMade()
    {
        var ct = TestContext.Current.CancellationToken;

        using var mercury = MercuryCli.Start(_environment, "--attach", "-d", _store);

        Assert.Equal(1, await mercury.Ended(ct));
        Assert.EndsWith($"Error: Could not connect to the mercuryd of {_store}. Is it running?", mercury.Output);
        Assert.False(Directory.Exists(_store));
    }

    [Fact]
    public async Task APipesNameAndAStore_AreRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        using var mercury = MercuryCli.Start(_environment, "--attach", "mcp", "-d", _store);

        Assert.Equal(1, await mercury.Ended(ct));
        Assert.StartsWith("Error: --attach names the pipe 'mcp' and a store is given: attach to one of them.", mercury.Errors);
        Assert.Equal("", mercury.Output);
    }

    /// <summary>A session that waits for its client's next line ends with the stop. The CLI, which waits for the
    /// user's, learns at that line that mercuryd has gone, and says so.</summary>
    [Fact]
    public async Task AnAttachedCli_DoesNotHoldMercurydsStop()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows stops a service with its stop request (ADR-065 Step 9).");
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = Mercuryd.Start(_environment, "-d", _store, "-p", FreePort());
        await mercuryd.WhenReady(ct);
        using var mercury = MercuryCli.Start(_environment, "--attach", "-d", _store);
        await mercury.WhenSaid("Connected to mercuryd", ct);

        mercuryd.Signal(Mercuryd.Sigterm);

        Assert.Equal(0, await mercuryd.Ended(ct));
        mercury.EndInput();
        Assert.Equal(1, await mercury.Ended(ct));
        Assert.Contains("Error: ", mercury.Output.Split(Environment.NewLine)[^1]);
    }

    private static string FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port.ToString();
    }
}
