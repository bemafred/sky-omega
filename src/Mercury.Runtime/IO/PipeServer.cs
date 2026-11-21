// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Named pipe server that accepts REPL connections.
/// Allows remote CLI access to a running Mercury instance.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<ReplSession> _sessionFactory;
    private readonly string _welcomeMessage;
    private readonly string _prompt;
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new pipe server.
    /// </summary>
    /// <param name="pipeName">Name of the pipe (e.g., "mercury-mcp").</param>
    /// <param name="sessionFactory">Factory to create ReplSession instances for each connection.</param>
    /// <param name="welcomeMessage">Welcome message for connecting clients.</param>
    /// <param name="prompt">Prompt to show clients.</param>
    public PipeServer(
        string pipeName,
        Func<ReplSession> sessionFactory,
        string? welcomeMessage = null,
        string prompt = "mcp> ")
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _welcomeMessage = welcomeMessage ?? $"Connected to Mercury pipe: {pipeName}";
        _prompt = prompt;
    }

    /// <summary>
    /// Whether the server is running.
    /// </summary>
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    /// <summary>
    /// The pipe name this server listens on.
    /// </summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Start accepting connections.
    /// </summary>
    /// <exception cref="IOException">The pipe cannot be made: on Linux and macOS, its socket's path is taken by a
    /// directory, or a pipe of this process allows no more instances.</exception>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PipeServer));

        if (_cts != null)
            throw new InvalidOperationException("Server already started");

        // The first pipe is made here, so one that cannot be made fails the start (ADR-065 D3). Made in the accept
        // loop, its error was taken for a connection's, and the loop made it again at once, for ever, inside Start:
        // the loop does not leave Start until its first wait.
        var first = CreatePipe();
        _cts = new CancellationTokenSource();
        _acceptTask = AcceptConnectionsAsync(first, _cts.Token);
    }

    /// <summary>
    /// Stops accepting connections, and waits for the sessions to end. A session waiting for its next line ends at
    /// once; one running a command ends when the command has run and its output is written.
    /// </summary>
    /// <remarks>
    /// The store the sessions use is closed after the stop, so the stop returns only when no session uses it (ADR-065
    /// D3). The wait has no bound of its own: the bound is the service manager's, which then kills the process, and
    /// the next open of the store replays its log (ADR-058).
    /// </remarks>
    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        await _cts.CancelAsync();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // The accept loop has ended, so no session is added after this.
        await Task.WhenAll(_sessions.Keys).ConfigureAwait(false);

        _cts.Dispose();
        _cts = null;
        _acceptTask = null;
    }

    private NamedPipeServerStream CreatePipe() => new(
        _pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private async Task AcceptConnectionsAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        NamedPipeServerStream? next = first;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = next ?? CreatePipe();
                next = null;

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // The session runs beside the loop, and the stop waits for it.
                var session = HandleSessionAsync(pipe, ct);
                _sessions.TryAdd(session, 0);
                _ = session.ContinueWith(ended => _sessions.TryRemove(ended, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception)
            {
                pipe?.Dispose();
                // Continue accepting on transient errors
            }
        }
    }

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe) { AutoFlush = true };

                // Send welcome
                await writer.WriteLineAsync(_welcomeMessage).ConfigureAwait(false);
                await writer.WriteLineAsync("Type :detach to disconnect").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                // Create session for this connection
                using var session = _sessionFactory();
                var formatter = new ResultTableFormatter(writer, useColor: false);

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    await writer.WriteAsync(_prompt).ConfigureAwait(false);

                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                    if (line == null)
                        break; // Client disconnected

                    if (line.Trim().Equals(":detach", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("Detaching...").ConfigureAwait(false);
                        break;
                    }

                    var result = session.Execute(line);

                    // Check for exit command - tell user to use :detach
                    if (result.Kind == ExecutionResultKind.Command && result.Message == "EXIT")
                    {
                        await writer.WriteLineAsync("Use :detach to disconnect from pipe session").ConfigureAwait(false);
                        continue;
                    }

                    // Format output using shared formatter
                    FormatResult(result, writer, formatter);
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected - expected
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Server stopping - expected
        }
    }

    private static void FormatResult(ExecutionResult result, TextWriter output, ResultTableFormatter formatter)
    {
        switch (result.Kind)
        {
            case ExecutionResultKind.Empty:
                break;

            case ExecutionResultKind.Select:
                formatter.FormatSelect(result);
                break;

            case ExecutionResultKind.Ask:
                formatter.FormatAsk(result);
                break;

            case ExecutionResultKind.Construct:
            case ExecutionResultKind.Describe:
                formatter.FormatTriples(result);
                break;

            case ExecutionResultKind.Update:
                formatter.FormatUpdate(result);
                break;

            case ExecutionResultKind.PrefixRegistered:
            case ExecutionResultKind.BaseSet:
            case ExecutionResultKind.Command:
                if (!string.IsNullOrEmpty(result.Message))
                    output.WriteLine(result.Message);
                break;

            case ExecutionResultKind.Error:
                formatter.FormatError(result);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
