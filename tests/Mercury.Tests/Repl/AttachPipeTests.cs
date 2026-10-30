using System.IO.Pipes;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Tests.Repl;

/// <summary>
/// The attach pipe (<c>mercury --attach</c>) carries UTF-8 text with no byte order mark in either direction. A .NET
/// StreamReader drops a byte order mark, so each test reads the bytes at the other end of the pipe.
/// </summary>
public class AttachPipeTests
{
    [Fact]
    public async Task PipeServer_Welcome_IsTheFirstBytesOnThePipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"mt-{Guid.NewGuid():N}"[..16];
        using var server = new PipeServer(
            pipeName,
            () => new ReplSession(
                _ => throw new InvalidOperationException("no query in this test"),
                _ => throw new InvalidOperationException("no update in this test"),
                () => throw new InvalidOperationException("no statistics in this test"),
                () => []),
            welcomeMessage: "Welcome");
        server.Start();

        var first = new byte[7];
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000, ct);
            await client.ReadExactlyAsync(first, ct);
        }
        await server.StopAsync();

        Assert.Equal("Welcome"u8.ToArray(), first);
    }

    [Fact]
    public async Task PipeClient_Command_IsTheFirstBytesOnThePipe()
    {
        // A Windows named pipe with no buffer completes a write only when the other end has read all of it, so the
        // server side reads the whole command line (CRLF on Windows) before it answers; reading part of it and writing
        // back deadlocked there. Every wait is under one 30-second limit, so a regression fails instead of hanging.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var pipeName = $"mt-{Guid.NewGuid():N}"[..16];
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connecting = server.WaitForConnectionAsync(ct);
        using var client = new PipeClient(pipeName);
        await client.ConnectAsync(5000, ct);
        await connecting;

        // SendCommandAsync reads the response up to the next prompt, which the server side sends after the command.
        var sending = client.SendCommandAsync(":stats", ct);
        var first = new byte[6];
        await server.ReadExactlyAsync(first, ct);
        var next = new byte[1];
        do
            await server.ReadExactlyAsync(next, ct);
        while (next[0] != (byte)'\n');
        await server.WriteAsync("mcp> \n"u8.ToArray(), ct);
        await sending;

        Assert.Equal(":stats"u8.ToArray(), first);
    }
}
