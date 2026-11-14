using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>The shared visualization client — connects to the rendezvous Unix-domain socket, reads the NDJSON
/// stream (snapshot-on-connect then live deltas), parses each line with <see cref="WireCodec"/>, updates the
/// view-agnostic <see cref="Model"/>, and pushes each update to an <see cref="IDebugStateView"/>. Every view
/// (console / TUI / GUI) is a thin consumer of this one client (mirrors how <c>Mercury.Cli.Sparql</c> is a thin
/// shim over <c>Mercury.Sparql.Tool</c>). BCL-only; depends only on the wire contract, never the engine.</summary>
public sealed class DebugStateClient
{
    private readonly DebugStateClientOptions _options;

    /// <summary>Construct a client. <paramref name="options"/> defaults to the well-known rendezvous path.</summary>
    public DebugStateClient(DebugStateClientOptions? options = null)
    {
        _options = options ?? new DebugStateClientOptions();
        if (_options.ReconnectDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "ReconnectDelay must be positive");
        Model = new DebugStateClientModel(_options.DeltaHistory);
    }

    /// <summary>The client-side debug-state model, updated as messages arrive.</summary>
    public DebugStateClientModel Model { get; }

    /// <summary>Connect and stream until the server closes the connection or <paramref name="ct"/> is cancelled.
    /// Pushes <see cref="IDebugStateView.OnConnected"/>, then <see cref="IDebugStateView.OnSnapshot"/> /
    /// <see cref="IDebugStateView.OnDelta"/> per message, then <see cref="IDebugStateView.OnDisconnected"/> once.
    /// A failed connect surfaces as <c>OnDisconnected</c> (not an exception) so a view degrades cleanly when no
    /// session is up. Returns when the stream ends.
    ///
    /// <para>With <see cref="DebugStateClientOptions.Reconnect"/> it does not return at the end of a stream: it
    /// waits for the next session and connects to it, until <paramref name="ct"/> is cancelled. The view then gets
    /// <c>OnDisconnected</c> once per change, when a stream ends or when the first connect finds no session, and
    /// not again for each retry. <see cref="Model"/> keeps the ended session's state until the next one
    /// connects, and is reset just before that session's <c>OnConnected</c>.</para></summary>
    public async Task RunAsync(IDebugStateView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!_options.Reconnect)
        {
            view.OnDisconnected(await RunOnceAsync(view, announceFailedConnect: true, ct).ConfigureAwait(false));
            return;
        }

        bool waiting = false; // true once the view has been told there is no session
        while (true)
        {
            string? reason = await RunOnceAsync(view, announceFailedConnect: !waiting, ct).ConfigureAwait(false);
            if (reason is not null) { view.OnDisconnected(reason); waiting = true; }
            if (ct.IsCancellationRequested) return;
            try { await Task.Delay(_options.ReconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One connection: connect, stream to its end, and return why it ended. Returns null for a failed
    /// connect when <paramref name="announceFailedConnect"/> is false (the view already knows it is waiting).</summary>
    private async Task<string?> RunOnceAsync(IDebugStateView view, bool announceFailedConnect, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.SocketPath), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return announceFailedConnect ? "cancelled before connect" : null; }
        catch (Exception ex)
        {
            if (!announceFailedConnect) return null;
            // The socket exists only while a session is active (the server unlinks it at the end), so its absence
            // is the ordinary "no session" case; the OS error for it differs per platform and explains nothing.
            return File.Exists(_options.SocketPath)
                ? $"could not connect to {_options.SocketPath}: {ex.Message}"
                : $"no session (no socket at {_options.SocketPath})";
        }

        // A new connection is a new session with its own sequence numbers: nothing of the previous one carries over.
        Model.Reset();
        view.OnConnected(_options.SocketPath);

        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                WireMessage? message;
                try { message = WireCodec.Parse(line); }
                catch (JsonException) { continue; } // a malformed line must never tear down the view

                if (message is null) continue;
                if (message.Snapshot is { } snapshot) { Model.ApplySnapshot(snapshot, message.Seq); view.OnSnapshot(snapshot, Model); }
                else if (message.Delta is { } delta) { Model.ApplyDelta(delta, message.Seq); view.OnDelta(delta, Model); }
            }
            return "server closed the connection"; // EOF — the session ended
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception ex) { return ex.Message; }
    }
}
