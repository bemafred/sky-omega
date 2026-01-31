// IAccessPolicy.cs
// Authorization abstraction for Solid access control.
// Supports both WAC (Web Access Control) and ACP (Access Control Policy).
// https://solidproject.org/TR/wac
// https://solidproject.org/TR/acp
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.AccessControl;

/// <summary>
/// Abstraction for Solid authorization policies.
/// Implementations include WebAccessControl (WAC) and AccessControlPolicy (ACP).
/// </summary>
public interface IAccessPolicy
{
    /// <summary>
    /// Evaluates access for an agent to a resource with a specific mode.
    /// </summary>
    /// <param name="agentWebId">The WebID of the requesting agent, or null for unauthenticated access.</param>
    /// <param name="resourceUri">The URI of the resource being accessed.</param>
    /// <param name="requestedMode">The access mode being requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The access decision.</returns>
    ValueTask<AccessDecision> EvaluateAsync(
        string? agentWebId,
        string resourceUri,
        AccessMode requestedMode,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all access modes granted to an agent for a resource.
    /// </summary>
    /// <param name="agentWebId">The WebID of the agent, or null for public access.</param>
    /// <param name="resourceUri">The URI of the resource.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The granted access modes.</returns>
    ValueTask<AccessMode> GetGrantedModesAsync(
        string? agentWebId,
        string resourceUri,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the URI of the access control document for a resource.
    /// </summary>
    /// <param name="resourceUri">The resource URI.</param>
    /// <returns>The ACL/ACP document URI, or null if none exists.</returns>
    string? GetAccessControlDocumentUri(string resourceUri);
}

/// <summary>
/// Factory for creating access policy implementations.
/// </summary>
public static class AccessPolicyFactory
{
    /// <summary>
    /// Creates a WAC (Web Access Control) policy instance.
    /// </summary>
    public static IAccessPolicy CreateWac(IAccessControlDocumentProvider documentProvider)
    {
        return new WebAccessControl(documentProvider);
    }

    /// <summary>
    /// Creates an ACP (Access Control Policy) instance.
    /// </summary>
    public static IAccessPolicy CreateAcp(IAccessControlDocumentProvider documentProvider)
    {
        return new AccessControlPolicy(documentProvider);
    }

    /// <summary>
    /// Creates a hybrid policy that tries WAC first, then falls back to ACP.
    /// </summary>
    public static IAccessPolicy CreateHybrid(IAccessControlDocumentProvider documentProvider)
    {
        return new HybridAccessPolicy(
            new WebAccessControl(documentProvider),
            new AccessControlPolicy(documentProvider));
    }

    /// <summary>
    /// Creates an allow-all policy for development/testing.
    /// </summary>
    public static IAccessPolicy CreateAllowAll()
    {
        return AllowAllPolicy.Instance;
    }
}

/// <summary>
/// Interface for fetching and caching access control documents.
/// </summary>
public interface IAccessControlDocumentProvider
{
    /// <summary>
    /// Fetches the access control document for a resource.
    /// </summary>
    /// <param name="resourceUri">The resource URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The document triples, or null if no ACL exists.</returns>
    ValueTask<IReadOnlyList<(string Subject, string Predicate, string Object)>?> GetAccessControlDocumentAsync(
        string resourceUri,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the URI of the access control document for a resource.
    /// Returns the .acl suffix for WAC or access control resource for ACP.
    /// </summary>
    string? GetAccessControlDocumentUri(string resourceUri);

    /// <summary>
    /// Fetches the WebID profile document to resolve agent groups.
    /// </summary>
    ValueTask<IReadOnlyList<(string Subject, string Predicate, string Object)>?> GetWebIdProfileAsync(
        string webId,
        CancellationToken ct = default);
}

/// <summary>
/// A policy that allows all access - for development/testing only.
/// </summary>
internal sealed class AllowAllPolicy : IAccessPolicy
{
    public static readonly AllowAllPolicy Instance = new();

    private AllowAllPolicy() { }

    public ValueTask<AccessDecision> EvaluateAsync(string? agentWebId, string resourceUri, AccessMode requestedMode, CancellationToken ct = default)
    {
        return ValueTask.FromResult(AccessDecision.AllowAll);
    }

    public ValueTask<AccessMode> GetGrantedModesAsync(string? agentWebId, string resourceUri, CancellationToken ct = default)
    {
        return ValueTask.FromResult(AccessMode.All);
    }

    public string? GetAccessControlDocumentUri(string resourceUri) => null;
}

/// <summary>
/// A hybrid policy that tries WAC first, then ACP.
/// </summary>
internal sealed class HybridAccessPolicy : IAccessPolicy
{
    private readonly IAccessPolicy _wac;
    private readonly IAccessPolicy _acp;

    public HybridAccessPolicy(IAccessPolicy wac, IAccessPolicy acp)
    {
        _wac = wac;
        _acp = acp;
    }

    public async ValueTask<AccessDecision> EvaluateAsync(string? agentWebId, string resourceUri, AccessMode requestedMode, CancellationToken ct = default)
    {
        // Try WAC first
        var wacDecision = await _wac.EvaluateAsync(agentWebId, resourceUri, requestedMode, ct);
        if (wacDecision.IsAllowed || wacDecision.PolicyDocumentUri != null)
            return wacDecision;

        // Fall back to ACP
        return await _acp.EvaluateAsync(agentWebId, resourceUri, requestedMode, ct);
    }

    public async ValueTask<AccessMode> GetGrantedModesAsync(string? agentWebId, string resourceUri, CancellationToken ct = default)
    {
        var wacModes = await _wac.GetGrantedModesAsync(agentWebId, resourceUri, ct);
        var acpModes = await _acp.GetGrantedModesAsync(agentWebId, resourceUri, ct);
        return wacModes | acpModes;
    }

    public string? GetAccessControlDocumentUri(string resourceUri)
    {
        return _wac.GetAccessControlDocumentUri(resourceUri) ?? _acp.GetAccessControlDocumentUri(resourceUri);
    }
}
