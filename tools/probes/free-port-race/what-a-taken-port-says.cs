#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
// What an HttpListener's start on http://localhost:{port}/ says when the port is taken, by each kind of taker a test
// run has: a listener on one address or on all of them, a connection's own end, and another HttpListener. A port is
// known to be free only while it is held, so a test endpoint takes a port, starts, and takes another when the start
// says the port is taken; this is what the start says, on each platform.
//
//   dotnet run tools/probes/free-port-race/what-a-taken-port-says.cs
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"localhost resolves to: {string.Join(", ", Dns.GetHostAddresses("localhost").Select(a => a.ToString()))}");
Console.WriteLine($"a start on port 0: {Start("http://localhost:0/")}");

(string Name, Func<int, IDisposable> Take)[] takers =
[
    ("a TCP listener on 127.0.0.1", port => Listen(new TcpListener(IPAddress.Loopback, port))),
    ("a TCP listener on [::1]", port => Listen(new TcpListener(IPAddress.IPv6Loopback, port))),
    ("a TCP listener on [::], dual mode", port => Listen(new TcpListener(IPAddress.IPv6Any, port) { Server = { DualMode = true } })),
    ("a TCP listener on 0.0.0.0", port => Listen(new TcpListener(IPAddress.Any, port))),
    ("a connection's own end on 127.0.0.1", port =>
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, port));
        client.Connect(server.LocalEndpoint);
        var accepted = server.AcceptSocket();
        return new Disposables(client, accepted, new Stops(server));
    }),
    ("an HttpListener on localhost", port => Http($"http://localhost:{port}/")),
    ("an HttpListener on 127.0.0.1", port => Http($"http://127.0.0.1:{port}/")),
];
foreach (var (name, take) in takers)
{
    var port = FreePort();
    IDisposable taker;
    try
    {
        taker = take(port);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"taken by {name}: the taker could not take it: {Describe(ex)}");
        continue;
    }
    using (taker)
        Console.WriteLine($"taken by {name}: {Start($"http://localhost:{port}/")}");
}

// Another HttpListener on the port refuses the start on every platform.
{
    var port = FreePort();
    using var taker = Http($"http://localhost:{port}/");
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    try
    {
        listener.Start();
        Console.WriteLine("a listener whose start is refused: the start was not refused");
    }
    catch (HttpListenerException)
    {
        try
        {
            listener.Prefixes.Add($"http://localhost:{FreePort()}/");
            Console.WriteLine("a listener whose start is refused: takes another prefix");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"a listener whose start is refused: {Describe(ex)}");
        }
    }
}

static string Start(string prefix)
{
    var listener = new HttpListener();
    try
    {
        listener.Prefixes.Add(prefix);
        listener.Start();
        return "started";
    }
    catch (Exception ex)
    {
        return Describe(ex);
    }
    finally
    {
        listener.Close();
    }
}

static string Describe(Exception ex) => ex is HttpListenerException h
    ? $"{ex.GetType().Name} ErrorCode {h.ErrorCode}, NativeErrorCode {h.NativeErrorCode}, HResult 0x{ex.HResult:X8}: {ex.Message}"
    : ex is SocketException s
        ? $"{ex.GetType().Name} {s.SocketErrorCode} ({s.ErrorCode}): {ex.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

static IDisposable Listen(TcpListener listener)
{
    listener.Start();
    return new Stops(listener);
}

static IDisposable Http(string prefix)
{
    var listener = new HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();
    return listener;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

sealed class Stops(TcpListener listener) : IDisposable
{
    public void Dispose() => listener.Stop();
}

sealed class Disposables(params IDisposable[] all) : IDisposable
{
    public void Dispose()
    {
        foreach (var d in all) d.Dispose();
    }
}
