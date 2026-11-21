// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using SkyOmega.Mercury.Sparql.Protocol;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The stop of the HTTP endpoint (ADR-065 D3): it takes no request after it begins, it lets the responses in progress
/// finish, and it returns only then, since the store they read is closed after it.
/// </summary>
/// <remarks>
/// The listener's own stop ends a response it has not finished, and its client gets an empty response (measured on
/// zbook, 2026-11-21). The response in progress here waits on the test, so the test knows it is in progress when the
/// stop begins.
/// </remarks>
public sealed class HttpListenerLoopStopTests
{
    [Fact]
    public async Task AResponseInProgress_Finishes_TheStopWaitsForIt_AndARequestMeanwhileIsAnswered503()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var inProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loop = new HttpListenerLoop([$"http://localhost:{port}/"], async (context, token) =>
        {
            if (context.Request.Url!.AbsolutePath == "/slow")
            {
                inProgress.SetResult();
                await release.Task;
            }
            await context.Response.OutputStream.WriteAsync("answered"u8.ToArray(), token);
        });
        loop.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // The loop's disposal waits for the response in progress, so the response is let go whatever fails before:
        // a failure then fails the test, and does not hang it.
        try
        {
            var slow = http.GetAsync($"http://localhost:{port}/slow", ct);
            await inProgress.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var stopping = loop.StopAsync();

            var meanwhile = await http.GetAsync($"http://localhost:{port}/other", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, meanwhile.StatusCode);
            Assert.False(stopping.IsCompleted, "The stop returned while a response was in progress.");

            release.SetResult();
            var answer = await slow;
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("answered", await answer.Content.ReadAsStringAsync(ct));
            await stopping.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.False(loop.IsListening);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
