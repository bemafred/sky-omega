// WebAccessControl.cs
// W3C Web Access Control (WAC) implementation for Solid.
// https://solidproject.org/TR/wac
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.AccessControl;

/// <summary>
/// Web Access Control (WAC) implementation.
/// Evaluates access based on .acl documents following W3C WAC spec.
/// </summary>
public sealed class WebAccessControl : IAccessPolicy
{
    private readonly IAccessControlDocumentProvider _documentProvider;

    // WAC namespace
    private const string AclNs = "http://www.w3.org/ns/auth/acl#";
    private const string FoafNs = "http://xmlns.com/foaf/0.1/";

    // WAC predicates
    private const string AclAuthorization = AclNs + "Authorization";
    private const string AclAgent = AclNs + "agent";
    private const string AclAgentClass = AclNs + "agentClass";
    private const string AclAgentGroup = AclNs + "agentGroup";
    private const string AclAccessTo = AclNs + "accessTo";
    private const string AclDefault = AclNs + "default";
    private const string AclMode = AclNs + "mode";
    private const string AclOrigin = AclNs + "origin";

    // Common agent classes
    private const string FoafAgent = FoafNs + "Agent"; // Anyone (authenticated or not)
    private const string AclAuthenticatedAgent = AclNs + "AuthenticatedAgent"; // Any authenticated agent

    public WebAccessControl(IAccessControlDocumentProvider documentProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
    }

    public async ValueTask<AccessDecision> EvaluateAsync(
        string? agentWebId,
        string resourceUri,
        AccessMode requestedMode,
        CancellationToken ct = default)
    {
        var grantedModes = await GetGrantedModesAsync(agentWebId, resourceUri, ct);

        // Check if Write is granted - Write implies Append
        if ((requestedMode & AccessMode.Append) != 0 && (grantedModes & AccessMode.Write) != 0)
        {
            grantedModes |= AccessMode.Append;
        }

        if ((grantedModes & requestedMode) == requestedMode)
        {
            var aclUri = GetAccessControlDocumentUri(resourceUri);
            return AccessDecision.Allow(grantedModes, "Access granted by WAC policy", aclUri);
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
        // Load ACL document
        var aclTriples = await _documentProvider.GetAccessControlDocumentAsync(resourceUri, ct);
        if (aclTriples == null || aclTriples.Count == 0)
        {
            return AccessMode.None;
        }

        var grantedModes = AccessMode.None;

        // Find all Authorization nodes
        var authNodes = new HashSet<string>();
        foreach (var triple in aclTriples)
        {
            if (triple.Object == AclAuthorization && triple.Predicate.EndsWith("#type") || triple.Predicate.EndsWith("/type"))
            {
                authNodes.Add(triple.Subject);
            }
            // Also match rdf:type
            if (triple.Predicate == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type" && triple.Object == AclAuthorization)
            {
                authNodes.Add(triple.Subject);
            }
        }

        // Evaluate each authorization
        foreach (var authNode in authNodes)
        {
            if (await EvaluateAuthorizationAsync(authNode, agentWebId, resourceUri, aclTriples, ct))
            {
                // Get modes granted by this authorization
                foreach (var triple in aclTriples)
                {
                    if (triple.Subject == authNode && triple.Predicate == AclMode)
                    {
                        grantedModes |= AccessModeExtensions.FromWacIri(triple.Object.AsSpan());
                    }
                }
            }
        }

        return grantedModes;
    }

    private async Task<bool> EvaluateAuthorizationAsync(
        string authNode,
        string? agentWebId,
        string resourceUri,
        IReadOnlyList<(string Subject, string Predicate, string Object)> aclTriples,
        CancellationToken ct)
    {
        // Check if this authorization applies to the resource
        bool appliesToResource = false;
        bool appliesToDefault = false;

        foreach (var triple in aclTriples)
        {
            if (triple.Subject != authNode) continue;

            if (triple.Predicate == AclAccessTo)
            {
                if (UriMatches(triple.Object, resourceUri))
                {
                    appliesToResource = true;
                }
            }
            else if (triple.Predicate == AclDefault)
            {
                // Default authorization applies to container contents
                if (resourceUri.StartsWith(triple.Object, StringComparison.OrdinalIgnoreCase))
                {
                    appliesToDefault = true;
                }
            }
        }

        if (!appliesToResource && !appliesToDefault)
            return false;

        // Check if the agent is authorized
        foreach (var triple in aclTriples)
        {
            if (triple.Subject != authNode) continue;

            if (triple.Predicate == AclAgent)
            {
                // Exact agent match
                if (agentWebId != null && UriMatches(triple.Object, agentWebId))
                    return true;
            }
            else if (triple.Predicate == AclAgentClass)
            {
                // foaf:Agent = anyone (including unauthenticated)
                if (triple.Object == FoafAgent)
                    return true;

                // acl:AuthenticatedAgent = any authenticated user
                if (triple.Object == AclAuthenticatedAgent && agentWebId != null)
                    return true;
            }
            else if (triple.Predicate == AclAgentGroup)
            {
                // Check group membership
                if (agentWebId != null && await IsAgentInGroupAsync(agentWebId, triple.Object, ct))
                    return true;
            }
        }

        return false;
    }

    private async Task<bool> IsAgentInGroupAsync(string agentWebId, string groupUri, CancellationToken ct)
    {
        // Fetch the group document (could be in a vCard group or custom format)
        // For now, we fetch the WebID profile and look for vcard:hasMember
        var groupTriples = await _documentProvider.GetWebIdProfileAsync(groupUri, ct);
        if (groupTriples == null)
            return false;

        foreach (var triple in groupTriples)
        {
            // vCard group member
            if (triple.Subject == groupUri &&
                (triple.Predicate == "http://www.w3.org/2006/vcard/ns#hasMember" ||
                 triple.Predicate == "http://www.w3.org/2006/vcard/ns#member") &&
                UriMatches(triple.Object, agentWebId))
            {
                return true;
            }
        }

        return false;
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
