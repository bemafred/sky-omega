// HttpListenerLoop.cs
// An HttpListener on one or more base URIs and the loop that accepts its requests: the one lifecycle of
// SparqlHttpServer and Mercury.Solid's SolidServer.
// No external dependencies, only BCL
// .NET 10 / C# 14

using System.Net;
using System.Text;

namespace SkyOmega.Mercury.Sparql.Protocol;

/// <summary>
/// An <see cref="HttpListener"/> on one or more base URIs and the loop that accepts their requests. Each request goes
/// to the server's respond function, which the loop does not wait for before it accepts the next. An exception the
/// function lets out is answered with 500, and the response is closed after the function either way.
/// </summary>
/// <remarks>
/// The URIs are one listener's prefixes: one port each, one accept loop for all (ADR-065 D4). A request is answered
/// when its Host is one of them. A start that fails on any of them fails whole, and leaves none listening.
/// </remarks>
internal sealed class HttpListenerLoop : IDisposable, IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, CancellationToken, Task> _respond;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    /// <param name="baseUris">The URIs to listen on, at least one (e.g., "http://localhost:8080/"). A trailing slash
    /// is added to one that has none, and a URI given twice is listened on once.</param>
    /// <param name="respond">Writes the response to one request. The loop closes the response after it.</param>
    public HttpListenerLoop(IEnumerable<string> baseUris, Func<HttpListenerContext, CancellationToken, Task> respond)
    {
        ArgumentNullException.ThrowIfNull(baseUris);
        BaseUris = baseUris
            .Select(uri => uri is null ? throw new ArgumentNullException(nameof(baseUris), "A URI to listen on is null.")
                : uri.EndsWith('/') ? uri : uri + "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (BaseUris.Count == 0)
            throw new ArgumentException("A listener needs a URI to listen on.", nameof(baseUris));
        _respond = respond;
        _listener = new HttpListener();
        foreach (var uri in BaseUris)
            _listener.Prefixes.Add(uri);
    }

    /// <summary>
    /// The URIs the listener listens on, each with its trailing slash, in the order given.
    /// </summary>
    public IReadOnlyList<string> BaseUris { get; }

    /// <summary>
    /// Whether the listener is listening.
    /// </summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>
    /// Starts the listener and the loop that accepts its requests.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A start that fails on any address unbinds the ones bound before it, and the listener closes itself (the
        // managed listener of Linux and macOS). The loop then has nothing to stop: a Stop of that listener would look
        // each prefix up again, bind the failed address to do so, and throw, and the start's exception would be lost
        // to it (measured on zbook, 2026-11-20). So the loop's state is made only once the listener has started.
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptTask = AcceptAsync(_cts.Token);
    }

    /// <summary>
    /// Stops the listener and waits for the loop to end. The token is cancelled before the listener stops, so the
    /// loop sees the stop when the listener's wait ends.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_acceptTask != null)
            await _acceptTask.ConfigureAwait(false);
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Respond in the background (not awaited), so the next request is accepted meanwhile
                _ = RespondAsync(context, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // A stop reaches this wait as the listener's exception, and which one depends on when the stop
                // came: a wait pending at Stop throws ObjectDisposedException, and a wait begun after Stop
                // throws InvalidOperationException (Linux and macOS).
                break;
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await _respond(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteErrorResponseAsync(context.Response, 500, "Internal Server Error", ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // The response may already be written or its connection gone
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>
    /// The error response both servers send: the status, and a JSON body with the status description and the message.
    /// </summary>
    public static async Task WriteErrorResponseAsync(HttpListenerResponse response, int statusCode,
        string statusDescription, string message)
    {
        response.StatusCode = statusCode;
        response.StatusDescription = statusDescription;
        response.ContentType = "application/json";

        var escapedMessage = message
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");

        var bytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{statusDescription}\",\"message\":\"{escapedMessage}\"}}");
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _listener.Close();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts?.Dispose();
    }
}
