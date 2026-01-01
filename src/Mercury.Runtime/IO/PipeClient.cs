// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Named pipe client for connecting to a remote Mercury REPL.
/// Used by CLI to attach to running MCP or other Mercury instances.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly string _serverName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Creates a new pipe client.
    /// </summary>
    /// <param name="pipeName">Name of the pipe to connect to.</param>
    /// <param name="serverName">Server name ("." for local).</param>
    public PipeClient(string pipeName, string serverName = ".")
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _serverName = serverName;
    }

    /// <summary>
    /// Whether the client is connected.
    /// </summary>
    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>
    /// The pipe name.
    /// </summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Connect to the pipe server.
    /// </summary>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PipeClient));

        if (_pipe != null)
            throw new InvalidOperationException("Already connected");

        _pipe = new NamedPipeClientStream(
            _serverName,
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            await _pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to pipe '{_pipeName}' timed out after {timeoutMs}ms");
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8);
        _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
    }

    /// <summary>
    /// Read the welcome message after connecting.
    /// </summary>
    public async Task<string> ReadWelcomeAsync(CancellationToken ct = default)
    {
        EnsureConnected();

        var sb = new StringBuilder();

        // Read until we get an empty line (end of welcome)
        while (true)
        {
            var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null || string.IsNullOrEmpty(line))
                break;
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Send a command and receive the response.
    /// </summary>
    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();

        await _writer!.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);

        // Read response until we see the prompt again
        var sb = new StringBuilder();

        while (true)
        {
            var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
                break;

            // Check if this is a prompt line (ends with "> ")
            if (line.EndsWith("> "))
            {
                // This is the next prompt - we're done
                break;
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Run an interactive session, proxying between local console and remote pipe.
    /// </summary>
    /// <param name="input">Local input (defaults to Console.In).</param>
    /// <param name="output">Local output (defaults to Console.Out).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunInteractiveAsync(
        TextReader? input = null,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        EnsureConnected();

        input ??= Console.In;
        output ??= Console.Out;

        // Read and display welcome
        var welcome = await ReadWelcomeAsync(ct).ConfigureAwait(false);
        output.WriteLine(welcome);

        // Interactive loop
        while (!ct.IsCancellationRequested && IsConnected)
        {
            // Read prompt from remote
            var prompt = await ReadUntilPromptAsync(ct).ConfigureAwait(false);
            if (prompt.output != null)
                output.Write(prompt.output);

            if (!IsConnected)
                break;

            output.Write(prompt.prompt);

            // Read local input
            var line = await Task.Run(() => input.ReadLine(), ct).ConfigureAwait(false);
            if (line == null)
            {
                // EOF - send detach
                await _writer!.WriteLineAsync(":detach".AsMemory(), ct).ConfigureAwait(false);
                break;
            }

            // Send to remote
            await _writer!.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);

            // Check for detach
            if (line.Trim().Equals(":detach", StringComparison.OrdinalIgnoreCase))
            {
                // Read final message
                var final = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
                if (final != null)
                    output.WriteLine(final);
                break;
            }
        }
    }

    private async Task<(string? output, string prompt)> ReadUntilPromptAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[1];

        while (!ct.IsCancellationRequested)
        {
            var read = await _reader!.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                // Connection closed
                return (sb.ToString(), "");
            }

            sb.Append(buffer[0]);

            // Check if we've received a prompt (ends with "> ")
            var current = sb.ToString();
            if (current.EndsWith("> "))
            {
                // Find where the prompt starts (look for newline before it or start)
                var promptStart = current.LastIndexOf('\n');
                if (promptStart >= 0)
                {
                    return (current[..promptStart], current[(promptStart + 1)..]);
                }
                else
                {
                    return (null, current);
                }
            }
        }

        return (sb.ToString(), "");
    }

    /// <summary>
    /// Disconnect from the pipe server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_writer != null && _pipe?.IsConnected == true)
        {
            try
            {
                await _writer.WriteLineAsync(":detach").ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors during disconnect
            }
        }

        Cleanup();
    }

    private void EnsureConnected()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PipeClient));

        if (_pipe == null || !_pipe.IsConnected)
            throw new InvalidOperationException("Not connected");
    }

    private void Cleanup()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();

        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Cleanup();
    }

    /// <summary>
    /// Check if a pipe server is available.
    /// </summary>
    public static async Task<bool> IsServerAvailableAsync(string pipeName, string serverName = ".", int timeoutMs = 1000)
    {
        try
        {
            using var client = new PipeClient(pipeName, serverName);
            await client.ConnectAsync(timeoutMs).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
