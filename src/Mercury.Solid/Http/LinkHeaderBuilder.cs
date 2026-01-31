// LinkHeaderBuilder.cs
// Solid Link header generation for resource discovery.
// Based on W3C Solid Protocol.
// https://solidproject.org/TR/protocol
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Text;
using SkyOmega.Mercury.Solid.Models;

namespace SkyOmega.Mercury.Solid.Http;

/// <summary>
/// Builds Link headers for Solid resources following the Solid Protocol.
/// </summary>
public sealed class LinkHeaderBuilder
{
    private readonly List<(string Uri, string Rel, string? Type)> _links = new();

    /// <summary>
    /// Adds a link to the header.
    /// </summary>
    public LinkHeaderBuilder AddLink(string uri, string rel, string? type = null)
    {
        _links.Add((uri, rel, type));
        return this;
    }

    /// <summary>
    /// Adds the ACL link for a resource.
    /// </summary>
    public LinkHeaderBuilder AddAclLink(string aclUri)
    {
        return AddLink(aclUri, "acl");
    }

    /// <summary>
    /// Adds the describedby link for resource metadata.
    /// </summary>
    public LinkHeaderBuilder AddDescribedByLink(string metaUri)
    {
        return AddLink(metaUri, "describedby");
    }

    /// <summary>
    /// Adds LDP type links for a resource.
    /// </summary>
    public LinkHeaderBuilder AddLdpTypeLinks(bool isContainer, bool isRdfSource)
    {
        AddLink(SolidContainer.Ldp.Resource, "type");

        if (isRdfSource)
        {
            AddLink(SolidContainer.Ldp.RDFSource, "type");
        }
        else
        {
            AddLink(SolidContainer.Ldp.NonRDFSource, "type");
        }

        if (isContainer)
        {
            AddLink(SolidContainer.Ldp.Container, "type");
            AddLink(SolidContainer.Ldp.BasicContainer, "type");
        }

        return this;
    }

    /// <summary>
    /// Adds storage discovery link.
    /// </summary>
    public LinkHeaderBuilder AddStorageLink(string storageUri)
    {
        return AddLink(storageUri, "http://www.w3.org/ns/pim/space#storage");
    }

    /// <summary>
    /// Adds WebSocket notification endpoint link.
    /// </summary>
    public LinkHeaderBuilder AddWebSocketLink(string wsUri)
    {
        return AddLink(wsUri, "http://www.w3.org/ns/solid/terms#updatesVia");
    }

    /// <summary>
    /// Builds the Link header value.
    /// </summary>
    public string Build()
    {
        if (_links.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        bool first = true;

        foreach (var (uri, rel, type) in _links)
        {
            if (!first)
                sb.Append(", ");
            first = false;

            sb.Append('<');
            sb.Append(uri);
            sb.Append(">; rel=\"");
            sb.Append(rel);
            sb.Append('"');

            if (type != null)
            {
                sb.Append("; type=\"");
                sb.Append(type);
                sb.Append('"');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates Link headers for a Solid resource.
    /// </summary>
    public static string ForResource(SolidResource resource, string? aclUri = null)
    {
        var builder = new LinkHeaderBuilder()
            .AddLdpTypeLinks(resource.IsContainer, resource.IsRdfSource);

        if (aclUri != null)
        {
            builder.AddAclLink(aclUri);
        }

        return builder.Build();
    }

    /// <summary>
    /// Creates Link headers for a container.
    /// </summary>
    public static string ForContainer(SolidContainer container, string? aclUri = null)
    {
        var builder = new LinkHeaderBuilder()
            .AddLdpTypeLinks(true, true);

        if (aclUri != null)
        {
            builder.AddAclLink(aclUri);
        }

        return builder.Build();
    }
}
