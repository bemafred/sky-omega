// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The stop of the HTTP endpoint (ADR-065 D3): it takes no request after it begins, it lets the responses in progress
/// finish, and it returns only then, since the store they read is closed after it. And the loop that writes each
/// response: with its length, and once the stop has begun with the connection's close after it.
/// </summary>
/// <remarks>
/// <para>
/// The listener's own stop ends a response it has not finished, and its client gets an empty response (measured on
/// zbook, 2026-11-21). The response in progress here waits on the test, so the test knows it is in progress when the
/// stop begins.
/// </para>
/// <para>
/// On Windows the listener's stop resets every connection http.sys still holds, and the client loses what it has not
/// yet read of its response; http.sys closes a connection gracefully only after a response that has a length and
/// closes it (measured on omega-windows, 2026-11-22: 30 of 30 runs each way). So the client here reads the body only
/// after the stop has returned, and the body is larger than what a client reads with the headers (32 KB; an 8-byte
/// body came with the headers and was not lost).
/// </para>
/// </remarks>
public sealed class HttpListenerLoopStopTests
{
    private static readonly byte[] Body = Enumerable.Range(0, 32 << 10).Select(i => (byte)('a' + i % 26)).ToArray();

    [Fact]
    public async Task AResponseInProgress_Finishes_TheStopWaitsForIt_AndARequestMeanwhileIsAnswered503()
    {
        var ct = TestContext.Current.CancellationToken;
        var inProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loop = FreePort.Start(
            port => new HttpListenerLoop([$"http://localhost:{port}/"], async (context, token) =>
            {
                inProgress.SetResult();
                await release.Task;
                return Body;
            }),
            loop => loop.Start(), out var port);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // The loop's disposal waits for the response in progress, so the response is let go whatever fails before:
        // a failure then fails the test, and does not hang it.
        try
        {
            var slow = http.GetAsync($"http://localhost:{port}/slow", HttpCompletionOption.ResponseHeadersRead, ct);
            await inProgress.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var stopping = loop.StopAsync();

            var meanwhile = await http.GetAsync($"http://localhost:{port}/other", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, meanwhile.StatusCode);
            Assert.True(meanwhile.Headers.ConnectionClose, "The 503 kept its connection.");
            Assert.False(stopping.IsCompleted, "The stop returned while a response was in progress.");

            release.SetResult();
            var answer = await slow;
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(Body.Length, answer.Content.Headers.ContentLength);
            Assert.True(answer.Headers.ConnectionClose, "The response written during the stop kept its connection.");
            await stopping.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.False(loop.IsListening);

            Assert.Equal(Body, await answer.Content.ReadAsByteArrayAsync(ct));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task AResponseBeforeTheStop_HasItsLength_AndKeepsItsConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var loop = FreePort.Start(
            port => new HttpListenerLoop([$"http://localhost:{port}/"], (context, token) => Task.FromResult(Body)),
            loop => loop.Start(), out var port);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var answer = await http.GetAsync($"http://localhost:{port}/", ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(Body.Length, answer.Content.Headers.ContentLength);
        Assert.NotEqual(true, answer.Headers.ConnectionClose);
        Assert.Equal(Body, await answer.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task AHeadRequest_IsAnsweredWithItsBodysLength_AndNothingAfterTheHead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var loop = FreePort.Start(
            port => new HttpListenerLoop([$"http://localhost:{port}/"], (context, token) => Task.FromResult(Body)),
            loop => loop.Start(), out var port);

        // A TCP client, not an HttpClient: an HttpClient drops a pooled connection that has data on it, and so never
        // reads what came after a HEAD's head. The request closes its connection, so the server ends it after the
        // response, and the client reads all there is. A body written for a HEAD comes after the head (Linux), or the
        // connection is reset (Windows; both measured on 2026-11-22). It connects to the name and tries each address
        // it resolves to, as an HTTP client does: on the CI runners the listener on "localhost" is not on 127.0.0.1
        // (CI 44995443554: a connection to 127.0.0.1 was refused on Linux and macOS; on zbook it was not).
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("localhost", port, ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HEAD / HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n"), ct);
        var received = new MemoryStream();
        await stream.CopyToAsync(received, ct);

        var text = Encoding.ASCII.GetString(received.ToArray());
        var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headEnd > 0, $"No head in: {text}");
        var head = text[..headEnd].Split("\r\n");
        Assert.Equal("HTTP/1.1 200 OK", head[0]);
        Assert.Contains($"Content-Length: {Body.Length}", head);
        Assert.Equal(headEnd + 4, text.Length);
    }

    [Fact]
    public async Task AStoppedLoop_IsDisposed_WithItsPortTakenByAnother()
    {
        // On Linux and macOS a listener's Close after its Stop looks each prefix up again and binds its port to do
        // so, and it threw "Address already in use" when the port was not free at that moment (Mercury.Tests in
        // parallel on zbook, 2026-11-21: from the Dispose after a stop). The port is taken here once the loop has
        // stopped, so a disposal that binds it throws.
        var loop = FreePort.Start(
            port => new HttpListenerLoop([$"http://localhost:{port}/"], (context, token) => Task.FromResult(Body)),
            loop => loop.Start(), out var port);
        await loop.StopAsync();
        var taker = new TcpListener(IPAddress.IPv6Any, port) { Server = { DualMode = true } };
        taker.Start();
        try
        {
            await loop.DisposeAsync();
            loop.Dispose();
        }
        finally
        {
            taker.Stop();
        }
    }
}
