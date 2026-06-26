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
        Model = new DebugStateClientModel(_options.DeltaHistory);
    }

    /// <summary>The client-side debug-state model, updated as messages arrive.</summary>
    public DebugStateClientModel Model { get; }

    /// <summary>Connect and stream until the server closes the connection or <paramref name="ct"/> is cancelled.
    /// Pushes <see cref="IDebugStateView.OnConnected"/>, then <see cref="IDebugStateView.OnSnapshot"/> /
    /// <see cref="IDebugStateView.OnDelta"/> per message, then <see cref="IDebugStateView.OnDisconnected"/> once.
    /// A failed connect surfaces as <c>OnDisconnected</c> (not an exception) so a view degrades cleanly when no
    /// session is up. Returns when the stream ends.</summary>
    public async Task RunAsync(IDebugStateView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.SocketPath), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { view.OnDisconnected("cancelled before connect"); return; }
        catch (Exception ex) { view.OnDisconnected($"could not connect to {_options.SocketPath}: {ex.Message}"); return; }

        view.OnConnected(_options.SocketPath);

        string? reason;
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
            reason = "server closed the connection"; // EOF — the session ended
        }
        catch (OperationCanceledException) { reason = "cancelled"; }
        catch (Exception ex) { reason = ex.Message; }

        view.OnDisconnected(reason);
    }
}
