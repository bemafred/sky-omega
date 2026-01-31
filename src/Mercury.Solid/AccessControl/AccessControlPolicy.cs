// AccessControlPolicy.cs
// W3C Access Control Policy (ACP) implementation for Solid.
// https://solidproject.org/TR/acp
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.AccessControl;

/// <summary>
/// Access Control Policy (ACP) implementation.
/// Evaluates access based on ACP policy documents.
/// </summary>
public sealed class AccessControlPolicy : IAccessPolicy
{
    private readonly IAccessControlDocumentProvider _documentProvider;

    // ACP namespace
    private const string AcpNs = "http://www.w3.org/ns/solid/acp#";

    // ACP predicates
    private const string AcpPolicy = AcpNs + "Policy";
    private const string AcpAccessControl = AcpNs + "AccessControl";
    private const string AcpResource = AcpNs + "resource";
    private const string AcpApply = AcpNs + "apply";
    private const string AcpAllow = AcpNs + "allow";
    private const string AcpDeny = AcpNs + "deny";
    private const string AcpAllOf = AcpNs + "allOf";
    private const string AcpAnyOf = AcpNs + "anyOf";
    private const string AcpNoneOf = AcpNs + "noneOf";
    private const string AcpAgent = AcpNs + "agent";
    private const string AcpPublic = AcpNs + "PublicAgent";
    private const string AcpAuthenticated = AcpNs + "AuthenticatedAgent";
    private const string AcpCreator = AcpNs + "CreatorAgent";

    public AccessControlPolicy(IAccessControlDocumentProvider documentProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
    }

    public async ValueTask<AccessDecision> EvaluateAsync(
        string? agentWebId,
        string resourceUri,
        AccessMode requestedMode,
        CancellationToken ct = default)
    {
        var (allowed, denied) = await EvaluatePoliciesAsync(agentWebId, resourceUri, ct);

        // ACP uses explicit deny - denied modes take precedence
        var effectiveModes = allowed & ~denied;

        if ((effectiveModes & requestedMode) == requestedMode)
        {
            var acpUri = GetAccessControlDocumentUri(resourceUri);
            return AccessDecision.Allow(effectiveModes, "Access granted by ACP policy", acpUri);
        }

        if (agentWebId == null)
        {
            return AccessDecision.DenyUnauthenticated;
        }

        return AccessDecision.DenyUnauthorized;
    }

    public async ValueTask<AccessMode> GetGrantedModesAsync(
        string? agentWebId,
        string resourceUri,
        CancellationToken ct = default)
    {
        var (allowed, denied) = await EvaluatePoliciesAsync(agentWebId, resourceUri, ct);
        return allowed & ~denied;
    }

    private async Task<(AccessMode Allowed, AccessMode Denied)> EvaluatePoliciesAsync(
        string? agentWebId,
        string resourceUri,
        CancellationToken ct)
    {
        // Load ACP document
        var acpTriples = await _documentProvider.GetAccessControlDocumentAsync(resourceUri, ct);
        if (acpTriples == null || acpTriples.Count == 0)
        {
            return (AccessMode.None, AccessMode.None);
        }

        var allowed = AccessMode.None;
        var denied = AccessMode.None;

        // Find AccessControl resources for this resource
        var accessControls = new List<string>();
        foreach (var triple in acpTriples)
        {
            if (triple.Predicate == AcpResource && UriMatches(triple.Object, resourceUri))
            {
                accessControls.Add(triple.Subject);
            }
        }

        // For each AccessControl, find and evaluate policies
        foreach (var accessControl in accessControls)
        {
            foreach (var triple in acpTriples)
            {
                if (triple.Subject == accessControl && triple.Predicate == AcpApply)
                {
                    var policyNode = triple.Object;
                    var (policyAllowed, policyDenied) = await EvaluatePolicyAsync(
                        policyNode, agentWebId, acpTriples, ct);

                    allowed |= policyAllowed;
                    denied |= policyDenied;
                }
            }
        }

        return (allowed, denied);
    }

    private async Task<(AccessMode Allowed, AccessMode Denied)> EvaluatePolicyAsync(
        string policyNode,
        string? agentWebId,
        IReadOnlyList<(string Subject, string Predicate, string Object)> acpTriples,
        CancellationToken ct)
    {
        // Check if the agent matches the policy's matchers
        bool matches = await EvaluateMatchersAsync(policyNode, agentWebId, acpTriples, ct);
        if (!matches)
        {
            return (AccessMode.None, AccessMode.None);
        }

        // Get allowed and denied modes
        var allowed = AccessMode.None;
        var denied = AccessMode.None;

        foreach (var triple in acpTriples)
        {
            if (triple.Subject != policyNode) continue;

            if (triple.Predicate == AcpAllow)
            {
                allowed |= AccessModeExtensions.FromAcpIri(triple.Object.AsSpan());
            }
            else if (triple.Predicate == AcpDeny)
            {
                denied |= AccessModeExtensions.FromAcpIri(triple.Object.AsSpan());
            }
        }

        return (allowed, denied);
    }

    private async Task<bool> EvaluateMatchersAsync(
        string policyNode,
        string? agentWebId,
        IReadOnlyList<(string Subject, string Predicate, string Object)> acpTriples,
        CancellationToken ct)
    {
        // Find matcher conditions
        var allOfMatchers = new List<string>();
        var anyOfMatchers = new List<string>();
        var noneOfMatchers = new List<string>();

        foreach (var triple in acpTriples)
        {
            if (triple.Subject != policyNode) continue;

            if (triple.Predicate == AcpAllOf)
                allOfMatchers.Add(triple.Object);
            else if (triple.Predicate == AcpAnyOf)
                anyOfMatchers.Add(triple.Object);
            else if (triple.Predicate == AcpNoneOf)
                noneOfMatchers.Add(triple.Object);
        }

        // If no matchers, policy doesn't apply
        if (allOfMatchers.Count == 0 && anyOfMatchers.Count == 0)
        {
            return false;
        }

        // allOf: All matchers must match
        foreach (var matcher in allOfMatchers)
        {
            if (!await EvaluateMatcherAsync(matcher, agentWebId, acpTriples, ct))
                return false;
        }

        // anyOf: At least one matcher must match (if present)
        if (anyOfMatchers.Count > 0)
        {
            bool anyMatched = false;
            foreach (var matcher in anyOfMatchers)
            {
                if (await EvaluateMatcherAsync(matcher, agentWebId, acpTriples, ct))
                {
                    anyMatched = true;
                    break;
                }
            }
            if (!anyMatched)
                return false;
        }

        // noneOf: No matcher should match
        foreach (var matcher in noneOfMatchers)
        {
            if (await EvaluateMatcherAsync(matcher, agentWebId, acpTriples, ct))
                return false;
        }

        return true;
    }

    private Task<bool> EvaluateMatcherAsync(
        string matcherNode,
        string? agentWebId,
        IReadOnlyList<(string Subject, string Predicate, string Object)> acpTriples,
        CancellationToken ct)
    {
        foreach (var triple in acpTriples)
        {
            if (triple.Subject != matcherNode) continue;

            if (triple.Predicate == AcpAgent)
            {
                // Check for special agent types
                if (triple.Object == AcpPublic)
                    return Task.FromResult(true);

                if (triple.Object == AcpAuthenticated)
                    return Task.FromResult(agentWebId != null);

                // Specific agent match
                if (agentWebId != null && UriMatches(triple.Object, agentWebId))
                    return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private static bool UriMatches(string uri1, string uri2)
    {
        // Strip angle brackets if present
        var span1 = uri1.AsSpan().Trim();
        var span2 = uri2.AsSpan().Trim();

        if (span1.Length > 0 && span1[0] == '<')
            span1 = span1.Slice(1);
        if (span1.Length > 0 && span1[^1] == '>')
            span1 = span1.Slice(0, span1.Length - 1);

        if (span2.Length > 0 && span2[0] == '<')
            span2 = span2.Slice(1);
        if (span2.Length > 0 && span2[^1] == '>')
            span2 = span2.Slice(0, span2.Length - 1);

        return span1.Equals(span2, StringComparison.OrdinalIgnoreCase);
    }

    public string? GetAccessControlDocumentUri(string resourceUri)
    {
        return _documentProvider.GetAccessControlDocumentUri(resourceUri);
    }
}
