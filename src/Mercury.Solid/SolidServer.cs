// SolidServer.cs
// Solid Protocol HTTP server implementation.
// Based on W3C Solid Protocol specification.
// https://solidproject.org/TR/protocol
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Net;
using System.Text;
using SkyOmega.Mercury.Solid.AccessControl;
using SkyOmega.Mercury.Solid.Http;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Solid;

/// <summary>
/// Solid Protocol HTTP server using BCL HttpListener.
/// Implements W3C Solid Protocol for resource CRUD operations.
/// </summary>
/// <remarks>
/// Endpoints:
/// - GET /path/to/resource - Read resource
/// - PUT /path/to/resource - Create or replace resource
/// - PATCH /path/to/resource - Modify resource (N3 Patch)
/// - DELETE /path/to/resource - Remove resource
/// - POST /path/to/container/ - Create resource in container
/// - HEAD /path/to/resource - Get resource metadata
///
/// Also supports SPARQL endpoints:
/// - GET/POST /sparql - SPARQL query
/// - POST /sparql/update - SPARQL Update (if enabled)
/// </remarks>
public sealed class SolidServer : IDisposable, IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly QuadStore _store;
    private readonly string _baseUri;
    private readonly SolidServerOptions _options;
    private readonly IAccessPolicy _accessPolicy;
    private readonly ResourceHandler _resourceHandler;
    private readonly ContainerHandler _containerHandler;
    private readonly PatchHandler _patchHandler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new Solid HTTP server.
    /// </summary>
    /// <param name="store">The QuadStore to use for RDF storage.</param>
    /// <param name="baseUri">Base URI to listen on (e.g., "http://localhost:8080/").</param>
    /// <param name="options">Server configuration options.</param>
    public SolidServer(QuadStore store, string baseUri, SolidServerOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _options = options ?? new SolidServerOptions();

        if (!_baseUri.EndsWith("/"))
            _baseUri += "/";

        // Create access policy
        _accessPolicy = _options.AccessPolicy ?? AccessPolicyFactory.CreateAllowAll();

        // Create handlers
        _resourceHandler = new ResourceHandler(_store, _accessPolicy);
        _containerHandler = new ContainerHandler(_store, _accessPolicy);
        _patchHandler = new PatchHandler(_store, _accessPolicy);

        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUri);
    }

    /// <summary>
    /// Start the HTTP server and begin accepting requests.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = AcceptConnectionsAsync(_cts.Token);
    }

    /// <summary>
    /// Stop the HTTP server gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _listener.Stop();

            if (_listenTask != null)
            {
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
            }
        }
    }

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>
    /// The base URI the server is listening on.
    /// </summary>
    public string BaseUri => _baseUri;

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Handle request in background (don't await)
                _ = HandleRequestAsync(context, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Add CORS headers if enabled
            if (_options.EnableCors)
            {
                response.Headers.Add("Access-Control-Allow-Origin", _options.CorsOrigin);
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept, Authorization, Slug, Link, If-Match, If-None-Match");
                response.Headers.Add("Access-Control-Expose-Headers", "Location, ETag, Link, Accept-Patch, Accept-Post, WAC-Allow");
            }

            // Handle preflight
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";

            // Route to SPARQL endpoints if enabled
            if (_options.EnableSparql && path.EndsWith("/sparql", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSparqlRequestAsync(request, response, ct).ConfigureAwait(false);
                return;
            }

            if (_options.EnableSparql && _options.EnableSparqlUpdate &&
                path.EndsWith("/sparql/update", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSparqlUpdateRequestAsync(request, response, ct).ConfigureAwait(false);
                return;
            }

            // Extract resource URI from path
            var resourceUri = _baseUri.TrimEnd('/') + path;

            // Extract agent WebID from authentication (simplified - real implementation needs Solid-OIDC)
            var agentWebId = ExtractAgentWebId(request);

            // Route based on HTTP method
            ResourceResult result;

            switch (request.HttpMethod)
            {
                case "GET":
                    if (resourceUri.EndsWith("/"))
                    {
                        result = await _containerHandler.HandleGetAsync(
                            resourceUri,
                            agentWebId,
                            request.Headers["Accept"],
                            ct);
                    }
                    else
                    {
                        result = await _resourceHandler.HandleGetAsync(
                            resourceUri,
                            agentWebId,
                            request.Headers["Accept"],
                            ct);
                    }
                    break;

                case "HEAD":
                    // Same as GET but don't send body
                    if (resourceUri.EndsWith("/"))
                    {
                        result = await _containerHandler.HandleGetAsync(
                            resourceUri,
                            agentWebId,
                            request.Headers["Accept"],
                            ct);
                    }
                    else
                    {
                        result = await _resourceHandler.HandleGetAsync(
                            resourceUri,
                            agentWebId,
                            request.Headers["Accept"],
                            ct);
                    }
                    // Clear body for HEAD
                    result = new ResourceResult
                    {
                        StatusCode = result.StatusCode,
                        StatusDescription = result.StatusDescription,
                        ContentType = result.ContentType,
                        ETag = result.ETag,
                        LastModified = result.LastModified,
                        LinkHeader = result.LinkHeader,
                        Body = null // No body for HEAD
                    };
                    break;

                case "PUT":
                    result = await _resourceHandler.HandlePutAsync(
                        resourceUri,
                        agentWebId,
                        request.ContentType ?? "text/turtle",
                        request.InputStream,
                        request.Headers["If-Match"],
                        request.Headers["If-None-Match"],
                        ct);
                    break;

                case "PATCH":
                    result = await _patchHandler.HandlePatchAsync(
                        resourceUri,
                        agentWebId,
                        request.ContentType ?? "text/n3",
                        request.InputStream,
                        request.Headers["If-Match"],
                        ct);
                    break;

                case "DELETE":
                    result = await _resourceHandler.HandleDeleteAsync(
                        resourceUri,
                        agentWebId,
                        ct);
                    break;

                case "POST":
                    // POST to container creates a new resource
                    if (!resourceUri.EndsWith("/"))
                        resourceUri += "/";

                    result = await _containerHandler.HandlePostAsync(
                        resourceUri,
                        agentWebId,
                        request.ContentType ?? "text/turtle",
                        request.InputStream,
                        request.Headers["Slug"],
                        request.Headers["Link"],
                        ct);
                    break;

                default:
                    await WriteErrorResponseAsync(response, 405, "Method Not Allowed",
                        $"Method {request.HttpMethod} not supported").ConfigureAwait(false);
                    return;
            }

            // Send response
            await SendResourceResultAsync(response, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteErrorResponseAsync(response, 500, "Internal Server Error", ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors writing error response
            }
        }
        finally
        {
            response.Close();
        }
    }

    private async Task SendResourceResultAsync(HttpListenerResponse response, ResourceResult result, CancellationToken ct)
    {
        response.StatusCode = result.StatusCode;
        response.StatusDescription = result.StatusDescription ?? "OK";

        if (result.ContentType != null)
            response.ContentType = result.ContentType;

        if (result.ETag != null)
            response.Headers.Add("ETag", result.ETag);

        if (result.LastModified.HasValue)
            response.Headers.Add("Last-Modified", result.LastModified.Value.ToString("R"));

        if (result.Location != null)
            response.Headers.Add("Location", result.Location);

        if (result.LinkHeader != null)
            response.Headers.Add("Link", result.LinkHeader);

        // Add Accept-Patch header
        response.Headers.Add("Accept-Patch", "text/n3, application/n3-patch");

        // Add Accept-Post header for containers
        if (response.Headers["Link"]?.Contains("BasicContainer") == true)
        {
            response.Headers.Add("Accept-Post", "text/turtle, application/ld+json, application/n-triples");
        }

        if (result.ErrorMessage != null)
        {
            await WriteErrorResponseAsync(response, result.StatusCode, result.StatusDescription ?? "Error", result.ErrorMessage).ConfigureAwait(false);
            return;
        }

        if (result.Body != null && result.Body.Length > 0)
        {
            await response.OutputStream.WriteAsync(result.Body, ct).ConfigureAwait(false);
        }
    }

    private string? ExtractAgentWebId(HttpListenerRequest request)
    {
        // Simplified authentication - real implementation needs Solid-OIDC
        // For now, check for a WebID header or Bearer token

        var authHeader = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader))
            return null;

        // Very simplified - real implementation needs DPoP token validation
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // In a real implementation, validate the token and extract WebID
            // For now, we just return null (unauthenticated)
            return null;
        }

        // Check for WebID header (development/testing only)
        var webIdHeader = request.Headers["X-WebID"];
        if (!string.IsNullOrEmpty(webIdHeader))
        {
            return webIdHeader;
        }

        return null;
    }

    private async Task HandleSparqlRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        // Delegate to SPARQL endpoint handling
        // This is a simplified implementation - real implementation would use SparqlHttpServer

        await WriteErrorResponseAsync(response, 501, "Not Implemented",
            "SPARQL endpoint not yet implemented - use dedicated SparqlHttpServer").ConfigureAwait(false);
    }

    private async Task HandleSparqlUpdateRequestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await WriteErrorResponseAsync(response, 501, "Not Implemented",
            "SPARQL Update endpoint not yet implemented").ConfigureAwait(false);
    }

    private static async Task WriteErrorResponseAsync(HttpListenerResponse response, int statusCode,
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

/// <summary>
/// Configuration options for SolidServer.
/// </summary>
public sealed class SolidServerOptions
{
    /// <summary>
    /// The access policy to use for authorization.
    /// Default: AllowAll (development mode).
    /// </summary>
    public IAccessPolicy? AccessPolicy { get; set; }

    /// <summary>
    /// Whether to enable CORS headers for browser access.
    /// Default: true.
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// CORS origin value (Access-Control-Allow-Origin header).
    /// Default: "*" (allow all origins).
    /// </summary>
    public string CorsOrigin { get; set; } = "*";

    /// <summary>
    /// Whether to enable SPARQL endpoint.
    /// Default: false.
    /// </summary>
    public bool EnableSparql { get; set; } = false;

    /// <summary>
    /// Whether to enable SPARQL Update endpoint.
    /// Requires EnableSparql to be true.
    /// Default: false.
    /// </summary>
    public bool EnableSparqlUpdate { get; set; } = false;

    /// <summary>
    /// Maximum request body size in bytes.
    /// Default: 10MB.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Request timeout in milliseconds.
    /// Default: 30000 (30 seconds).
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 30000;
}
