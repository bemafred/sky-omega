// SolidResource.cs
// Solid resource representation.
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.Models;

/// <summary>
/// Represents a Solid resource (RDF document or binary).
/// </summary>
public sealed class SolidResource
{
    /// <summary>
    /// The resource URI.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// The content type (MIME type).
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Whether this resource is an RDF source (can be content-negotiated).
    /// </summary>
    public bool IsRdfSource { get; init; }

    /// <summary>
    /// Whether this resource is a container (LDP container).
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>
    /// The ETag for conditional requests.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Last modification time.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The named graph URI in the QuadStore (typically same as resource URI).
    /// </summary>
    public string? GraphUri { get; init; }

    /// <summary>
    /// The parent container URI.
    /// </summary>
    public string? ContainerUri { get; init; }

    /// <summary>
    /// Creates a new RDF resource.
    /// </summary>
    public static SolidResource CreateRdfSource(string uri, string? eTag = null, DateTimeOffset? lastModified = null)
    {
        return new SolidResource
        {
            Uri = uri,
            ContentType = "text/turtle",
            IsRdfSource = true,
            IsContainer = false,
            ETag = eTag,
            LastModified = lastModified,
            GraphUri = uri,
            ContainerUri = GetContainerUri(uri)
        };
    }

    /// <summary>
    /// Creates a new container resource.
    /// </summary>
    public static SolidResource CreateContainer(string uri, string? eTag = null, DateTimeOffset? lastModified = null)
    {
        // Ensure container URI ends with /
        if (!uri.EndsWith("/"))
            uri += "/";

        return new SolidResource
        {
            Uri = uri,
            ContentType = "text/turtle",
            IsRdfSource = true,
            IsContainer = true,
            ETag = eTag,
            LastModified = lastModified,
            GraphUri = uri,
            ContainerUri = GetContainerUri(uri.TrimEnd('/'))
        };
    }

    /// <summary>
    /// Creates a new binary (non-RDF) resource.
    /// </summary>
    public static SolidResource CreateBinary(string uri, string contentType, string? eTag = null, DateTimeOffset? lastModified = null)
    {
        return new SolidResource
        {
            Uri = uri,
            ContentType = contentType,
            IsRdfSource = false,
            IsContainer = false,
            ETag = eTag,
            LastModified = lastModified,
            GraphUri = null, // Binaries don't have a graph
            ContainerUri = GetContainerUri(uri)
        };
    }

    /// <summary>
    /// Gets the parent container URI for a resource.
    /// </summary>
    private static string? GetContainerUri(string resourceUri)
    {
        if (string.IsNullOrEmpty(resourceUri))
            return null;

        var lastSlash = resourceUri.LastIndexOf('/');
        if (lastSlash <= 0)
            return null;

        // Find the scheme separator
        var schemeEnd = resourceUri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0 && lastSlash <= schemeEnd + 3)
            return null; // Resource is at root

        return resourceUri.Substring(0, lastSlash + 1);
    }
}

/// <summary>
/// LDP container types.
/// </summary>
public enum ContainerType
{
    /// <summary>
    /// Basic container - simple membership.
    /// </summary>
    BasicContainer,

    /// <summary>
    /// Direct container - membership via membership triples.
    /// </summary>
    DirectContainer,

    /// <summary>
    /// Indirect container - membership via derived membership.
    /// </summary>
    IndirectContainer
}
