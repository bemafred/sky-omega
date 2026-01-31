// AccessDecision.cs
// Authorization decision result for Solid access control.
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.AccessControl;

/// <summary>
/// Result of an access control evaluation.
/// </summary>
public readonly struct AccessDecision
{
    /// <summary>
    /// Whether access is allowed.
    /// </summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// The access modes that are granted (may be subset of requested).
    /// </summary>
    public AccessMode GrantedModes { get; }

    /// <summary>
    /// Human-readable reason for the decision.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// The URI of the ACL/ACP document that made the decision.
    /// </summary>
    public string? PolicyDocumentUri { get; }

    /// <summary>
    /// The specific authorization/policy ID within the document.
    /// </summary>
    public string? PolicyId { get; }

    private AccessDecision(bool isAllowed, AccessMode grantedModes, string? reason, string? policyDocumentUri, string? policyId)
    {
        IsAllowed = isAllowed;
        GrantedModes = grantedModes;
        Reason = reason;
        PolicyDocumentUri = policyDocumentUri;
        PolicyId = policyId;
    }

    /// <summary>
    /// Creates an allow decision with the specified granted modes.
    /// </summary>
    public static AccessDecision Allow(AccessMode grantedModes, string? reason = null, string? policyDocumentUri = null, string? policyId = null)
    {
        return new AccessDecision(true, grantedModes, reason, policyDocumentUri, policyId);
    }

    /// <summary>
    /// Creates a deny decision with no granted modes.
    /// </summary>
    public static AccessDecision Deny(string? reason = null, string? policyDocumentUri = null)
    {
        return new AccessDecision(false, AccessMode.None, reason, policyDocumentUri, null);
    }

    /// <summary>
    /// Pre-computed allow decision for all access.
    /// </summary>
    public static readonly AccessDecision AllowAll = new(true, AccessMode.All, "Full access granted", null, null);

    /// <summary>
    /// Pre-computed deny decision for unauthenticated access.
    /// </summary>
    public static readonly AccessDecision DenyUnauthenticated = new(false, AccessMode.None, "Authentication required", null, null);

    /// <summary>
    /// Pre-computed deny decision for unauthorized access.
    /// </summary>
    public static readonly AccessDecision DenyUnauthorized = new(false, AccessMode.None, "Access denied by policy", null, null);

    /// <summary>
    /// Pre-computed deny decision when no ACL is found.
    /// </summary>
    public static readonly AccessDecision DenyNoAcl = new(false, AccessMode.None, "No access control document found", null, null);

    /// <summary>
    /// Checks if a specific mode is granted.
    /// </summary>
    public bool HasMode(AccessMode mode) => (GrantedModes & mode) == mode;

    public override string ToString()
    {
        return IsAllowed
            ? $"Allow({GrantedModes}): {Reason ?? "No reason"}"
            : $"Deny: {Reason ?? "No reason"}";
    }
}
