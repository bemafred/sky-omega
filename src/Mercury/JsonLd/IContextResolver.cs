// IContextResolver.cs
// Interface for resolving external JSON-LD contexts
// Supports loading contexts from URIs (file://, http://, etc.)

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.JsonLd;

/// <summary>
/// Interface for resolving external JSON-LD contexts.
/// Implementations can load contexts from files, HTTP, or other sources.
/// </summary>
public interface IContextResolver
{
    /// <summary>
    /// Resolve a context URI to its JSON content.
    /// </summary>
    /// <param name="contextUri">The URI of the context to resolve (relative or absolute)</param>
    /// <param name="baseUri">The base URI for resolving relative URIs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The JSON content of the context, or null if not found</returns>
    /// <exception cref="JsonLdContextException">Thrown when context loading fails</exception>
    Task<string?> ResolveAsync(string contextUri, string? baseUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception thrown when context resolution fails.
/// </summary>
public class JsonLdContextException : Exception
{
    public string ContextUri { get; }
    public string ErrorCode { get; }

    public JsonLdContextException(string contextUri, string errorCode, string message)
        : base(message)
    {
        ContextUri = contextUri;
        ErrorCode = errorCode;
    }

    public JsonLdContextException(string contextUri, string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ContextUri = contextUri;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Context resolver that loads contexts from the local file system.
/// Used primarily for testing with local context files.
/// </summary>
public class FileContextResolver : IContextResolver
{
    private readonly string? _basePath;

    /// <summary>
    /// Create a file context resolver.
    /// </summary>
    /// <param name="basePath">Optional base path for resolving relative file paths</param>
    public FileContextResolver(string? basePath = null)
    {
        _basePath = basePath;
    }

    public async Task<string?> ResolveAsync(string contextUri, string? baseUri, CancellationToken cancellationToken = default)
    {
        // Handle relative URIs
        string resolvedPath;

        if (Uri.TryCreate(contextUri, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme == "file")
            {
                resolvedPath = absoluteUri.LocalPath;
            }
            else if (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https")
            {
                // For HTTP URIs in test mode, try to map to local file
                // Extract the filename and look in base path
                var filename = Path.GetFileName(absoluteUri.LocalPath);
                if (_basePath != null)
                {
                    resolvedPath = Path.Combine(_basePath, filename);
                }
                else
                {
                    // Can't resolve HTTP without base path
                    throw new JsonLdContextException(contextUri, "loading remote context failed",
                        $"Cannot load remote context without HTTP support: {contextUri}");
                }
            }
            else
            {
                throw new JsonLdContextException(contextUri, "loading remote context failed",
                    $"Unsupported URI scheme: {absoluteUri.Scheme}");
            }
        }
        else
        {
            // Relative URI - resolve against base
            if (!string.IsNullOrEmpty(baseUri) && Uri.TryCreate(baseUri, UriKind.Absolute, out var baseUriObj))
            {
                if (Uri.TryCreate(baseUriObj, contextUri, out var resolved))
                {
                    if (resolved.Scheme == "file")
                    {
                        resolvedPath = resolved.LocalPath;
                    }
                    else if (_basePath != null)
                    {
                        // Map HTTP-like URI to local file
                        var filename = Path.GetFileName(resolved.LocalPath);
                        resolvedPath = Path.Combine(_basePath, filename);
                    }
                    else
                    {
                        throw new JsonLdContextException(contextUri, "loading remote context failed",
                            $"Cannot resolve relative context without base path: {contextUri}");
                    }
                }
                else
                {
                    throw new JsonLdContextException(contextUri, "loading remote context failed",
                        $"Cannot resolve relative URI: {contextUri}");
                }
            }
            else if (_basePath != null)
            {
                // No base URI, use base path directly
                resolvedPath = Path.Combine(_basePath, contextUri);
            }
            else
            {
                throw new JsonLdContextException(contextUri, "loading remote context failed",
                    $"Cannot resolve context without base: {contextUri}");
            }
        }

        // Load the file
        if (!File.Exists(resolvedPath))
        {
            throw new JsonLdContextException(contextUri, "loading remote context failed",
                $"Context file not found: {resolvedPath}");
        }

        try
        {
            return await File.ReadAllTextAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not JsonLdContextException)
        {
            throw new JsonLdContextException(contextUri, "loading remote context failed",
                $"Failed to read context file: {resolvedPath}", ex);
        }
    }
}

/// <summary>
/// Null context resolver that doesn't support external contexts.
/// Used when no resolver is provided.
/// </summary>
public class NullContextResolver : IContextResolver
{
    public static readonly NullContextResolver Instance = new();

    private NullContextResolver() { }

    public Task<string?> ResolveAsync(string contextUri, string? baseUri, CancellationToken cancellationToken = default)
    {
        // External contexts not supported - return null to indicate failure
        return Task.FromResult<string?>(null);
    }
}
