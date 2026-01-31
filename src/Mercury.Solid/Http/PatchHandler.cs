// PatchHandler.cs
// HTTP handler for Solid PATCH operations.
// Based on W3C Solid Protocol and N3 Patch specification.
// https://solidproject.org/TR/n3-patch
// No external dependencies, only BCL.
// .NET 10 / C# 14

using SkyOmega.Mercury.Solid.AccessControl;
using SkyOmega.Mercury.Solid.N3;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Solid.Http;

/// <summary>
/// Handles PATCH requests for Solid resources using N3 Patch format.
/// </summary>
public sealed class PatchHandler
{
    private readonly QuadStore _store;
    private readonly IAccessPolicy _accessPolicy;
    private readonly N3PatchExecutor _executor;

    // Supported PATCH content types
    private const string N3ContentType = "text/n3";
    private const string SolidInsertDeletePatchType = "application/n3-patch";

    public PatchHandler(QuadStore store, IAccessPolicy accessPolicy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
        _executor = new N3PatchExecutor(store);
    }

    /// <summary>
    /// Handles PATCH requests for a resource.
    /// </summary>
    public async Task<ResourceResult> HandlePatchAsync(
        string resourceUri,
        string? agentWebId,
        string contentType,
        Stream body,
        string? ifMatch,
        CancellationToken ct = default)
    {
        // Check access - PATCH requires Write permission
        var decision = await _accessPolicy.EvaluateAsync(agentWebId, resourceUri, AccessMode.Write, ct);
        if (!decision.IsAllowed)
        {
            return agentWebId == null
                ? ResourceResult.Unauthorized("Authentication required")
                : ResourceResult.Forbidden(decision.Reason ?? "Access denied");
        }

        // Validate content type
        if (!IsN3PatchContentType(contentType))
        {
            return ResourceResult.UnsupportedMediaType(
                $"PATCH requires content type '{N3ContentType}' or '{SolidInsertDeletePatchType}', got '{contentType}'");
        }

        // Check if resource exists (for If-Match validation)
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
                var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.MD5);
                do
                {
                    var current = results.Current;
                    AppendToHash(hasher, current.Subject);
                    AppendToHash(hasher, current.Predicate);
                    AppendToHash(hasher, current.Object);
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

        // Parse the N3 Patch
        N3Patch patch;
        try
        {
            using var parser = new N3PatchParser(body, resourceUri);
            patch = await parser.ParseAsync(ct);
        }
        catch (Exception ex)
        {
            return ResourceResult.BadRequest($"Invalid N3 Patch: {ex.Message}");
        }

        if (patch.IsEmpty)
        {
            // Empty patch is valid, just return success
            return ResourceResult.NoContent(currentETag);
        }

        // Execute the patch
        var result = _executor.Execute(patch, resourceUri);

        if (!result.IsSuccess)
        {
            return ResourceResult.BadRequest($"Patch execution failed: {result.ErrorMessage}");
        }

        // Compute new ETag
        string? newETag = null;
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(null, null, null, resourceUri);
            if (results.MoveNext())
            {
                var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.MD5);
                do
                {
                    var current = results.Current;
                    AppendToHash(hasher, current.Subject);
                    AppendToHash(hasher, current.Predicate);
                    AppendToHash(hasher, current.Object);
                }
                while (results.MoveNext());

                var hashBytes = hasher.GetHashAndReset();
                newETag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";
            }
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        var aclUri = _accessPolicy.GetAccessControlDocumentUri(resourceUri);
        var linkHeader = LinkHeaderBuilder.ForResource(
            new Models.SolidResource
            {
                Uri = resourceUri,
                ContentType = "text/turtle",
                IsRdfSource = true,
                IsContainer = resourceUri.EndsWith("/")
            },
            aclUri);

        return ResourceResult.NoContent(newETag, linkHeader);
    }

    private static bool IsN3PatchContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        // Strip parameters
        var semicolonIndex = contentType.IndexOf(';');
        if (semicolonIndex >= 0)
            contentType = contentType.Substring(0, semicolonIndex);

        contentType = contentType.Trim();

        return contentType.Equals(N3ContentType, StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals(SolidInsertDeletePatchType, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendToHash(System.Security.Cryptography.IncrementalHash hasher, ReadOnlySpan<char> value)
    {
        Span<byte> buffer = stackalloc byte[256];
        if (value.Length <= 80)
        {
            var bytes = buffer.Slice(0, System.Text.Encoding.UTF8.GetByteCount(value));
            System.Text.Encoding.UTF8.GetBytes(value, bytes);
            hasher.AppendData(bytes);
        }
        else
        {
            hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(value.ToString()));
        }
    }
}
