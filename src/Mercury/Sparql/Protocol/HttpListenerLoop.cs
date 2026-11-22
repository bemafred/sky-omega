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
/// to the server's respond function, which the loop does not wait for before it accepts the next. The function sets the
/// response's status and headers and returns its body, and the loop writes the body and closes the response. An
/// exception the function lets out is answered with 500.
/// </summary>
/// <remarks>
/// <para>
/// The URIs are one listener's prefixes: one port each, one accept loop for all (ADR-065 D4). A request is answered
/// when its Host is one of them. A start that fails on any of them fails whole, and leaves none listening.
/// </para>
/// <para>
/// A stop lets the responses in progress finish before the listener stops (ADR-065 D3): what they read is closed
/// after the stop, mercuryd's store among it.
/// </para>
/// <para>
/// Every body is written with its length, and a response written once the stop has begun closes its connection. On
/// Windows the listener's stop resets every connection http.sys still holds, and its client loses what it has not yet
/// read; http.sys closes a connection gracefully only after a response that has a length and closes it, and the stop
/// does not reset that one (measured on omega-windows, 2026-11-22). A response written before the stop began keeps its
/// connection, and on Windows the stop can still take it from a client that has not read it
/// (<c>docs/limits/http-stop-resets-keep-alive-connections-on-windows.md</c>).
/// </para>
/// </remarks>
internal sealed class HttpListenerLoop : IDisposable, IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, CancellationToken, Task<byte[]>> _respond;
    private readonly HashSet<Task> _responding = [];
    private readonly CancellationTokenSource _disposing = new();
    private Task? _acceptTask;
    private volatile bool _stopping;
    private bool _disposed;

    /// <param name="baseUris">The URIs to listen on, at least one (e.g., "http://localhost:8080/"). A trailing slash
    /// is added to one that has none, and a URI given twice is listened on once.</param>
    /// <param name="respond">Sets the status and headers of the response to one request, and returns its body, which
    /// the loop writes (for a HEAD request, the loop sends its length and not the body). Its token is cancelled when
    /// the loop is disposed, not when it stops.</param>
    public HttpListenerLoop(IEnumerable<string> baseUris, Func<HttpListenerContext, CancellationToken, Task<byte[]>> respond)
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
        // to it (measured on zbook, 2026-11-20). So the loop is made only once the listener has started.
        _listener.Start();
        _stopping = false;
        _acceptTask = AcceptAsync();
    }

    /// <summary>
    /// Stops taking requests, waits for the responses in progress, then closes the listener and waits for the loop to
    /// end. A request that comes while the responses finish is answered 503. A stopped loop is not started again.
    /// </summary>
    /// <remarks>
    /// The listener's stop ends the responses it has not finished: a write after it throws ObjectDisposedException,
    /// and the client gets an empty response (measured on zbook, 2026-11-21). So they finish first, and each closes its
    /// connection, which on Windows keeps the stop from resetting it (the class's remarks). The wait has no bound of
    /// its own: the bound is the service manager's, which then kills the process (ADR-065 D3).
    /// <para>
    /// The listener is closed, not stopped. On Linux and macOS a listener's Close after its Stop looks each prefix up
    /// again and binds its port to do so, and throws if the port is not free at that moment (measured on zbook,
    /// 2026-11-22), so a disposal after the stop could throw. A closed listener's Close does nothing.
    /// </para>
    /// </remarks>
    public async Task StopAsync()
    {
        if (_acceptTask == null)
            return;

        Task[] inProgress;
        lock (_responding)
        {
            _stopping = true;
            inProgress = [.. _responding];
        }
        await Task.WhenAll(inProgress).ConfigureAwait(false);

        _listener.Close();
        await _acceptTask.ConfigureAwait(false);
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping)
            {
                // A stop reaches this wait as the listener's exception, whichever it is: the close ends a wait
                // pending at it, and a wait begun after it throws.
                break;
            }

            // A response begins under the lock that the stop takes its list under, so the stop waits for each one
            // that began before it, and none begins after it.
            lock (_responding)
            {
                if (!_stopping)
                {
                    var responding = Task.Run(() => RespondAsync(context));
                    _responding.Add(responding);
                    _ = responding.ContinueWith(ended => { lock (_responding) _responding.Remove(ended); },
                        TaskScheduler.Default);
                    continue;
                }
            }
            _ = RefuseAsync(context);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        byte[] body;
        try
        {
            body = await _respond(context, _disposing.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            body = ErrorResponse(context.Response, 500, "Internal Server Error", ex.Message);
        }
        await WriteAsync(context, body).ConfigureAwait(false);
    }

    // A request that came while the stop waits for the responses in progress.
    private async Task RefuseAsync(HttpListenerContext context)
    {
        byte[] body;
        try
        {
            body = ErrorResponse(context.Response, 503, "Service Unavailable", "The server is stopping.");
        }
        catch (ObjectDisposedException)
        {
            // The stop stopped the listener meanwhile, and it ended the response
            return;
        }
        await WriteAsync(context, body).ConfigureAwait(false);
    }

    // The body, with its length, and once the stop has begun the connection's close after it (the class's remarks).
    private async Task WriteAsync(HttpListenerContext context, byte[] body)
    {
        var response = context.Response;
        try
        {
            if (_stopping)
                response.KeepAlive = false;
            response.ContentLength64 = body.Length;
            if (context.Request.HttpMethod != "HEAD")
                await response.OutputStream.WriteAsync(body, _disposing.Token).ConfigureAwait(false);
            response.Close();
        }
        catch (Exception)
        {
            // The client is gone, or the listener stopped or was disposed: the response cannot be written, and its
            // connection is ended. The stop waits for this task, so nothing is let out of it.
            response.Abort();
        }
    }

    /// <summary>
    /// The error response both servers send: sets the status, and returns a JSON body with the status description and
    /// the message.
    /// </summary>
    public static byte[] ErrorResponse(HttpListenerResponse response, int statusCode, string statusDescription,
        string message)
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

        return Encoding.UTF8.GetBytes($"{{\"error\":\"{statusDescription}\",\"message\":\"{escapedMessage}\"}}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopping = true;
        _disposing.Cancel();
        _listener.Close();
        _disposing.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _stopping = true;
        _disposing.Cancel();
        _listener.Close();
        _disposing.Dispose();
    }
}
