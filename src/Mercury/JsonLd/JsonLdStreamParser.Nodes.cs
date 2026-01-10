// JsonLdStreamParser.Nodes.cs
// Node parsing and graph handling

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    private string ParseNode(ref Utf8JsonReader reader, QuadHandler handler, string? parentSubject)
    {
        string? subject = null;
        var properties = new List<(string predicate, JsonElement value)>();
        string? graphIri = null;
        bool hasGraphKeyword = false;

        // First pass: collect all properties and handle @context/@id/@graph
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Process @context first if present
        if (root.TryGetProperty("@context", out var contextElement))
        {
            ProcessContext(contextElement);
        }

        // Track whether type-scoped context was applied (for nested node restoration)
        // Type-scoped contexts apply to this node's properties but should not propagate to nested nodes
        // Property-scoped contexts DO propagate, so we only save state if type-scoped context is applied
        Dictionary<string, string>? savedContextForNested = null;
        string? savedVocabForNested = null;
        string? savedBaseForNested = null;
        Dictionary<string, string?>? typeScopedTermChanges = null;     // Terms added/modified by type-scoped (value = original IRI, null if new)
        Dictionary<string, string?>? typeScopedCoercionChanges = null; // Coercions added/modified by type-scoped
        bool hasTypeScopedContext = false;
        JsonElement typeElement = default;
        List<string>? expandedTypeIris = null; // Type IRIs expanded BEFORE applying type-scoped context

        // Check for @type and apply type-scoped context BEFORE expanding @id
        // IMPORTANT: Expand type IRIs BEFORE applying type-scoped context
        // (type-scoped context changes @vocab which should NOT affect the @type IRI itself)
        // Check both literal @type AND ALL @type aliases (multiple properties can alias to @type)
        List<JsonElement> typeElements = new();
        bool foundType = root.TryGetProperty("@type", out typeElement);
        if (foundType)
        {
            typeElements.Add(typeElement);
        }
        // Also check ALL @type aliases - multiple properties can alias to @type (e.g., type1, type2)
        foreach (var alias in _typeAliases)
        {
            if (root.TryGetProperty(alias, out var aliasElement))
            {
                typeElements.Add(aliasElement);
                foundType = true;
            }
        }
        if (foundType)
        {
            // Expand type IRIs using current context (BEFORE type-scoped context)
            // Combine types from all @type and alias properties
            expandedTypeIris = new List<string>();
            foreach (var te in typeElements)
            {
                expandedTypeIris.AddRange(ExpandTypeIris(te));
            }
            // Check if ANY type has a scoped context BEFORE applying
            hasTypeScopedContext = typeElements.Any(te => HasTypeScopedContext(te));

            if (hasTypeScopedContext)
            {
                // Save state BEFORE applying type-scoped contexts
                savedContextForNested = new Dictionary<string, string>(_context);
                savedVocabForNested = _vocabIri;
                savedBaseForNested = _baseIri;

                // Track terms, coercions, and containers BEFORE type-scoped context
                var termsBefore = new Dictionary<string, string>(_context);
                var coercionsBefore = new Dictionary<string, string>(_typeCoercion);
                var containerTypeBefore = new Dictionary<string, bool>(_containerType);
                var containerIndexBefore = new Dictionary<string, bool>(_containerIndex);
                var containerListBefore = new Dictionary<string, bool>(_containerList);
                var containerLangBefore = new Dictionary<string, bool>(_containerLanguage);
                var containerGraphBefore = new Dictionary<string, bool>(_containerGraph);
                var containerIdBefore = new Dictionary<string, bool>(_containerId);

                // Reset @propagate flag before applying type-scoped contexts
                // It will be set to true if any type-scoped context has @propagate: true
                _typeScopedPropagate = false;

                // Apply type-scoped contexts from ALL type elements
                foreach (var te in typeElements)
                {
                    ApplyTypeScopedContexts(te);
                }

                // Track what was ADDED or MODIFIED by type-scoped context
                // Store the original value (null if term was added, original IRI if modified)
                typeScopedTermChanges = new Dictionary<string, string?>();
                foreach (var kv in _context)
                {
                    if (!termsBefore.TryGetValue(kv.Key, out var oldValue))
                    {
                        // New term - store null as original
                        typeScopedTermChanges[kv.Key] = null;
                    }
                    else if (oldValue != kv.Value)
                    {
                        // Modified term - store original value
                        typeScopedTermChanges[kv.Key] = oldValue;
                    }
                }

                typeScopedCoercionChanges = new Dictionary<string, string?>();
                foreach (var kv in _typeCoercion)
                {
                    if (!coercionsBefore.TryGetValue(kv.Key, out var oldValue))
                    {
                        typeScopedCoercionChanges[kv.Key] = null;
                    }
                    else if (oldValue != kv.Value)
                    {
                        typeScopedCoercionChanges[kv.Key] = oldValue;
                    }
                }

                // Track container changes (null = newly added, true/false = original value)
                Dictionary<string, bool?>? typeScopedContainerTypeChanges = null;
                Dictionary<string, bool?>? typeScopedContainerIndexChanges = null;
                Dictionary<string, bool?>? typeScopedContainerListChanges = null;
                Dictionary<string, bool?>? typeScopedContainerLangChanges = null;
                Dictionary<string, bool?>? typeScopedContainerGraphChanges = null;
                Dictionary<string, bool?>? typeScopedContainerIdChanges = null;

                // Helper to track container changes
                void TrackContainerChanges(Dictionary<string, bool> before, Dictionary<string, bool> after, ref Dictionary<string, bool?>? changes)
                {
                    foreach (var kv in after)
                    {
                        if (!before.TryGetValue(kv.Key, out var oldValue))
                        {
                            // New container - store null (meaning remove on revert)
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = null;
                        }
                        else if (oldValue != kv.Value)
                        {
                            // Modified container
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = oldValue;
                        }
                    }
                    // Also track removed containers
                    foreach (var kv in before)
                    {
                        if (!after.ContainsKey(kv.Key))
                        {
                            // Container was removed - store original value
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = kv.Value;
                        }
                    }
                }

                TrackContainerChanges(containerTypeBefore, _containerType, ref typeScopedContainerTypeChanges);
                TrackContainerChanges(containerIndexBefore, _containerIndex, ref typeScopedContainerIndexChanges);
                TrackContainerChanges(containerListBefore, _containerList, ref typeScopedContainerListChanges);
                TrackContainerChanges(containerLangBefore, _containerLanguage, ref typeScopedContainerLangChanges);
                TrackContainerChanges(containerGraphBefore, _containerGraph, ref typeScopedContainerGraphChanges);
                TrackContainerChanges(containerIdBefore, _containerId, ref typeScopedContainerIdChanges);

                _typeScopedContainerTypeChanges = typeScopedContainerTypeChanges;
                _typeScopedContainerIndexChanges = typeScopedContainerIndexChanges;
                _typeScopedContainerListChanges = typeScopedContainerListChanges;
                _typeScopedContainerLangChanges = typeScopedContainerLangChanges;
                _typeScopedContainerGraphChanges = typeScopedContainerGraphChanges;
                _typeScopedContainerIdChanges = typeScopedContainerIdChanges;
            }
            else
            {
                // Apply type-scoped contexts (may change @base/@vocab)
                ApplyTypeScopedContexts(typeElement);
            }
        }

        // Get @id for subject (AFTER type-scoped context is applied)
        // Check both @id and any aliases for @id
        // Also detect colliding keywords (er26) - multiple @id-like properties
        int idPropertyCount = 0;
        bool hasExplicitId = false;
        if (root.TryGetProperty("@id", out var idElement))
        {
            idPropertyCount++;
            hasExplicitId = true;
            // @id must be a string (er27)
            if (idElement.ValueKind != JsonValueKind.String && idElement.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidOperationException("invalid @id value");
            }
            subject = ExpandIri(idElement.GetString() ?? "");
        }
        // Also check @id aliases - count them for collision detection
        foreach (var alias in _idAliases)
        {
            if (root.TryGetProperty(alias, out var aliasIdElement))
            {
                idPropertyCount++;
                hasExplicitId = true;
                if (subject == null)
                {
                    subject = ExpandIri(aliasIdElement.GetString() ?? "");
                }
            }
        }
        // Multiple @id properties is a collision (er26)
        if (idPropertyCount > 1)
        {
            throw new InvalidOperationException("colliding keywords");
        }

        // If @id was present but unresolvable (relative IRI + null base), skip the node entirely (e060)
        // Return null to signal the node should be dropped
        if (hasExplicitId && subject == null)
        {
            return null!;
        }

        // Check for @graph or @graph alias
        JsonElement graphElement = default;
        if (root.TryGetProperty("@graph", out graphElement))
        {
            hasGraphKeyword = true;
            // Subject becomes the graph IRI if present
            graphIri = subject;
        }
        else
        {
            // Check for @graph aliases
            foreach (var alias in _graphAliases)
            {
                if (root.TryGetProperty(alias, out graphElement))
                {
                    hasGraphKeyword = true;
                    graphIri = subject;
                    break;
                }
            }
        }

        // Generate blank node if no @id (but not if @id was present and unresolvable - handled above)
        subject ??= GenerateBlankNode();

        // If this node has @graph and we generated a blank node for subject,
        // update graphIri to match ONLY if the node has other properties
        // (a node with just @graph should process content in default graph)
        if (hasGraphKeyword && graphIri == null)
        {
            // Check if there are any non-keyword properties
            bool hasOtherProperties = false;
            foreach (var prop in root.EnumerateObject())
            {
                var name = prop.Name;
                if (!name.StartsWith('@') && !_graphAliases.Contains(name))
                {
                    hasOtherProperties = true;
                    break;
                }
            }
            if (hasOtherProperties)
            {
                graphIri = subject;
            }
        }

        // Emit type triple (after we have the subject)
        // Use pre-expanded type IRIs (expanded BEFORE type-scoped context was applied)
        if (expandedTypeIris != null)
        {
            foreach (var typeIri in expandedTypeIris)
            {
                EmitQuad(handler, subject, RdfType, typeIri, _currentGraph);
            }
        }

        // Store saved state for nested node restoration (only if type-scoped context was applied)
        var previousSavedContext = _savedContextForNested;
        var previousSavedVocab = _savedVocabForNested;
        var previousSavedBase = _savedBaseForNested;
        var previousTypeScopedTermChanges = _typeScopedTermChanges;
        var previousTypeScopedCoercionChanges = _typeScopedCoercionChanges;
        var previousTypeScopedPropagate = _typeScopedPropagate;
        var previousTypeScopedContainerTypeChanges = _typeScopedContainerTypeChanges;
        var previousTypeScopedContainerIndexChanges = _typeScopedContainerIndexChanges;
        var previousTypeScopedContainerListChanges = _typeScopedContainerListChanges;
        var previousTypeScopedContainerLangChanges = _typeScopedContainerLangChanges;
        var previousTypeScopedContainerGraphChanges = _typeScopedContainerGraphChanges;
        var previousTypeScopedContainerIdChanges = _typeScopedContainerIdChanges;
        if (hasTypeScopedContext)
        {
            _savedContextForNested = savedContextForNested;
            _savedVocabForNested = savedVocabForNested;
            _savedBaseForNested = savedBaseForNested;
            _typeScopedTermChanges = typeScopedTermChanges;
            _typeScopedCoercionChanges = typeScopedCoercionChanges;
            // Note: _typeScopedPropagate was already set during ApplyTypeScopedContexts
            // Container changes are already assigned to fields during tracking above
        }

        // Process @reverse keyword - contains reverse properties
        if (root.TryGetProperty("@reverse", out var reverseElement))
        {
            ProcessReverseKeyword(subject, reverseElement, handler, _currentGraph);
        }

        // Process @nest keyword and aliases - contains properties to be "un-nested" onto this node
        if (root.TryGetProperty("@nest", out var nestElement))
        {
            ProcessNestKeyword(subject, nestElement, handler, _currentGraph);
        }
        // Also process @nest aliases - use snapshot since ProcessNestKeyword may modify _nestAliases
        foreach (var alias in _nestAliases.ToList())
        {
            if (root.TryGetProperty(alias, out var aliasNestElement))
            {
                // Apply property-scoped context if present (c037)
                if (_scopedContext.TryGetValue(alias, out var scopedContextJson))
                {
                    // Save and apply context, then restore after processing
                    var savedVocab = _vocabIri;
                    var savedBase = _baseIri;
                    var savedContext = new Dictionary<string, string>(_context);
                    using var scopedDoc = JsonDocument.Parse(scopedContextJson);
                    ProcessContext(scopedDoc.RootElement);
                    try
                    {
                        ProcessNestKeyword(subject, aliasNestElement, handler, _currentGraph);
                    }
                    finally
                    {
                        _vocabIri = savedVocab;
                        _baseIri = savedBase;
                        _context.Clear();
                        foreach (var kv in savedContext) _context[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    ProcessNestKeyword(subject, aliasNestElement, handler, _currentGraph);
                }
            }
        }

        // Process other properties FIRST (before @graph content)
        // Properties on the containing node go in the current graph, not the named graph
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords we've already processed
            // Per JSON-LD 1.1: Terms that look like keywords (@ followed by only lowercase a-z) are ignored
            // But other @ patterns (like "@", "@foo.bar") can be term definitions (e119)
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            // Check for @type alias - already processed above (type emission and scoped context)
            if (_typeAliases.Contains(propName))
                continue;

            // Skip @id aliases (already processed above)
            if (_idAliases.Contains(propName))
                continue;

            // Skip @graph aliases (already processed above)
            if (_graphAliases.Contains(propName))
                continue;

            // Skip @included aliases (processed below)
            if (_includedAliases.Contains(propName))
                continue;

            // Skip @nest aliases (already processed above)
            if (_nestAliases.Contains(propName))
                continue;

            // Check if this is a reverse property (term definition has @reverse but no @id)
            // Reverse properties may not expand to a predicate IRI, but ProcessProperty handles them
            var isReverseProperty = _reverseProperty.ContainsKey(propName);

            var predicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(predicate) && !isReverseProperty)
                continue;

            ProcessProperty(subject, predicate, propName, prop.Value, handler, _currentGraph);
        }

        if (hasGraphKeyword)
        {
            // Process @graph contents - these go in the named graph
            var savedGraph = _currentGraph;
            _currentGraph = graphIri;

            if (graphElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in graphElement.EnumerateArray())
                {
                    ProcessGraphNode(node, handler);
                }
            }
            else if (graphElement.ValueKind == JsonValueKind.Object)
            {
                ProcessGraphNode(graphElement, handler);
            }

            _currentGraph = savedGraph;
        }

        // Process @included - additional nodes that are not linked to this node
        if (root.TryGetProperty("@included", out var includedElement))
        {
            ProcessIncludedNodes(includedElement, handler);
        }

        // Also check for @included aliases
        foreach (var alias in _includedAliases)
        {
            if (root.TryGetProperty(alias, out var aliasedIncluded))
            {
                ProcessIncludedNodes(aliasedIncluded, handler);
            }
        }

        // Restore context if type-scoped context was applied
        // This ensures type-scoped modifications don't leak to subsequent properties in the parent node
        if (hasTypeScopedContext && savedContextForNested != null)
        {
            _context.Clear();
            foreach (var kv in savedContextForNested) _context[kv.Key] = kv.Value;
            _vocabIri = savedVocabForNested;
            _baseIri = savedBaseForNested;
        }

        // Restore previous saved state
        _savedContextForNested = previousSavedContext;
        _savedVocabForNested = previousSavedVocab;
        _savedBaseForNested = previousSavedBase;
        _typeScopedTermChanges = previousTypeScopedTermChanges;
        _typeScopedCoercionChanges = previousTypeScopedCoercionChanges;
        _typeScopedPropagate = previousTypeScopedPropagate;
        _typeScopedContainerTypeChanges = previousTypeScopedContainerTypeChanges;
        _typeScopedContainerIndexChanges = previousTypeScopedContainerIndexChanges;
        _typeScopedContainerListChanges = previousTypeScopedContainerListChanges;
        _typeScopedContainerLangChanges = previousTypeScopedContainerLangChanges;
        _typeScopedContainerGraphChanges = previousTypeScopedContainerGraphChanges;
        _typeScopedContainerIdChanges = previousTypeScopedContainerIdChanges;

        return subject;
    }

    /// <summary>
    /// Process a node in @graph context, handling free-floating values, @set, and @list.
    /// </summary>
    private void ProcessGraphNode(JsonElement node, QuadHandler handler)
    {
        // Skip primitives - they are free-floating values
        if (node.ValueKind != JsonValueKind.Object)
            return;

        // Skip value objects - they are free-floating and produce no triples
        if (node.TryGetProperty("@value", out _))
            return;

        // Skip free-floating @list objects - they produce no output
        if (node.TryGetProperty("@list", out _))
            return;

        // Handle @set objects - process their contents
        if (node.TryGetProperty("@set", out var setElement))
        {
            if (setElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in setElement.EnumerateArray())
                {
                    ProcessGraphNode(item, handler);
                }
            }
            else
            {
                ProcessGraphNode(setElement, handler);
            }
            return;
        }

        // Regular node object - parse it
        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(node.GetRawText()));
        tempReader.Read();
        ParseNode(ref tempReader, handler, null);
    }

    /// <summary>
    /// Process @included nodes - additional nodes that are not linked to the containing node.
    /// </summary>
    private void ProcessIncludedNodes(JsonElement element, QuadHandler handler)
    {
        // @included value must be a node object or array of node objects (in07, in08, in09)
        // It cannot be a string, value object, or list object
        if (element.ValueKind == JsonValueKind.String)
        {
            throw new InvalidOperationException("invalid @included value");
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in element.EnumerateArray())
            {
                ValidateIncludedNode(node);
                ProcessGraphNode(node, handler);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            ValidateIncludedNode(element);
            ProcessGraphNode(element, handler);
        }
    }

    /// <summary>
    /// Validate that a node is valid for @included (must be a node object, not value/list object)
    /// </summary>
    private void ValidateIncludedNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("invalid @included value");
        }

        // Check for value object (has @value or @value alias)
        if (node.TryGetProperty("@value", out _))
        {
            throw new InvalidOperationException("invalid @included value");
        }
        foreach (var alias in _valueAliases)
        {
            if (node.TryGetProperty(alias, out _))
            {
                throw new InvalidOperationException("invalid @included value");
            }
        }

        // Check for list object (has @list)
        if (node.TryGetProperty("@list", out _))
        {
            throw new InvalidOperationException("invalid @included value");
        }
    }

    private string ParseNodeWithType(ref Utf8JsonReader reader, QuadHandler handler, string? typeIri, string? typeTerm, string? parentNode)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return GenerateBlankNode();

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Revert parent's type-scoped changes (type-scoped contexts don't propagate to nested nodes)
        // Save them for restoration after processing
        Dictionary<string, string>? savedCoercions = null;
        Dictionary<string, string>? savedTerms = null;
        var savedVocab = _vocabIri;
        var savedBase = _baseIri;

        if (!_typeScopedPropagate && _typeScopedCoercionChanges != null && _typeScopedCoercionChanges.Count > 0)
        {
            savedCoercions = new Dictionary<string, string>();
            foreach (var kv in _typeScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedCoercions[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    _typeCoercion.Remove(kv.Key);
                }
                else
                {
                    _typeCoercion[kv.Key] = kv.Value;
                }
            }
        }

        if (!_typeScopedPropagate && _typeScopedTermChanges != null && _typeScopedTermChanges.Count > 0)
        {
            savedTerms = new Dictionary<string, string>();
            foreach (var kv in _typeScopedTermChanges)
            {
                if (_context.TryGetValue(kv.Key, out var currentValue))
                {
                    savedTerms[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    _context.Remove(kv.Key);
                }
                else
                {
                    _context[kv.Key] = kv.Value;
                }
            }
        }

        // Revert type-scoped container changes
        Dictionary<string, bool>? savedContainerType = null;
        Dictionary<string, bool>? savedContainerIndex = null;
        Dictionary<string, bool>? savedContainerList = null;
        Dictionary<string, bool>? savedContainerLang = null;
        Dictionary<string, bool>? savedContainerGraph = null;
        Dictionary<string, bool>? savedContainerId = null;

        void RevertContainerChanges(Dictionary<string, bool?>? changes, Dictionary<string, bool> container, ref Dictionary<string, bool>? saved)
        {
            if (changes != null && changes.Count > 0)
            {
                saved = new Dictionary<string, bool>();
                foreach (var kv in changes)
                {
                    if (container.TryGetValue(kv.Key, out var currentValue))
                    {
                        saved[kv.Key] = currentValue;
                    }
                    if (kv.Value == null)
                    {
                        container.Remove(kv.Key);
                    }
                    else
                    {
                        container[kv.Key] = kv.Value.Value;
                    }
                }
            }
        }

        if (!_typeScopedPropagate)
        {
            RevertContainerChanges(_typeScopedContainerTypeChanges, _containerType, ref savedContainerType);
            RevertContainerChanges(_typeScopedContainerIndexChanges, _containerIndex, ref savedContainerIndex);
            RevertContainerChanges(_typeScopedContainerListChanges, _containerList, ref savedContainerList);
            RevertContainerChanges(_typeScopedContainerLangChanges, _containerLanguage, ref savedContainerLang);
            RevertContainerChanges(_typeScopedContainerGraphChanges, _containerGraph, ref savedContainerGraph);
            RevertContainerChanges(_typeScopedContainerIdChanges, _containerId, ref savedContainerId);
        }

        // Restore @vocab/@base to pre-type-scoped state
        if (!_typeScopedPropagate && _savedContextForNested != null)
        {
            _vocabIri = _savedVocabForNested;
            _baseIri = _savedBaseForNested;
        }

        // Save context state before applying the new type's scoped context
        // This ensures the type-scoped context doesn't leak to parent after we return
        Dictionary<string, string>? savedContextState = null;
        string? savedVocabBeforeType = null;
        string? savedBaseBeforeType = null;

        // Apply the new type's scoped context (if typeTerm has a scoped context)
        if (!string.IsNullOrEmpty(typeTerm) && _scopedContext.TryGetValue(typeTerm, out var scopedJson))
        {
            // Save state before applying type-scoped context
            savedContextState = new Dictionary<string, string>(_context);
            savedVocabBeforeType = _vocabIri;
            savedBaseBeforeType = _baseIri;

            using var scopedDoc = JsonDocument.Parse(scopedJson);
            ProcessContext(scopedDoc.RootElement);
        }

        // Process @context if present in the object itself
        if (root.TryGetProperty("@context", out var contextElement))
        {
            // Save state if not already saved
            if (savedContextState == null)
            {
                savedContextState = new Dictionary<string, string>(_context);
                savedVocabBeforeType = _vocabIri;
                savedBaseBeforeType = _baseIri;
            }
            ProcessContext(contextElement);
        }

        // Get @id for subject if present, otherwise generate blank node
        string nodeId;
        if (root.TryGetProperty("@id", out var idElement))
        {
            var objId = idElement.GetString();
            nodeId = string.IsNullOrEmpty(objId) ? GenerateBlankNode() : ExpandIri(objId);
        }
        else
        {
            nodeId = GenerateBlankNode();
        }

        // Emit @type triple if typeIri is provided (not @none)
        if (!string.IsNullOrEmpty(typeIri))
        {
            EmitQuad(handler, nodeId, RdfType, typeIri, _currentGraph);
        }

        // Process additional @type if present (in addition to the key-derived type)
        if (root.TryGetProperty("@type", out var typeElement))
        {
            ProcessType(nodeId, typeElement, handler, _currentGraph);
        }

        // Process properties
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            var propPredicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(propPredicate))
                continue;

            ProcessProperty(nodeId, propPredicate, propName, prop.Value, handler, _currentGraph);
        }

        // First, restore context state to before we applied this node's type-scoped/inline context
        // This prevents the nested node's context from leaking to the parent
        if (savedContextState != null)
        {
            _context.Clear();
            foreach (var kv in savedContextState)
            {
                _context[kv.Key] = kv.Value;
            }
            _vocabIri = savedVocabBeforeType;
            _baseIri = savedBaseBeforeType;
        }

        // Then restore parent's type-scoped changes (they apply to parent's remaining properties)
        if (savedCoercions != null)
        {
            foreach (var kv in savedCoercions)
            {
                _typeCoercion[kv.Key] = kv.Value;
            }
        }
        if (savedTerms != null)
        {
            foreach (var kv in savedTerms)
            {
                _context[kv.Key] = kv.Value;
            }
        }

        // Restore container values
        void RestoreContainer(Dictionary<string, bool>? saved, Dictionary<string, bool> container)
        {
            if (saved != null)
            {
                foreach (var kv in saved)
                {
                    container[kv.Key] = kv.Value;
                }
            }
        }
        RestoreContainer(savedContainerType, _containerType);
        RestoreContainer(savedContainerIndex, _containerIndex);
        RestoreContainer(savedContainerList, _containerList);
        RestoreContainer(savedContainerLang, _containerLanguage);
        RestoreContainer(savedContainerGraph, _containerGraph);
        RestoreContainer(savedContainerId, _containerId);

        _vocabIri = savedVocab;
        _baseIri = savedBase;

        return nodeId;
    }

    /// <summary>
    /// Parse a node with a pre-specified @id.
    /// Used for @id container maps where the key provides the @id.
    /// </summary>
    private string ParseNodeWithId(ref Utf8JsonReader reader, QuadHandler handler, string nodeId, string? parentNode)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return nodeId;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Process @context if present
        if (root.TryGetProperty("@context", out var contextElement))
        {
            ProcessContext(contextElement);
        }

        // Use provided nodeId unless object has its own @id
        var subject = nodeId;
        if (root.TryGetProperty("@id", out var idElement))
        {
            var objId = idElement.GetString();
            if (!string.IsNullOrEmpty(objId))
            {
                subject = ExpandIri(objId);
            }
        }

        // Process @type if present
        if (root.TryGetProperty("@type", out var typeElement))
        {
            ProcessType(subject, typeElement, handler, _currentGraph);
        }

        // Process properties
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            var predicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(predicate))
                continue;

            ProcessProperty(subject, predicate, propName, prop.Value, handler, _currentGraph);
        }

        return subject;
    }
}
