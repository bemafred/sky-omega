// HttpListenerLoop.cs
// An HttpListener on one base URI and the loop that accepts its requests: the one lifecycle of
// SparqlHttpServer and Mercury.Solid's SolidServer.
// No external dependencies, only BCL
// .NET 10 / C# 14

using System.Net;
using System.Text;

namespace SkyOmega.Mercury.Sparql.Protocol;

/// <summary>
/// An <see cref="HttpListener"/> on one base URI and the loop that accepts its requests. Each request goes to the
/// server's respond function, which the loop does not wait for before it accepts the next. An exception the function
/// lets out is answered with 500, and the response is closed after the function either way.
/// </summary>
internal sealed class HttpListenerLoop : IDisposable, IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, CancellationToken, Task> _respond;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    /// <param name="baseUri">The URI to listen on (e.g., "http://localhost:8080/"); a trailing slash is added when it
    /// has none.</param>
    /// <param name="respond">Writes the response to one request. The loop closes the response after it.</param>
    public HttpListenerLoop(string baseUri, Func<HttpListenerContext, CancellationToken, Task> respond)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        BaseUri = baseUri.EndsWith('/') ? baseUri : baseUri + "/";
        _respond = respond;
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUri);
    }

    /// <summary>
    /// The URI the listener listens on, with its trailing slash.
    /// </summary>
    public string BaseUri { get; }

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

        _cts = new CancellationTokenSource();
        _listener.Start();
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
