// AccessMode.cs
// Solid Protocol access modes for WAC and ACP authorization.
// Based on W3C Web Access Control and Access Control Policy specifications.
// https://solidproject.org/TR/wac
// https://solidproject.org/TR/acp
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Solid.AccessControl;

/// <summary>
/// Access modes supported by Solid authorization systems (WAC and ACP).
/// These map to both acl: and acp: namespace predicates.
/// </summary>
[Flags]
public enum AccessMode
{
    /// <summary>
    /// No access rights.
    /// </summary>
    None = 0,

    /// <summary>
    /// Read access - allows GET and HEAD requests.
    /// WAC: acl:Read, ACP: acp:Read
    /// </summary>
    Read = 1 << 0,

    /// <summary>
    /// Write access - allows PUT, POST, PATCH, DELETE requests.
    /// WAC: acl:Write, ACP: acp:Write
    /// </summary>
    Write = 1 << 1,

    /// <summary>
    /// Append access - allows POST requests only (add without modify).
    /// WAC: acl:Append, ACP: acp:Append
    /// </summary>
    Append = 1 << 2,

    /// <summary>
    /// Control access - allows modifying ACL/ACP documents.
    /// WAC: acl:Control, ACP: acp:Control
    /// </summary>
    Control = 1 << 3,

    /// <summary>
    /// All access rights combined.
    /// </summary>
    All = Read | Write | Append | Control
}

/// <summary>
/// Extension methods for AccessMode.
/// </summary>
public static class AccessModeExtensions
{
    /// <summary>
    /// Returns the WAC IRI for this access mode.
    /// </summary>
    public static string ToWacIri(this AccessMode mode)
    {
        return mode switch
        {
            AccessMode.Read => "http://www.w3.org/ns/auth/acl#Read",
            AccessMode.Write => "http://www.w3.org/ns/auth/acl#Write",
            AccessMode.Append => "http://www.w3.org/ns/auth/acl#Append",
            AccessMode.Control => "http://www.w3.org/ns/auth/acl#Control",
            _ => throw new ArgumentException($"Cannot convert combined mode {mode} to single IRI", nameof(mode))
        };
    }

    /// <summary>
    /// Returns the ACP IRI for this access mode.
    /// </summary>
    public static string ToAcpIri(this AccessMode mode)
    {
        return mode switch
        {
            AccessMode.Read => "http://www.w3.org/ns/solid/acp#Read",
            AccessMode.Write => "http://www.w3.org/ns/solid/acp#Write",
            AccessMode.Append => "http://www.w3.org/ns/solid/acp#Append",
            AccessMode.Control => "http://www.w3.org/ns/solid/acp#Control",
            _ => throw new ArgumentException($"Cannot convert combined mode {mode} to single IRI", nameof(mode))
        };
    }

    /// <summary>
    /// Parses a WAC mode IRI to an AccessMode.
    /// </summary>
    public static AccessMode FromWacIri(ReadOnlySpan<char> iri)
    {
        if (iri.EndsWith("Read".AsSpan(), StringComparison.Ordinal))
            return AccessMode.Read;
        if (iri.EndsWith("Write".AsSpan(), StringComparison.Ordinal))
            return AccessMode.Write;
        if (iri.EndsWith("Append".AsSpan(), StringComparison.Ordinal))
            return AccessMode.Append;
        if (iri.EndsWith("Control".AsSpan(), StringComparison.Ordinal))
            return AccessMode.Control;
        return AccessMode.None;
    }

    /// <summary>
    /// Parses an ACP mode IRI to an AccessMode.
    /// </summary>
    public static AccessMode FromAcpIri(ReadOnlySpan<char> iri)
    {
        // ACP uses same suffixes as WAC
        return FromWacIri(iri);
    }

    /// <summary>
    /// Gets the HTTP method access requirement.
    /// </summary>
    public static AccessMode RequiredForMethod(string httpMethod)
    {
        return httpMethod.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => AccessMode.Read,
            "POST" => AccessMode.Append, // Note: Write also grants POST
            "PUT" or "PATCH" or "DELETE" => AccessMode.Write,
            "OPTIONS" => AccessMode.None, // CORS preflight doesn't require auth
            _ => AccessMode.None
        };
    }
}
