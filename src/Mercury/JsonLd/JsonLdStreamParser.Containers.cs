// JsonLdStreamParser.Containers.cs
// Container map processing (@language, @id, @type, @graph, @index)

using System;
using System.Text;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    /// <summary>
    /// Process a language map (@container: @language).
    /// Object keys are language tags, values are strings or arrays of strings.
    /// </summary>
    private void ProcessLanguageMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var langTag = prop.Name;
            var langValue = prop.Value;

            // @none means no language tag - output as plain literal
            var isNone = langTag == "@none" || _noneAliases.Contains(langTag);

            if (langValue.ValueKind == JsonValueKind.String)
            {
                var strVal = langValue.GetString() ?? "";
                var literal = isNone
                    ? $"\"{EscapeString(strVal)}\""
                    : $"\"{EscapeString(strVal)}\"@{langTag}";
                EmitQuad(handler, subject, predicate, literal, graphIri);
            }
            else if (langValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple values for same language
                foreach (var item in langValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var strVal = item.GetString() ?? "";
                        var literal = isNone
                            ? $"\"{EscapeString(strVal)}\""
                            : $"\"{EscapeString(strVal)}\"@{langTag}";
                        EmitQuad(handler, subject, predicate, literal, graphIri);
                    }
                    else if (item.ValueKind != JsonValueKind.Null)
                    {
                        // Language map values must be strings (er35)
                        throw new InvalidOperationException("invalid language map value");
                    }
                }
            }
            else if (langValue.ValueKind != JsonValueKind.Null)
            {
                // Language map values must be strings or arrays of strings (er35)
                throw new InvalidOperationException("invalid language map value");
            }
        }
    }

    /// <summary>
    /// Process an @id container map (@container: @id).
    /// Object keys become the @id of the nested objects.
    /// @none (or alias) means generate a blank node.
    /// </summary>
    private void ProcessIdMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var idKey = prop.Name;
            var idValue = prop.Value;

            // @none or alias means no @id - generate blank node
            var isNone = idKey == "@none" || _noneAliases.Contains(idKey);

            if (idValue.ValueKind == JsonValueKind.Object)
            {
                // Process nested object with the key as @id
                string nodeId;
                if (isNone)
                {
                    nodeId = GenerateBlankNode();
                }
                else
                {
                    nodeId = ExpandIri(idKey);
                }

                // Create a temporary reader for the nested object
                var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(idValue.GetRawText()));
                tempReader.Read();

                // Parse the nested node with the specified @id
                var nestedId = ParseNodeWithId(ref tempReader, handler, nodeId, subject);
                EmitQuad(handler, subject, predicate, nestedId, graphIri);
            }
            else if (idValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple objects with same @id
                foreach (var item in idValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string nodeId;
                        if (isNone)
                        {
                            nodeId = GenerateBlankNode();
                        }
                        else
                        {
                            nodeId = ExpandIri(idKey);
                        }

                        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(item.GetRawText()));
                        tempReader.Read();
                        var nestedId = ParseNodeWithId(ref tempReader, handler, nodeId, subject);
                        EmitQuad(handler, subject, predicate, nestedId, graphIri);
                    }
                }
            }
        }
    }

    private void ProcessTypeMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri, string? coercedType = null)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var typeKey = prop.Name;
            var typeValue = prop.Value;

            // @none or alias means no @type - don't emit rdf:type triple
            var isNone = typeKey == "@none" || _noneAliases.Contains(typeKey);
            var expandedTypeIri = isNone ? null : ExpandTypeIri(typeKey);

            if (typeValue.ValueKind == JsonValueKind.Object)
            {
                // Process nested object with the key as @type
                var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(typeValue.GetRawText()));
                tempReader.Read();

                // Parse the nested node with the specified @type (pass term for scoped context lookup)
                var nestedId = ParseNodeWithType(ref tempReader, handler, expandedTypeIri, typeKey, subject);
                EmitQuad(handler, subject, predicate, nestedId, graphIri);
            }
            else if (typeValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple objects with same @type
                foreach (var item in typeValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(item.GetRawText()));
                        tempReader.Read();
                        var nestedId = ParseNodeWithType(ref tempReader, handler, expandedTypeIri, typeKey, subject);
                        EmitQuad(handler, subject, predicate, nestedId, graphIri);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        // String value in @type map is a node reference (m017)
                        var stringVal = item.GetString() ?? "";
                        // Use @vocab expansion if coercedType is @vocab (m019), otherwise use @base (m017/m018)
                        var nodeRef = coercedType == "@vocab" ? ExpandTerm(stringVal) : ExpandIri(stringVal);
                        if (!string.IsNullOrEmpty(nodeRef))
                        {
                            // Emit type triple if not @none
                            if (!isNone && !string.IsNullOrEmpty(expandedTypeIri))
                            {
                                EmitQuad(handler, nodeRef, RdfType, expandedTypeIri, graphIri);
                            }
                            EmitQuad(handler, subject, predicate, nodeRef, graphIri);
                        }
                    }
                }
            }
            else if (typeValue.ValueKind == JsonValueKind.String)
            {
                // String value in @type map is a node reference (m017)
                // The string is the @id of a node with the key as its @type
                var stringVal = typeValue.GetString() ?? "";
                // Use @vocab expansion if coercedType is @vocab (m019), otherwise use @base (m017/m018)
                var nodeRef = coercedType == "@vocab" ? ExpandTerm(stringVal) : ExpandIri(stringVal);
                if (!string.IsNullOrEmpty(nodeRef))
                {
                    // Emit type triple if not @none
                    if (!isNone && !string.IsNullOrEmpty(expandedTypeIri))
                    {
                        EmitQuad(handler, nodeRef, RdfType, expandedTypeIri, graphIri);
                    }
                    EmitQuad(handler, subject, predicate, nodeRef, graphIri);
                }
            }
        }
    }

    /// <summary>
    /// Process a graph container value.
    /// The value becomes a named graph with the property linking to the graph node.
    /// </summary>
    private void ProcessGraphContainer(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage,
        bool isIndexContainer = false, bool isIdContainer = false,
        string? indexPropIri = null, string? indexPropCoercedType = null)
    {
        // Handle compound container [@graph, @index] or [@graph, @id]
        if ((isIndexContainer || isIdContainer) && value.ValueKind == JsonValueKind.Object)
        {
            // Object keys are indexes (@index) or graph IDs (@id)
            foreach (var prop in value.EnumerateObject())
            {
                var key = prop.Name;
                var itemValue = prop.Value;

                // Check for @none (no @index property emission for pi11)
                bool isNone = key == "@none" || _noneAliases.Contains(key);

                // For @id container, the key becomes the graph @id
                // @none (or alias) means default graph - don't expand it (m015, m016)
                string? graphIdFromKey = null;
                if (isIdContainer)
                {
                    if (!isNone)
                    {
                        graphIdFromKey = ExpandIri(key, expandTerms: false);
                    }
                }

                if (itemValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemValue.EnumerateArray())
                    {
                        ProcessGraphContainerItem(subject, predicate, item, handler, graphIri, coercedType, termLanguage, graphIdFromKey, isCompoundContainer: true, indexPropIri, key, indexPropCoercedType, isNone);
                    }
                }
                else
                {
                    ProcessGraphContainerItem(subject, predicate, itemValue, handler, graphIri, coercedType, termLanguage, graphIdFromKey, isCompoundContainer: true, indexPropIri, key, indexPropCoercedType, isNone);
                }
            }
        }
        // Handle arrays - each item is a separate graph
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ProcessGraphContainerItem(subject, predicate, item, handler, graphIri, coercedType, termLanguage);
            }
        }
        else
        {
            ProcessGraphContainerItem(subject, predicate, value, handler, graphIri, coercedType, termLanguage);
        }
    }

    private void ProcessGraphContainerItem(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage,
        string? graphIdFromKey = null, bool isCompoundContainer = false,
        string? indexPropIri = null, string? indexKey = null, string? indexPropCoercedType = null, bool indexIsNone = false)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            // Non-object values in graph container are processed normally
            ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLanguage);
            return;
        }

        // Check if the object has @id - use that as the graph name
        // Priority: explicit @id in object > graphIdFromKey from compound container key
        string? explicitId = null;
        if (value.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            explicitId = ExpandIri(idProp.GetString() ?? "", expandTerms: false);
        }
        else if (graphIdFromKey != null)
        {
            explicitId = graphIdFromKey;
        }

        // Check if value already has @graph - if so, we need different handling
        bool hasInnerGraph = value.TryGetProperty("@graph", out var graphProp);

        // Generate blank nodes for the link object and graph
        // - For simple @graph container with inner @graph: use DIFFERENT blank nodes (e081)
        // - For compound container [@graph, @index] with inner @graph: use SAME blank node (e084)
        // - When value doesn't have @graph: always use same ID for both
        string linkObject = explicitId ?? GenerateBlankNode();
        string namedGraphIri;
        if (hasInnerGraph && !isCompoundContainer)
        {
            // Simple @graph container with inner @graph - use separate blank node
            namedGraphIri = GenerateBlankNode();
        }
        else
        {
            // Compound container with inner @graph OR no inner @graph - use same ID for both
            namedGraphIri = linkObject;
        }

        // Emit the link from subject to the graph node
        EmitQuad(handler, subject, predicate, linkObject, graphIri);

        // Emit property-valued @index triple if defined and not @none (pi11)
        if (!string.IsNullOrEmpty(indexPropIri) && !string.IsNullOrEmpty(indexKey) && !indexIsNone)
        {
            string indexValue;
            if (indexPropCoercedType == "@vocab")
            {
                indexValue = ExpandTerm(indexKey);
            }
            else if (indexPropCoercedType == "@id")
            {
                indexValue = ExpandIri(indexKey);
            }
            else
            {
                // Default to string literal
                indexValue = $"\"{EscapeString(indexKey)}\"";
            }
            EmitQuad(handler, linkObject, indexPropIri, indexValue, graphIri);
        }

        // Save current graph and set to the named graph for content processing
        var savedGraph = _currentGraph;
        _currentGraph = namedGraphIri;

        try
        {
            // Process the content - check for @graph property or process as regular node
            if (hasInnerGraph)
            {
                // Value has explicit @graph - process its contents into the named graph
                if (graphProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in graphProp.EnumerateArray())
                    {
                        if (node.ValueKind == JsonValueKind.Object)
                        {
                            ProcessGraphNode(node, handler);
                        }
                    }
                }
                else if (graphProp.ValueKind == JsonValueKind.Object)
                {
                    ProcessGraphNode(graphProp, handler);
                }
            }
            else
            {
                // No explicit @graph - process the object itself into the named graph
                ProcessGraphNode(value, handler);
            }
        }
        finally
        {
            // Restore the previous graph
            _currentGraph = savedGraph;
        }
    }
}
