// ContainerHandler.cs
// HTTP handler for Solid LDP container operations.
// Based on W3C Linked Data Platform 1.0.
// https://www.w3.org/TR/ldp/
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Globalization;
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
/// Handles container-specific operations (POST to create child, GET container listing).
/// </summary>
public sealed class ContainerHandler
{
    private readonly QuadStore _store;
    private readonly IAccessPolicy _accessPolicy;
    private readonly ResourceHandler _resourceHandler;

    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string DctermsModified = "http://purl.org/dc/terms/modified";

    public ContainerHandler(QuadStore store, IAccessPolicy accessPolicy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
        _resourceHandler = new ResourceHandler(store, accessPolicy);
    }

    /// <summary>
    /// Handles POST requests to create a new resource in a container.
    /// </summary>
    public async Task<ResourceResult> HandlePostAsync(
        string containerUri,
        string? agentWebId,
        string contentType,
        Stream body,
        string? slug,
        string? linkHeader,
        CancellationToken ct = default)
    {
        // Ensure container URI ends with /
        if (!containerUri.EndsWith("/"))
            containerUri += "/";

        // Check Append access (POST requires Append or Write)
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, containerUri, AccessMode.Append, ct);
        if (!decision.IsAllowed)
        {
            // Try Write access (which implies Append)
            decision = await _accessPolicy.EvaluateAsync(agentWebId, containerUri, AccessMode.Write, ct);
            if (!decision.IsAllowed)
            {
                return agentWebId == null
                    ? ResourceResult.Unauthorized("Authentication required")
                    : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
            }
        }

        // Check if container exists
        bool containerExists;
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, containerUri);
            containerExists = results.MoveNext();
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (!containerExists)
        {
            return ResourceResult.NotFound($"Container not found: {containerUri}");
        }

        // Determine if creating a container (from Link header)
        bool isContainerRequest = false;
        if (!string.IsNullOrEmpty(linkHeader))
        {
            isContainerRequest = linkHeader.Contains(SolidContainer.Ldp.Container, StringComparison.OrdinalIgnoreCase) ||
                                linkHeader.Contains(SolidContainer.Ldp.BasicContainer, StringComparison.OrdinalIgnoreCase);
        }

        // Generate resource URI
        var resourceSlug = SolidContainer.GenerateSlug(slug);
        var resourceUri = containerUri + resourceSlug;
        if (isContainerRequest && !resourceUri.EndsWith("/"))
            resourceUri += "/";

        // Check if resource already exists
        _store.AcquireReadLock();
        try
        {
            var existingResults = _store.QueryCurrent(null, null, null, resourceUri);
            if (existingResults.MoveNext())
            {
                existingResults.Dispose();
                // Generate a unique slug
                resourceSlug = SolidContainer.GenerateSlug();
                resourceUri = containerUri + resourceSlug;
                if (isContainerRequest && !resourceUri.EndsWith("/"))
                    resourceUri += "/";
            }
            else
            {
                existingResults.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        // Parse incoming RDF
        var format = RdfFormatNegotiator.FromContentType(contentType.AsSpan());
        if (format == RdfFormat.Unknown)
        {
            return ResourceResult.UnsupportedMediaType($"Unsupported content type: {contentType}");
        }

        List<(string Subject, string Predicate, string Object)> triples;
        try
        {
            triples = await RdfEngine.ParseTriplesAsync(body, format, resourceUri, ct);
        }
        catch (Exception ex)
        {
            return ResourceResult.BadRequest($"Invalid RDF: {ex.Message}");
        }

        // Create the resource
        _store.BeginBatch();
        try
        {
            // Add the new resource triples
            foreach (var (subject, predicate, obj) in triples)
            {
                _store.AddCurrentBatched(subject, predicate, obj, resourceUri);
            }

            // Add type triples for container
            if (isContainerRequest)
            {
                _store.AddCurrentBatched($"<{resourceUri}>", RdfType, SolidContainer.Ldp.Container, resourceUri);
                _store.AddCurrentBatched($"<{resourceUri}>", RdfType, SolidContainer.Ldp.BasicContainer, resourceUri);
                _store.AddCurrentBatched($"<{resourceUri}>", RdfType, SolidContainer.Ldp.Resource, resourceUri);
            }

            // Add modified timestamp
            var now = DateTimeOffset.UtcNow;
            var modified = $"\"{now:yyyy-MM-ddTHH:mm:ss.fffZ}\"^^<http://www.w3.org/2001/XMLSchema#dateTime>";
            _store.AddCurrentBatched($"<{resourceUri}>", DctermsModified, modified, resourceUri);

            // Add containment triple to parent container
            _store.AddCurrentBatched($"<{containerUri}>", SolidContainer.Ldp.Contains, $"<{resourceUri}>", containerUri);

            // Update container's modified timestamp
            _store.AddCurrentBatched($"<{containerUri}>", DctermsModified, modified, containerUri);

            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        // Compute ETag
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var (s, p, o) in triples)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(s));
            hasher.AppendData(Encoding.UTF8.GetBytes(p));
            hasher.AppendData(Encoding.UTF8.GetBytes(o));
        }
        var hashBytes = hasher.GetHashAndReset();
        var etag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";

        var aclUri = _accessPolicy.GetAccessControlDocumentUri(resourceUri);
        var responseLinkHeader = LinkHeaderBuilder.ForResource(
            new SolidResource
            {
                Uri = resourceUri,
                ContentType = contentType,
                IsRdfSource = true,
                IsContainer = isContainerRequest
            },
            aclUri);

        return ResourceResult.Created(resourceUri, etag, responseLinkHeader);
    }

    /// <summary>
    /// Handles GET requests for a container, including containment triples.
    /// </summary>
    public async Task<ResourceResult> HandleGetAsync(
        string containerUri,
        string? agentWebId,
        string? acceptHeader,
        CancellationToken ct = default)
    {
        // Ensure container URI ends with /
        if (!containerUri.EndsWith("/"))
            containerUri += "/";

        // Check access
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, containerUri, AccessMode.Read, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Determine output format
        var format = RdfFormat.Turtle;
        if (!string.IsNullOrEmpty(acceptHeader))
        {
            format = RdfFormatNegotiator.FromAcceptHeader(acceptHeader.AsSpan(), RdfFormat.Turtle);
        }

        var contentType = RdfFormatNegotiator.GetContentType(format);
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        _store.AcquireReadLock();
        try
        {
            // Query for container triples
            var results = _store.QueryCurrent(null, null, null, containerUri);

            using var memStream = new MemoryStream();
            using var writer = new StreamWriter(memStream, Encoding.UTF8, leaveOpen: true);

            bool hasTriples = false;

            switch (format)
            {
                case RdfFormat.Turtle:
                    {
                        using var turtleWriter = new TurtleStreamWriter(writer);
                        turtleWriter.WritePrefixes();

                        // Add LDP type triples
                        turtleWriter.WriteTriple($"<{containerUri}>".AsSpan(), RdfType.AsSpan(), SolidContainer.Ldp.Container.AsSpan());
                        turtleWriter.WriteTriple($"<{containerUri}>".AsSpan(), RdfType.AsSpan(), SolidContainer.Ldp.BasicContainer.AsSpan());

                        while (results.MoveNext())
                        {
                            hasTriples = true;
                            var current = results.Current;
                            turtleWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                        }
                    }
                    break;

                case RdfFormat.NTriples:
                    {
                        using var ntWriter = new NTriplesStreamWriter(writer);

                        // Add LDP type triples
                        ntWriter.WriteTriple($"<{containerUri}>".AsSpan(), $"<{RdfType}>".AsSpan(), $"<{SolidContainer.Ldp.Container}>".AsSpan());
                        ntWriter.WriteTriple($"<{containerUri}>".AsSpan(), $"<{RdfType}>".AsSpan(), $"<{SolidContainer.Ldp.BasicContainer}>".AsSpan());

                        while (results.MoveNext())
                        {
                            hasTriples = true;
                            var current = results.Current;
                            ntWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                        }
                    }
                    break;

                default:
                    {
                        using var defaultWriter = new NTriplesStreamWriter(writer);

                        while (results.MoveNext())
                        {
                            hasTriples = true;
                            var current = results.Current;
                            defaultWriter.WriteTriple(current.Subject, current.Predicate, current.Object);
                            UpdateHash(hasher, current.Subject, current.Predicate, current.Object);
                        }
                    }
                    break;
            }

            results.Dispose();

            if (!hasTriples)
            {
                // Container doesn't exist
                return ResourceResult.NotFound($"Container not found: {containerUri}");
            }

            writer.Flush();

            var hashBytes = hasher.GetHashAndReset();
            var etag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";

            var aclUri = _accessPolicy.GetAccessControlDocumentUri(containerUri);
            var linkHeader = LinkHeaderBuilder.ForContainer(
                new SolidContainer { Uri = containerUri },
                aclUri);

            return ResourceResult.Success(
                memStream.ToArray(),
                contentType,
                etag,
                null,
                linkHeader);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    /// <summary>
    /// Creates a new container.
    /// </summary>
    public async Task<ResourceResult> CreateContainerAsync(
        string containerUri,
        string? agentWebId,
        CancellationToken ct = default)
    {
        // Ensure container URI ends with /
        if (!containerUri.EndsWith("/"))
            containerUri += "/";

        // Get parent container
        var parentUri = GetParentContainerUri(containerUri);
        if (parentUri == null)
        {
            return ResourceResult.BadRequest("Cannot create root container");
        }

        // Check Write access on parent
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, parentUri, AccessMode.Write, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Check if container already exists
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, containerUri);
            if (results.MoveNext())
            {
                results.Dispose();
                return ResourceResult.PreconditionFailed("Container already exists");
            }
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        // Create the container
        _store.BeginBatch();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var modified = $"\"{now:yyyy-MM-ddTHH:mm:ss.fffZ}\"^^<http://www.w3.org/2001/XMLSchema#dateTime>";

            // Add container type triples
            _store.AddCurrentBatched($"<{containerUri}>", RdfType, SolidContainer.Ldp.Container, containerUri);
            _store.AddCurrentBatched($"<{containerUri}>", RdfType, SolidContainer.Ldp.BasicContainer, containerUri);
            _store.AddCurrentBatched($"<{containerUri}>", RdfType, SolidContainer.Ldp.Resource, containerUri);
            _store.AddCurrentBatched($"<{containerUri}>", DctermsModified, modified, containerUri);

            // Add containment triple to parent
            _store.AddCurrentBatched($"<{parentUri}>", SolidContainer.Ldp.Contains, $"<{containerUri}>", parentUri);
            _store.AddCurrentBatched($"<{parentUri}>", DctermsModified, modified, parentUri);

            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        var aclUri = _accessPolicy.GetAccessControlDocumentUri(containerUri);
        var linkHeader = LinkHeaderBuilder.ForContainer(
            new SolidContainer { Uri = containerUri },
            aclUri);

        return ResourceResult.Created(containerUri, null, linkHeader);
    }

    private static string? GetParentContainerUri(string containerUri)
    {
        // Remove trailing slash for calculation
        var uri = containerUri.TrimEnd('/');
        var lastSlash = uri.LastIndexOf('/');
        if (lastSlash <= 0)
            return null;

        // Check for scheme
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0 && lastSlash <= schemeEnd + 3)
            return null;

        return uri.Substring(0, lastSlash + 1);
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
