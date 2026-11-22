#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
// What HttpListener puts on the wire for a response without a body, and whether a client that reads after the
// listener's stop gets it whole. A raw socket is the client, so the headers are the ones sent.
// Each case: the server writes its response and closes it; the listener stops; 50 ms later the client reads all
// it can, until the connection ends or 2 s pass.
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}");
var big = Enumerable.Range(0, 32 << 10).Select(i => (byte)('a' + i % 26)).ToArray();
(string Name, string Method, int Status, byte[] Body, bool SetLength, bool KeepAlive)[] cases =
[
    ("204, length not set, close", "OPTIONS", 204, [], false, false),
    ("204, length 0, close", "OPTIONS", 204, [], true, false),
    ("200 empty, length 0, close", "GET", 200, [], true, false),
    ("201 empty, length 0, close", "PUT", 201, [], true, false),
    ("HEAD 200, length not set, close", "HEAD", 200, [], false, false),
    ("HEAD 200, length 0, close", "HEAD", 200, [], true, false),
    ("HEAD 200, length 32 KB unwritten, close", "HEAD", 200, big, true, false),
    ("HEAD 200, length 32 KB unwritten, keep", "HEAD", 200, big, true, true),
    ("HEAD 200, length 32 KB WRITTEN, close", "HEAD", 200, big, true, false),
    ("200 32 KB, length, close", "GET", 200, big, true, false),
    ("200 32 KB, length, keep-alive", "GET", 200, big, true, true),
    ("204, length 0, keep-alive", "OPTIONS", 204, [], true, true),
];
foreach (var c in cases)
{
    var port = FreePort();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = c.Status;
        context.Response.KeepAlive = c.KeepAlive;
        if (c.SetLength) context.Response.ContentLength64 = c.Body.Length;
        if (c.Body.Length > 0 && (c.Method != "HEAD" || c.Name.Contains("WRITTEN"))) await context.Response.OutputStream.WriteAsync(c.Body);
        context.Response.Close();
    });
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    string outcome;
    var received = new MemoryStream();
    try
    {
        await socket.ConnectAsync(IPAddress.Loopback, port);
        var requestBody = c.Method == "PUT" ? "Content-Length: 0\r\n" : "";
        await socket.SendAsync(Encoding.ASCII.GetBytes(
            $"{c.Method} / HTTP/1.1\r\nHost: localhost:{port}\r\n{requestBody}\r\n"));
        if (await Task.WhenAny(server, Task.Delay(5000)) != server) throw new TimeoutException("the server did not finish");
        try { await server; }
        catch (Exception ex) { Console.WriteLine($"    the server's close threw {ex.GetType().Name}: {ex.Message}"); }
        listener.Stop();
        await Task.Delay(50);
        var buffer = new byte[64 << 10];
        outcome = "timeout (the connection stayed open)";
        while (true)
        {
            using var cts = new CancellationTokenSource(2000);
            int n;
            try { n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token); }
            catch (OperationCanceledException) { break; }
            if (n == 0) { outcome = "end of stream (graceful)"; break; }
            received.Write(buffer, 0, n);
        }
    }
    catch (SocketException ex) { outcome = $"{ex.SocketErrorCode}"; }
    catch (Exception ex) { outcome = $"{ex.GetType().Name}: {ex.Message}"; }
    finally { listener.Close(); }
    var text = Encoding.ASCII.GetString(received.ToArray());
    var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    var head = headEnd >= 0 ? text[..headEnd] : text;
    var bodyLength = headEnd >= 0 ? received.Length - headEnd - 4 : 0;
    var shown = string.Join(" | ", head.Split("\r\n").Where(l => !l.StartsWith("Date:") && !l.StartsWith("Server:")));
    Console.WriteLine($"{c.Name,-32} {outcome}; {received.Length} bytes, body {bodyLength}\n    {shown}");
    var sent = c.Method == "HEAD" ? 0 : c.Body.Length;
    var after = headEnd >= 0 ? text[(headEnd + 4 + Math.Min(sent, (int)bodyLength))..] : "";
    if (after.Length > 0) Console.WriteLine($"    after the response: {after.Replace("\r\n", " | ")}");
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}
