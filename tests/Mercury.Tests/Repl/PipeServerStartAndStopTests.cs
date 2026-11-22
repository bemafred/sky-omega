// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Tests.Repl;

/// <summary>
/// The attach pipe's start and stop (ADR-065 D3): a pipe that cannot be made fails the start, and a stop returns only
/// when no session uses the store, which is closed after it.
/// </summary>
public sealed class PipeServerStartAndStopTests
{
    private static ReplSession SessionWhoseStatisticsAre(Func<StoreStatistics> statistics) => new(
        _ => throw new InvalidOperationException("no query in this test"),
        _ => throw new InvalidOperationException("no update in this test"),
        statistics,
        () => []);

    [Fact]
    public async Task APipeThatCannotBeMade_FailsTheStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"mt-{Guid.NewGuid():N}"[..16];
        // A pipe of this name that allows one instance, and has it: no other instance can be made.
        await using var taken = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var server = new PipeServer(pipeName, () => SessionWhoseStatisticsAre(() => new StoreStatistics()));

        // Before the fix the start made the pipe again and again, for ever: the limit makes that a failure, not a hang.
        var error = await Record.ExceptionAsync(() => Task.Run(server.Start, ct).WaitAsync(TimeSpan.FromSeconds(30), ct));

        Assert.True(error is IOException or UnauthorizedAccessException, $"The start ended with {error?.GetType().Name ?? "no exception"}.");
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task ACommandInProgress_RunsToItsEnd_AndItsOutputIsWritten_BeforeTheStopReturns()
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var pipeName = $"mt-{Guid.NewGuid():N}"[..16];
        var inProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var server = new PipeServer(pipeName, () => SessionWhoseStatisticsAre(() =>
        {
            inProgress.SetResult();
            release.Wait(ct); // the command reads the store until the test lets it end
            return new StoreStatistics { QuadCount = 777 };
        }), welcomeMessage: "Welcome", prompt: "test> ");
        server.Start();
        // The command is let go whatever fails before, so a failure fails the test and leaves no session waiting.
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000, ct);
            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await ReadUntil(reader, "test> ", ct);
            // The writer is closed once the command is written. The session's end breaks the pipe, and a writer
            // disposed after it flushes into the broken pipe and throws (omega-windows, Mercury.Tests in parallel,
            // 2026-11-22).
            await using (var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
                await writer.WriteLineAsync(":stats".AsMemory(), ct);
            await inProgress.Task.WaitAsync(ct);

            var stopping = server.StopAsync();
            await Task.Delay(300, ct);
            Assert.False(stopping.IsCompleted, "The stop returned while a session ran a command.");

            release.Set();
            Assert.Contains("777", await reader.ReadToEndAsync(ct)); // the output, then the end of the session
            await stopping.WaitAsync(ct);
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task<string> ReadUntil(StreamReader reader, string end, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new char[1];
        while (!text.ToString().EndsWith(end, StringComparison.Ordinal))
        {
            if (await reader.ReadAsync(one.AsMemory(), ct) == 0)
                break;
            text.Append(one[0]);
        }
        return text.ToString();
    }
}
