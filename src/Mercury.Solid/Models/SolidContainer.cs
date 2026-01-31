// SolidContainer.cs
// Solid LDP container semantics.
// Based on W3C Linked Data Platform 1.0.
// https://www.w3.org/TR/ldp/
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.Models;

/// <summary>
/// Represents a Solid LDP container with its members.
/// </summary>
public sealed class SolidContainer
{
    /// <summary>
    /// The container URI.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// The container type.
    /// </summary>
    public ContainerType Type { get; init; } = ContainerType.BasicContainer;

    /// <summary>
    /// The ETag for conditional requests.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Last modification time.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The contained resources (ldp:contains).
    /// </summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>
    /// For DirectContainer: the membership resource.
    /// </summary>
    public string? MembershipResource { get; init; }

    /// <summary>
    /// For DirectContainer: the has-member relation.
    /// </summary>
    public string? HasMemberRelation { get; init; }

    /// <summary>
    /// For IndirectContainer: the inserted content relation.
    /// </summary>
    public string? InsertedContentRelation { get; init; }

    /// <summary>
    /// LDP namespace constants.
    /// </summary>
    public static class Ldp
    {
        public const string Namespace = "http://www.w3.org/ns/ldp#";
        public const string Resource = Namespace + "Resource";
        public const string RDFSource = Namespace + "RDFSource";
        public const string NonRDFSource = Namespace + "NonRDFSource";
        public const string Container = Namespace + "Container";
        public const string BasicContainer = Namespace + "BasicContainer";
        public const string DirectContainer = Namespace + "DirectContainer";
        public const string IndirectContainer = Namespace + "IndirectContainer";
        public const string Contains = Namespace + "contains";
        public const string MembershipResource = Namespace + "membershipResource";
        public const string HasMemberRelation = Namespace + "hasMemberRelation";
        public const string IsMemberOfRelation = Namespace + "isMemberOfRelation";
        public const string InsertedContentRelation = Namespace + "insertedContentRelation";
    }

    /// <summary>
    /// Generates the containment RDF triples for this container.
    /// </summary>
    public IEnumerable<(string Subject, string Predicate, string Object)> GetContainmentTriples()
    {
        // Type triples
        yield return (Uri, "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", Ldp.Container);
        yield return (Uri, "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", Ldp.BasicContainer);

        if (Type == ContainerType.DirectContainer)
        {
            yield return (Uri, "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", Ldp.DirectContainer);
        }
        else if (Type == ContainerType.IndirectContainer)
        {
            yield return (Uri, "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", Ldp.IndirectContainer);
        }

        // Containment triples
        foreach (var member in Members)
        {
            yield return (Uri, Ldp.Contains, member);
        }

        // DirectContainer membership
        if (Type == ContainerType.DirectContainer && MembershipResource != null && HasMemberRelation != null)
        {
            yield return (Uri, Ldp.MembershipResource, MembershipResource);
            yield return (Uri, Ldp.HasMemberRelation, HasMemberRelation);
        }

        // IndirectContainer
        if (Type == ContainerType.IndirectContainer && InsertedContentRelation != null)
        {
            yield return (Uri, Ldp.InsertedContentRelation, InsertedContentRelation);
        }
    }

    /// <summary>
    /// Generates a slug for a new resource in this container.
    /// </summary>
    public static string GenerateSlug(string? preferredSlug = null)
    {
        if (!string.IsNullOrEmpty(preferredSlug))
        {
            // Sanitize the slug
            return SanitizeSlug(preferredSlug);
        }

        // Generate a UUID-based slug
        return Guid.NewGuid().ToString("N")[..8];
    }

    private static string SanitizeSlug(string slug)
    {
        // Remove problematic characters for URIs
        var sanitized = new char[slug.Length];
        int j = 0;
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
            {
                sanitized[j++] = c;
            }
            else if (c == ' ')
            {
                sanitized[j++] = '-';
            }
        }
        return new string(sanitized, 0, j);
    }
}
