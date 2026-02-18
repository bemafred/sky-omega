// ResourceHandler.cs
// HTTP handler for Solid resource operations (GET, PUT, DELETE).
// Based on W3C Solid Protocol.
// https://solidproject.org/TR/protocol
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Solid.AccessControl;
using SkyOmega.Mercury.Solid.Models;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Solid.Http;

/// <summary>
/// Handles GET, PUT, DELETE operations for Solid resources.
/// </summary>
public sealed class ResourceHandler
{
    private readonly QuadStore _store;
    private readonly IAccessPolicy _accessPolicy;

    // Common RDF predicates
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string DctermsModified = "http://purl.org/dc/terms/modified";

    public ResourceHandler(QuadStore store, IAccessPolicy accessPolicy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
    }

    /// <summary>
    /// Handles GET requests for a resource.
    /// </summary>
    public async Task<ResourceResult> HandleGetAsync(
        string resourceUri,
        string? agentWebId,
        string? acceptHeader,
        CancellationToken ct = default)
    {
        // Check access
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, resourceUri, AccessMode.Read, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Check if resource exists
        var graphUri = $"<{resourceUri}>";
        _store.AcquireReadLock();
        try
        {
            // Query for any triples in this graph
            var results = _store.QueryCurrent(null, null, null, resourceUri);
            if (!results.MoveNext())
            {
                results.Dispose();
                return ResourceResult.NotFound($"Resource not found: {resourceUri}");
            }

            // Determine output format
            var format = RdfFormat.Turtle;
            if (!string.IsNullOrEmpty(acceptHeader))
            {
                format = RdfFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), RdfFormat.Turtle);
            }

            // Serialize the graph
            using var memStream = new MemoryStream();
            using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

            var contentType = RdfFormatNegotiator.GetContentType(format);
            var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            int tripleCount = 0;

            switch (format)
            {
                case RdfFormat.Turtle:
                    {
                        using var turtleWriter = new TurtleStreamWriter(writer);
                        turtleWriter.WritePrefixes();

                        // Write the first triple we already read
                        do
                        {
                            var current = results.Current;
                            turtleWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                            tripleCount++;
                        }
                        while (results.MoveNext());
                    }
                    break;

                case RdfFormat.NTriples:
                    {
                        using var ntWriter = new NTriplesStreamWriter(writer);

                        do
                        {
                            var current = results.Current;
                            ntWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                            tripleCount++;
                        }
                        while (results.MoveNext());
                    }
                    break;

                default:
                    // Default to N-Triples for unsupported formats
                    {
                        using var defaultWriter = new NTriplesStreamWriter(writer);

                        do
                        {
                            var current = results.Current;
                            defaultWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                            tripleCount++;
                        }
                        while (results.MoveNext());
                    }
                    break;
            }

            results.Dispose();
            writer.Flush();

            // Generate ETag from content hash
            var hashBytes = hasher.GetHashAndReset();
            var etag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";

            // Get last modified from metadata (if stored)
            var lastModified = GetLastModified(resourceUri);

            // Get ACL URI
            var aclUri = _accessPolicy.GetAccessControlDocumentUri(resourceUri);

            // Build Link header
            var isContainer = resourceUri.EndsWith("/");
            var linkHeader = LinkHeaderBuilder.ForResource(
                new SolidResource
                {
                    Uri = resourceUri,
                    ContentType = contentType,
                    IsRdfSource = true,
                    IsContainer = isContainer
                },
                aclUri);

            return ResourceResult.Success(
                memStream.ToArray(),
                contentType,
                etag,
                lastModified,
                linkHeader);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    /// <summary>
    /// Handles PUT requests to create or replace a resource.
    /// </summary>
    public async Task<ResourceResult> HandlePutAsync(
        string resourceUri,
        string? agentWebId,
        string contentType,
        Stream body,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken ct = default)
    {
        // Check access
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, resourceUri, AccessMode.Write, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Parse content type
        var format = RdfFormatNegotiator.FromContentType(contentType.AsSpan());
        if (format == RdfFormat.Unknown)
        {
            return ResourceResult.UnsupportedMediaType($"Unsupported content type: {contentType}");
        }

        // Check preconditions
        bool resourceExists;
        string? currentETag = null;

        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, resourceUri);
            resourceExists = results.MoveNext();
            if (resourceExists)
            {
                // Compute current ETag
                var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                do
                {
                    var current = results.Current;
                    UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                }
                while (results.MoveNext());

                var hashBytes = hasher.GetHashAndReset();
                currentETag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";
            }
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        // Validate If-Match
        if (ifMatch != null && resourceExists)
        {
            if (ifMatch != "*" && ifMatch != currentETag)
            {
                return ResourceResult.PreconditionFailed("ETag mismatch");
            }
        }

        // Validate If-None-Match
        if (ifNoneMatch != null)
        {
            if (ifNoneMatch == "*" && resourceExists)
            {
                return ResourceResult.PreconditionFailed("Resource already exists");
            }
            if (ifNoneMatch == currentETag)
            {
                return ResourceResult.PreconditionFailed("ETag matched");
            }
        }

        // Parse incoming RDF
        List<(string Subject, string Predicate, string Object)> triples;
        try
        {
            triples = await RdfEngine.ParseTriplesAsync(body, format, resourceUri, ct);
        }
        catch (Exception ex)
        {
            return ResourceResult.BadRequest($"Invalid RDF: {ex.Message}");
        }

        // Replace the resource (delete old, insert new)
        _store.BeginBatch();
        try
        {
            // Delete existing triples in this graph
            if (resourceExists)
            {
                DeleteGraphTriples(resourceUri);
            }

            // Insert new triples
            foreach (var (subject, predicate, obj) in triples)
            {
                _store.AddCurrentBatched(subject, predicate, obj, resourceUri);
            }

            // Add modified timestamp
            var now = DateTimeOffset.UtcNow;
            var modified = $"\"{now:yyyy-MM-ddTHH:mm:ss.fffZ}\"^^<http://www.w3.org/2001/XMLSchema#dateTime>";
            _store.AddCurrentBatched($"<{resourceUri}>", DctermsModified, modified, resourceUri);

            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        // Compute new ETag
        var newHasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var (s, p, o) in triples)
        {
            UpdateHash(newHasher, s, p, o);
        }
        var newHashBytes = newHasher.GetHashAndReset();
        var newETag = $"\"{Convert.ToHexString(newHashBytes).ToLowerInvariant()}\"";

        var aclUri = _accessPolicy.GetAccessControlDocumentUri(resourceUri);
        var linkHeader = LinkHeaderBuilder.ForResource(
            new SolidResource
            {
                Uri = resourceUri,
                ContentType = contentType,
                IsRdfSource = true,
                IsContainer = resourceUri.EndsWith("/")
            },
            aclUri);

        return resourceExists
            ? ResourceResult.NoContent(newETag, linkHeader)
            : ResourceResult.Created(resourceUri, newETag, linkHeader);
    }

    /// <summary>
    /// Handles DELETE requests to remove a resource.
    /// </summary>
    public async Task<ResourceResult> HandleDeleteAsync(
        string resourceUri,
        string? agentWebId,
        CancellationToken ct = default)
    {
        // Check access
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, resourceUri, AccessMode.Write, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Check if resource exists
        bool resourceExists;
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, resourceUri);
            resourceExists = results.MoveNext();
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (!resourceExists)
        {
            return ResourceResult.NotFound($"Resource not found: {resourceUri}");
        }

        // Delete all triples in the graph
        _store.BeginBatch();
        try
        {
            DeleteGraphTriples(resourceUri);
            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        return ResourceResult.NoContent();
    }

    private void DeleteGraphTriples(string graphUri)
    {
        // Query all triples in the graph and delete them.
        // Read lock required around QueryCurrent enumeration (ADR-020 single-writer contract).
        var toDelete = new List<(string Subject, string Predicate, string Object)>();

        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, graphUri);
            while (results.MoveNext())
            {
                var current = results.Current;
                toDelete.Add((current.Subject.ToString(), current.Predicate.ToString(), current.Object.ToString()));
            }
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        foreach (var (s, p, o) in toDelete)
        {
            _store.DeleteCurrentBatched(s, p, o, graphUri);
        }
    }

    private DateTimeOffset? GetLastModified(string resourceUri)
    {
        // Look for dcterms:modified on the resource
        var results = _store.QueryCurrent($"<{resourceUri}>", DctermsModified, null, resourceUri);
        try
        {
            if (results.MoveNext())
            {
                var modified = results.Current.Object.ToString();
                // Parse xsd:dateTime literal
                if (modified.StartsWith("\"") && modified.Contains("^^"))
                {
                    var quote2 = modified.IndexOf('"', 1);
                    if (quote2 > 1)
                    {
                        var dateStr = modified.Substring(1, quote2 - 1);
                        if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        {
                            return dt;
                        }
                    }
                }
            }
        }
        finally
        {
            results.Dispose();
        }
        return null;
    }

    private static void UpdateHash(IncrementalHash hasher, ReadOnlySpan<char> s, ReadOnlySpan<char> p, ReadOnlySpan<char> o)
    {
        // Stack allocate for small strings, otherwise use heap
        Span<byte> buffer = stackalloc byte[256];

        var sBytes = s.Length <= 80 ? buffer.Slice(0, Encoding.UTF8.GetByteCount(s)) : new byte[Encoding.UTF8.GetByteCount(s)];
        Encoding.UTF8.GetBytes(s, sBytes);
        hasher.AppendData(sBytes);

        var pBytes = p.Length <= 80 ? buffer.Slice(0, Encoding.UTF8.GetByteCount(p)) : new byte[Encoding.UTF8.GetByteCount(p)];
        Encoding.UTF8.GetBytes(p, pBytes);
        hasher.AppendData(pBytes);

        var oBytes = o.Length <= 80 ? buffer.Slice(0, Encoding.UTF8.GetByteCount(o)) : new byte[Encoding.UTF8.GetByteCount(o)];
        Encoding.UTF8.GetBytes(o, oBytes);
        hasher.AppendData(oBytes);
    }

}

/// <summary>
/// Result of a resource operation.
/// </summary>
public sealed class ResourceResult
{
    public int StatusCode { get; init; }
    public string? StatusDescription { get; init; }
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? Location { get; init; }
    public string? LinkHeader { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    public static ResourceResult Success(byte[] body, string contentType, string? etag = null, DateTimeOffset? lastModified = null, string? linkHeader = null)
    {
        return new ResourceResult
        {
            StatusCode = 200,
            StatusDescription = "OK",
            Body = body,
            ContentType = contentType,
            ETag = etag,
            LastModified = lastModified,
            LinkHeader = linkHeader
        };
    }

    public static ResourceResult Created(string location, string? etag = null, string? linkHeader = null)
    {
        return new ResourceResult
        {
            StatusCode = 201,
            StatusDescription = "Created",
            Location = location,
            ETag = etag,
            LinkHeader = linkHeader
        };
    }

    public static ResourceResult NoContent(string? etag = null, string? linkHeader = null)
    {
        return new ResourceResult
        {
            StatusCode = 204,
            StatusDescription = "No Content",
            ETag = etag,
            LinkHeader = linkHeader
        };
    }

    public static ResourceResult BadRequest(string message)
    {
        return new ResourceResult
        {
            StatusCode = 400,
            StatusDescription = "Bad Request",
            ErrorMessage = message
        };
    }

    public static ResourceResult Unauthorized(string message)
    {
        return new ResourceResult
        {
            StatusCode = 401,
            StatusDescription = "Unauthorized",
            ErrorMessage = message
        };
    }

    public static ResourceResult Forbidden(string message)
    {
        return new ResourceResult
        {
            StatusCode = 403,
            StatusDescription = "Forbidden",
            ErrorMessage = message
        };
    }

    public static ResourceResult NotFound(string message)
    {
        return new ResourceResult
        {
            StatusCode = 404,
            StatusDescription = "Not Found",
            ErrorMessage = message
        };
    }

    public static ResourceResult PreconditionFailed(string message)
    {
        return new ResourceResult
        {
            StatusCode = 412,
            StatusDescription = "Precondition Failed",
            ErrorMessage = message
        };
    }

    public static ResourceResult UnsupportedMediaType(string message)
    {
        return new ResourceResult
        {
            StatusCode = 415,
            StatusDescription = "Unsupported Media Type",
            ErrorMessage = message
        };
    }
}
