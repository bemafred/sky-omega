#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
// What a client reads of a response that was finished before the listener stopped, when it reads after the stop.
// The question (Mercury.Tests in parallel on omega-windows, 2026-11-21): does HttpListener.Stop() lose a response that
// the server has closed and the client has not yet read? Each run is one listener, one request, one response.
//
// The order is fixed, so the result does not depend on timing: the server writes the body and closes the response;
// the client has the response's headers; then the listener stops; then, 50 ms later, the client reads the body.
//
// Flags per variant, joined by '+'; "plain" has none: chunked and keep-alive, the loop's responses before 2026-11-22.
//   close      the response says Connection: close (Response.KeepAlive = false)
//   length     the response has a Content-Length (otherwise chunked)
//   wait       the listener stops 200 ms after the response was closed, not at once
//   readfirst  the client reads the body before the stop (the control)
//   big        the body is 32 KB: more than the client reads with the headers, less than the sockets' buffers hold
//              (a body of 1 MB never finished: the server's write waits for the client to read)
//   nostop     the listener does not stop before the read (the control for close)
//   nochunk    SendChunked = false and no Content-Length: a body delimited by the connection's close
// The first run of each variant prints the TCP states of the port's connections just before the stop.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

string[] variants = args.Length > 0 ? args
    : ["readfirst", "plain", "wait", "close", "close+wait", "length", "length+big", "close+length", "close+length+big+wait",
       "close+nostop"];
const int runs = 30;
Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, {runs} runs each");
foreach (var variant in variants)
{
    var flags = variant.Split('+').ToHashSet();
    int whole = 0;
    var failures = new Dictionary<string, int>();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < runs; i++)
    {
        var outcome = await Once(flags, printStates: i == 0);
        if (outcome == "whole") whole++;
        else failures[outcome] = failures.GetValueOrDefault(outcome) + 1;
    }
    Console.WriteLine($"{variant,-20} read whole {whole}/{runs}" +
        (failures.Count > 0 ? "; " + string.Join("; ", failures.Select(f => $"{f.Value}x {f.Key}")) : "") +
        $" ({sw.ElapsedMilliseconds} ms)");
}

static async Task<string> Once(HashSet<string> flags, bool printStates)
{
    var port = FreePort();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    var body = flags.Contains("big")
        ? Enumerable.Range(0, 32 << 10).Select(i => (byte)('a' + i % 26)).ToArray()
        : "answered"u8.ToArray();
    var expected = System.Text.Encoding.ASCII.GetString(body);
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        if (flags.Contains("close")) context.Response.KeepAlive = false;
        if (flags.Contains("length")) context.Response.ContentLength64 = body.Length;
        if (flags.Contains("nochunk")) context.Response.SendChunked = false;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    });
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        var response = await http.GetAsync($"http://localhost:{port}/", HttpCompletionOption.ResponseHeadersRead);
        if (!flags.Contains("readfirst") && await Task.WhenAny(server, Task.Delay(5000)) != server)
            return "the server's response did not finish before the client read";
        await server;
        if (flags.Contains("readfirst"))
        {
            var early = await response.Content.ReadAsStringAsync();
            listener.Stop();
            return early == expected ? "whole" : $"read {early.Length} chars";
        }
        if (flags.Contains("wait")) await Task.Delay(200);
        if (printStates)
            Console.WriteLine("  before the stop: " + string.Join(", ",
                System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                    .Where(c => c.LocalEndPoint.Port == port || c.RemoteEndPoint.Port == port)
                    .Select(c => $"{(c.LocalEndPoint.Port == port ? "server" : "client")} {c.State}")));
        if (!flags.Contains("nostop")) listener.Stop();
        await Task.Delay(50);
        var text = await response.Content.ReadAsStringAsync();
        return text == expected ? "whole" : $"read {text.Length} chars";
    }
    catch (Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner is SocketException se ? $"{ex.GetType().Name}/{se.SocketErrorCode}" : $"{ex.GetType().Name}/{inner.GetType().Name}: {inner.Message}";
    }
    finally
    {
        listener.Close();
    }
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}
