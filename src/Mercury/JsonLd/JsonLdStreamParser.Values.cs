// JsonLdStreamParser.Values.cs
// Value processing, literals, lists, number handling

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    private void ProcessValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var strVal = value.GetString() ?? "";
                if (coercedType == "@id")
                {
                    // IRI reference - compact IRIs and relative IRIs, NOT terms
                    // See test e056: "Use terms with @type: @vocab but not with @type: @id"
                    var iri = ExpandIri(strVal, expandTerms: false);
                    // Skip if IRI cannot be resolved (e.g., relative IRI with @base: null) (li14)
                    if (iri != null)
                        EmitQuad(handler, subject, predicate, iri, graphIri);
                }
                else if (coercedType == "@vocab")
                {
                    // Vocabulary IRI - first try as term, then use @vocab
                    var iri = ExpandIri(strVal, expandTerms: true);
                    // Skip if IRI cannot be resolved
                    if (iri != null)
                        EmitQuad(handler, subject, predicate, iri, graphIri);
                }
                else if (coercedType == "@json")
                {
                    // JSON literal - use canonical JSON representation (js17)
                    var canonicalJson = CanonicalizeJson(value);
                    EmitQuad(handler, subject, predicate,
                        $"\"{canonicalJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else if (!string.IsNullOrEmpty(coercedType))
                {
                    // Typed literal - terms like "dateTime" should be expanded using @vocab
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    var literal = $"\"{EscapeString(strVal)}\"^^{datatypeIri}";
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                else
                {
                    // Plain string literal - apply term language, then default language
                    // Term-level @language: null (empty string) means explicitly no language tag
                    string literal;
                    if (termLanguage != null)
                    {
                        // Term has explicit @language setting
                        if (termLanguage.Length > 0)
                        {
                            literal = $"\"{EscapeString(strVal)}\"@{termLanguage}";
                        }
                        else
                        {
                            // @language: null in term definition - no language tag
                            literal = $"\"{EscapeString(strVal)}\"";
                        }
                    }
                    else if (!string.IsNullOrEmpty(_defaultLanguage))
                    {
                        literal = $"\"{EscapeString(strVal)}\"@{_defaultLanguage}";
                    }
                    else
                    {
                        literal = $"\"{EscapeString(strVal)}\"";
                    }
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                break;

            case JsonValueKind.Number:
                ProcessNumberLiteral(subject, predicate, value, handler, graphIri, coercedType);
                break;

            case JsonValueKind.True:
                if (coercedType == "@json")
                {
                    // JSON literal for true
                    EmitQuad(handler, subject, predicate,
                        "\"true\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab")
                {
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    EmitQuad(handler, subject, predicate, $"\"true\"^^{datatypeIri}", graphIri);
                }
                else
                {
                    EmitQuad(handler, subject, predicate,
                        "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                }
                break;

            case JsonValueKind.False:
                if (coercedType == "@json")
                {
                    // JSON literal for false
                    EmitQuad(handler, subject, predicate,
                        "\"false\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab")
                {
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    EmitQuad(handler, subject, predicate, $"\"false\"^^{datatypeIri}", graphIri);
                }
                else
                {
                    EmitQuad(handler, subject, predicate,
                        "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                }
                break;

            case JsonValueKind.Object:
                // Handle @json type - serialize entire object as canonical JSON literal (js06, js07)
                if (coercedType == "@json")
                {
                    var canonicalJson = CanonicalizeJson(value);
                    EmitQuad(handler, subject, predicate,
                        $"\"{canonicalJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else
                {
                    ProcessObjectValue(subject, predicate, value, handler, graphIri, coercedType);
                }
                break;

            case JsonValueKind.Null:
                // null values are normally ignored in JSON-LD, but with @type: @json they are emitted (js18)
                if (coercedType == "@json")
                {
                    EmitQuad(handler, subject, predicate,
                        "\"null\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                // Otherwise null values are ignored
                break;
        }
    }

    private void ProcessObjectValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType = null)
    {
        // Check for value object (@value or alias)
        JsonElement valProp = default;
        bool hasValue = value.TryGetProperty("@value", out valProp);
        if (!hasValue)
        {
            // Check for @value aliases from type-scoped or property-scoped contexts
            foreach (var alias in _valueAliases)
            {
                if (value.TryGetProperty(alias, out valProp))
                {
                    hasValue = true;
                    break;
                }
            }
        }

        if (hasValue)
        {
            // Skip if @value is null, unless @type is @json (js22)
            if (valProp.ValueKind == JsonValueKind.Null)
            {
                // Check if @type is @json - if so, emit "null" as JSON literal
                JsonElement typeProp = default;
                bool hasJsonType = value.TryGetProperty("@type", out typeProp);
                if (!hasJsonType)
                {
                    foreach (var alias in _typeAliases)
                    {
                        if (value.TryGetProperty(alias, out typeProp))
                        {
                            hasJsonType = true;
                            break;
                        }
                    }
                }
                if (hasJsonType)
                {
                    var typeVal = typeProp.GetString() ?? "";
                    if (typeVal == "@json" || _jsonAliases.Contains(typeVal))
                    {
                        EmitQuad(handler, subject, predicate,
                            "\"null\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                    }
                }
                return;
            }

            // Check for compound-literal mode with @direction
            if (_rdfDirection == "compound-literal" && value.TryGetProperty("@direction", out var dirProp) &&
                dirProp.ValueKind == JsonValueKind.String)
            {
                var direction = dirProp.GetString() ?? "";
                var valueStr = valProp.ValueKind == JsonValueKind.String ? valProp.GetString() ?? "" : valProp.GetRawText();

                // Create blank node for compound literal
                var compoundNode = GenerateBlankNode();

                // Emit: subject --predicate--> compoundNode
                EmitQuad(handler, subject, predicate, compoundNode, graphIri);

                // Emit: compoundNode --rdf:value--> "value"
                EmitQuad(handler, compoundNode, RdfValue, $"\"{EscapeString(valueStr)}\"", graphIri);

                // Emit: compoundNode --rdf:direction--> "direction"
                EmitQuad(handler, compoundNode, RdfDirection, $"\"{direction}\"", graphIri);

                // Check for @language - if present, emit rdf:language (lowercase)
                if (value.TryGetProperty("@language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
                {
                    var lang = langProp.GetString() ?? "";
                    EmitQuad(handler, compoundNode, RdfLanguage, $"\"{lang.ToLowerInvariant()}\"", graphIri);
                }

                return;
            }

            var literal = ProcessValueObject(value, valProp);
            EmitQuad(handler, subject, predicate, literal, graphIri);
            return;
        }

        // Check for invalid value object with @language (or alias) but no @value - drop it
        // Note: @type without @value is valid - it's a node object with rdf:type, not a value object
        bool hasLanguage = value.TryGetProperty("@language", out _);
        if (!hasLanguage)
        {
            foreach (var alias in _languageAliases)
            {
                if (value.TryGetProperty(alias, out _))
                {
                    hasLanguage = true;
                    break;
                }
            }
        }
        if (hasLanguage)
        {
            // @language requires @value - this is invalid, drop the property
            return;
        }

        // Check for @id only (IRI reference)
        if (value.TryGetProperty("@id", out var idProp) && value.EnumerateObject().Count() == 1)
        {
            var idValue = idProp.GetString() ?? "";
            // Per JSON-LD 1.1: @id values that look like keywords (e.g., "@ignoreMe")
            // should be ignored and no triple should be emitted (e122)
            if (idValue.StartsWith('@') && IsKeywordLike(idValue))
            {
                return; // Ignore keyword-like @id value
            }
            var iri = ExpandIri(idValue);
            EmitQuad(handler, subject, predicate, iri, graphIri);
            return;
        }

        // Check for @graph (graph object)
        if (value.TryGetProperty("@graph", out var graphProp))
        {
            // Create a blank node for the named graph
            var graphNode = GenerateBlankNode();

            // Emit the relationship from subject to the graph node
            EmitQuad(handler, subject, predicate, graphNode, graphIri);

            // Save current graph and set to the new named graph
            var savedGraph = _currentGraph;
            _currentGraph = graphNode;
            try
            {
                // Process @graph contents into that named graph
                if (graphProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in graphProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            ProcessGraphNode(item, handler);
                        }
                    }
                }
                else if (graphProp.ValueKind == JsonValueKind.Object)
                {
                    ProcessGraphNode(graphProp, handler);
                }
            }
            finally
            {
                _currentGraph = savedGraph;
            }
            return;
        }

        // Check for @list
        if (value.TryGetProperty("@list", out var listProp))
        {
            // List objects cannot have @id or other invalid keys (er41)
            if (value.TryGetProperty("@id", out _))
            {
                throw new InvalidOperationException("invalid set or list object");
            }
            // Pass coercedType to list processing
            var listHead = ProcessList(listProp, handler, graphIri, coercedType);
            EmitQuad(handler, subject, predicate, listHead, graphIri);
            return;
        }

        // Check for @set - flatten the contents, ignore empty @set
        if (value.TryGetProperty("@set", out var setProp))
        {
            // @set just contains values to be flattened - process each one
            // Pass coercedType to each element
            if (setProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in setProp.EnumerateArray())
                {
                    // Skip null values
                    if (item.ValueKind == JsonValueKind.Null)
                        continue;
                    ProcessValue(subject, predicate, item, handler, graphIri, coercedType, null);
                }
            }
            else if (setProp.ValueKind != JsonValueKind.Null)
            {
                // Single value
                ProcessValue(subject, predicate, setProp, handler, graphIri, coercedType, null);
            }
            // Empty @set produces no output
            return;
        }

        // Nested object - create blank node
        // 1. Revert type-scoped changes (type-scoped contexts don't propagate by default)
        // 2. Revert property-scoped changes if @propagate: false (c027, c028)
        // 3. Save/restore @vocab/@base around nested node (in case nested has inline @context)
        Dictionary<string, string>? savedCoercions = null;
        Dictionary<string, string>? savedTerms = null;
        Dictionary<string, string>? savedPropCoercions = null;
        Dictionary<string, string>? savedPropTerms = null;
        // Only revert type-scoped changes if @propagate is NOT true
        // @propagate: true means type-scoped context SHOULD propagate to nested nodes
        if (!_typeScopedPropagate && _typeScopedCoercionChanges != null && _typeScopedCoercionChanges.Count > 0)
        {
            // Revert type coercions to their original values (or remove if they were new)
            savedCoercions = new Dictionary<string, string>();
            foreach (var kv in _typeScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedCoercions[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    // Term was new - remove it
                    _typeCoercion.Remove(kv.Key);
                }
                else
                {
                    // Term was modified - restore original
                    _typeCoercion[kv.Key] = kv.Value;
                }
            }
        }

        // Revert type-scoped term changes
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
                    // Term was new - remove it
                    _context.Remove(kv.Key);
                }
                else
                {
                    // Term was modified - restore original
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
                        // Container was new - remove it
                        container.Remove(kv.Key);
                    }
                    else
                    {
                        // Container was modified or removed - restore original
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

        // Revert property-scoped changes if @propagate: false (c027, c028)
        // Property-scoped contexts propagate by default, but @propagate: false stops propagation
        Dictionary<string, bool>? savedPropContainerType = null;
        Dictionary<string, bool>? savedPropContainerIndex = null;
        Dictionary<string, bool>? savedPropContainerList = null;
        Dictionary<string, bool>? savedPropContainerLang = null;
        Dictionary<string, bool>? savedPropContainerGraph = null;
        Dictionary<string, bool>? savedPropContainerId = null;

        if (_propScopedNoPropagate && _propScopedCoercionChanges != null && _propScopedCoercionChanges.Count > 0)
        {
            savedPropCoercions = new Dictionary<string, string>();
            foreach (var kv in _propScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedPropCoercions[kv.Key] = currentValue;
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

        if (_propScopedNoPropagate && _propScopedTermChanges != null && _propScopedTermChanges.Count > 0)
        {
            savedPropTerms = new Dictionary<string, string>();
            foreach (var kv in _propScopedTermChanges)
            {
                if (_context.TryGetValue(kv.Key, out var currentValue))
                {
                    savedPropTerms[kv.Key] = currentValue;
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

        if (_propScopedNoPropagate)
        {
            RevertContainerChanges(_propScopedContainerTypeChanges, _containerType, ref savedPropContainerType);
            RevertContainerChanges(_propScopedContainerIndexChanges, _containerIndex, ref savedPropContainerIndex);
            RevertContainerChanges(_propScopedContainerListChanges, _containerList, ref savedPropContainerList);
            RevertContainerChanges(_propScopedContainerLangChanges, _containerLanguage, ref savedPropContainerLang);
            RevertContainerChanges(_propScopedContainerGraphChanges, _containerGraph, ref savedPropContainerGraph);
            RevertContainerChanges(_propScopedContainerIdChanges, _containerId, ref savedPropContainerId);
        }

        // Revert inline context changes if @propagate: false (c028)
        // Inline contexts propagate by default, but @propagate: false stops propagation
        Dictionary<string, string>? savedInlineCoercions = null;
        Dictionary<string, string>? savedInlineTerms = null;
        Dictionary<string, bool>? savedInlineContainerType = null;
        Dictionary<string, bool>? savedInlineContainerIndex = null;
        Dictionary<string, bool>? savedInlineContainerList = null;
        Dictionary<string, bool>? savedInlineContainerLang = null;
        Dictionary<string, bool>? savedInlineContainerGraph = null;
        Dictionary<string, bool>? savedInlineContainerId = null;

        if (_inlineScopedNoPropagate && _inlineScopedCoercionChanges != null && _inlineScopedCoercionChanges.Count > 0)
        {
            savedInlineCoercions = new Dictionary<string, string>();
            foreach (var kv in _inlineScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedInlineCoercions[kv.Key] = currentValue;
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

        if (_inlineScopedNoPropagate && _inlineScopedTermChanges != null && _inlineScopedTermChanges.Count > 0)
        {
            savedInlineTerms = new Dictionary<string, string>();
            foreach (var kv in _inlineScopedTermChanges)
            {
                if (_context.TryGetValue(kv.Key, out var currentValue))
                {
                    savedInlineTerms[kv.Key] = currentValue;
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

        if (_inlineScopedNoPropagate)
        {
            RevertContainerChanges(_inlineScopedContainerTypeChanges, _containerType, ref savedInlineContainerType);
            RevertContainerChanges(_inlineScopedContainerIndexChanges, _containerIndex, ref savedInlineContainerIndex);
            RevertContainerChanges(_inlineScopedContainerListChanges, _containerList, ref savedInlineContainerList);
            RevertContainerChanges(_inlineScopedContainerLangChanges, _containerLanguage, ref savedInlineContainerLang);
            RevertContainerChanges(_inlineScopedContainerGraphChanges, _containerGraph, ref savedInlineContainerGraph);
            RevertContainerChanges(_inlineScopedContainerIdChanges, _containerId, ref savedInlineContainerId);
        }

        // Save full context state before processing nested node
        // Nested object's inline @context should NOT affect siblings
        var savedVocab = _vocabIri;
        var savedBase = _baseIri;
        var savedContextForNested = new Dictionary<string, string>(_context);
        var savedTypeCoercionForNested = new Dictionary<string, string>(_typeCoercion);
        var savedContainerListForNested = new Dictionary<string, bool>(_containerList);
        var savedContainerLangForNested = new Dictionary<string, bool>(_containerLanguage);
        var savedContainerIndexForNested = new Dictionary<string, bool>(_containerIndex);
        var savedContainerGraphForNested = new Dictionary<string, bool>(_containerGraph);
        var savedContainerIdForNested = new Dictionary<string, bool>(_containerId);
        var savedContainerTypeForNested = new Dictionary<string, bool>(_containerType);

        // If type-scoped context changed @vocab/@base, restore to pre-type-scoped state for nested
        // (unless @propagate is true)
        if (!_typeScopedPropagate && _savedContextForNested != null)
        {
            _vocabIri = _savedVocabForNested;
            _baseIri = _savedBaseForNested;
        }

        // Save and temporarily remove type-scoped protected terms for nested node (pr22)
        // Type-scoped protected terms should NOT prevent redefinition in nested nodes
        var savedProtectedTerms = new HashSet<string>(_protectedTerms, StringComparer.Ordinal);
        var savedTypeScopedProtectedTerms = new HashSet<string>(_typeScopedProtectedTerms, StringComparer.Ordinal);
        foreach (var term in _typeScopedProtectedTerms)
        {
            _protectedTerms.Remove(term);
        }
        _typeScopedProtectedTerms.Clear();

        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
        tempReader.Read();
        var blankNode = ParseNode(ref tempReader, handler, subject);
        // Skip emitting link if node was dropped (e.g., @id unresolvable with null @base) (e060)
        if (blankNode != null)
            EmitQuad(handler, subject, predicate, blankNode, graphIri);

        // Restore full context state after processing nested node
        // Nested object's inline @context should NOT affect siblings
        _context.Clear();
        foreach (var kv in savedContextForNested)
            _context[kv.Key] = kv.Value;

        _typeCoercion.Clear();
        foreach (var kv in savedTypeCoercionForNested)
            _typeCoercion[kv.Key] = kv.Value;

        _containerList.Clear();
        foreach (var kv in savedContainerListForNested)
            _containerList[kv.Key] = kv.Value;

        _containerLanguage.Clear();
        foreach (var kv in savedContainerLangForNested)
            _containerLanguage[kv.Key] = kv.Value;

        _containerIndex.Clear();
        foreach (var kv in savedContainerIndexForNested)
            _containerIndex[kv.Key] = kv.Value;

        _containerGraph.Clear();
        foreach (var kv in savedContainerGraphForNested)
            _containerGraph[kv.Key] = kv.Value;

        _containerId.Clear();
        foreach (var kv in savedContainerIdForNested)
            _containerId[kv.Key] = kv.Value;

        _containerType.Clear();
        foreach (var kv in savedContainerTypeForNested)
            _containerType[kv.Key] = kv.Value;

        _vocabIri = savedVocab;
        _baseIri = savedBase;

        // Re-apply type-scoped changes (they apply to this node's remaining properties)
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

        // Restore type-scoped container values
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

        // Restore protected terms after nested node (pr22)
        _protectedTerms.Clear();
        foreach (var term in savedProtectedTerms)
            _protectedTerms.Add(term);
        _typeScopedProtectedTerms.Clear();
        foreach (var term in savedTypeScopedProtectedTerms)
            _typeScopedProtectedTerms.Add(term);

        // Re-apply property-scoped changes (they apply to this node's remaining properties) (c027, c028)
        if (savedPropCoercions != null)
        {
            foreach (var kv in savedPropCoercions)
            {
                _typeCoercion[kv.Key] = kv.Value;
            }
        }
        if (savedPropTerms != null)
        {
            foreach (var kv in savedPropTerms)
            {
                _context[kv.Key] = kv.Value;
            }
        }
        RestoreContainer(savedPropContainerType, _containerType);
        RestoreContainer(savedPropContainerIndex, _containerIndex);
        RestoreContainer(savedPropContainerList, _containerList);
        RestoreContainer(savedPropContainerLang, _containerLanguage);
        RestoreContainer(savedPropContainerGraph, _containerGraph);
        RestoreContainer(savedPropContainerId, _containerId);

        // Re-apply inline context changes (they apply to this node's remaining properties) (c028)
        if (savedInlineCoercions != null)
        {
            foreach (var kv in savedInlineCoercions)
            {
                _typeCoercion[kv.Key] = kv.Value;
            }
        }
        if (savedInlineTerms != null)
        {
            foreach (var kv in savedInlineTerms)
            {
                _context[kv.Key] = kv.Value;
            }
        }
        RestoreContainer(savedInlineContainerType, _containerType);
        RestoreContainer(savedInlineContainerIndex, _containerIndex);
        RestoreContainer(savedInlineContainerList, _containerList);
        RestoreContainer(savedInlineContainerLang, _containerLanguage);
        RestoreContainer(savedInlineContainerGraph, _containerGraph);
        RestoreContainer(savedInlineContainerId, _containerId);
    }

    private string ProcessValueObject(JsonElement obj, JsonElement valueProp)
    {
        // Value objects cannot contain @id (er37)
        if (obj.TryGetProperty("@id", out _))
        {
            throw new InvalidOperationException("invalid value object");
        }

        // Value objects cannot have both @type and @language (er38)
        bool hasTypeForConflict = obj.TryGetProperty("@type", out _);
        if (!hasTypeForConflict)
        {
            foreach (var alias in _typeAliases)
            {
                if (obj.TryGetProperty(alias, out _))
                {
                    hasTypeForConflict = true;
                    break;
                }
            }
        }
        bool hasLangForConflict = obj.TryGetProperty("@language", out _);
        if (!hasLangForConflict)
        {
            foreach (var alias in _languageAliases)
            {
                if (obj.TryGetProperty(alias, out _))
                {
                    hasLangForConflict = true;
                    break;
                }
            }
        }
        if (hasTypeForConflict && hasLangForConflict)
        {
            throw new InvalidOperationException("invalid value object");
        }

        // Early check for @json type - allows object/array values (js23, js16)
        // Must check before the switch that throws for object/array
        JsonElement earlyTypeProp = default;
        bool hasEarlyType = obj.TryGetProperty("@type", out earlyTypeProp);
        if (!hasEarlyType)
        {
            foreach (var alias in _typeAliases)
            {
                if (obj.TryGetProperty(alias, out earlyTypeProp))
                {
                    hasEarlyType = true;
                    break;
                }
            }
        }
        if (hasEarlyType && earlyTypeProp.ValueKind == JsonValueKind.String)
        {
            var earlyTypeStr = earlyTypeProp.GetString() ?? "";
            if (earlyTypeStr == "@json" || _jsonAliases.Contains(earlyTypeStr))
            {
                var canonicalJson = CanonicalizeJson(valueProp);
                return $"\"{canonicalJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>";
            }
        }

        string valueStr;
        string? inferredType = null;  // Inferred XSD type for native JSON values

        switch (valueProp.ValueKind)
        {
            case JsonValueKind.String:
                valueStr = valueProp.GetString() ?? "";
                break;
            case JsonValueKind.Number:
                var rawText = valueProp.GetRawText();
                valueStr = rawText;
                // Infer XSD type for native JSON numbers
                var isDouble = rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E');
                inferredType = isDouble
                    ? "<http://www.w3.org/2001/XMLSchema#double>"
                    : "<http://www.w3.org/2001/XMLSchema#integer>";
                break;
            case JsonValueKind.True:
                valueStr = "true";
                inferredType = "<http://www.w3.org/2001/XMLSchema#boolean>";
                break;
            case JsonValueKind.False:
                valueStr = "false";
                inferredType = "<http://www.w3.org/2001/XMLSchema#boolean>";
                break;
            case JsonValueKind.Null:
                // Null @value is allowed in some contexts (handled elsewhere)
                valueStr = "";
                break;
            default:
                // @value must be a scalar (string, number, boolean, null) - not array or object (er29)
                throw new InvalidOperationException("invalid value object value");
        }

        // Check for @language (or alias)
        JsonElement langProp = default;
        bool hasLanguage = obj.TryGetProperty("@language", out langProp);
        if (!hasLanguage)
        {
            foreach (var alias in _languageAliases)
            {
                if (obj.TryGetProperty(alias, out langProp))
                {
                    hasLanguage = true;
                    break;
                }
            }
        }
        // Check for @index - if present, must be a string (er31)
        // This must be checked early as @language handling returns immediately
        if (obj.TryGetProperty("@index", out var indexProp))
        {
            if (indexProp.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("invalid @index value");
            }
            // @index is valid but doesn't affect RDF output - it's just metadata
        }

        // Check for @direction
        string? direction = null;
        if (obj.TryGetProperty("@direction", out var dirProp))
        {
            if (dirProp.ValueKind == JsonValueKind.String)
            {
                direction = dirProp.GetString();
            }
        }

        if (hasLanguage)
        {
            // @language must be a string (er30)
            if (langProp.ValueKind != JsonValueKind.String && langProp.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidOperationException("invalid language-tagged string");
            }
            // When @language is present, @value must be a string (er39)
            if (valueProp.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("invalid language-tagged value");
            }
            var lang = langProp.GetString() ?? "";

            // Handle direction with language
            if (!string.IsNullOrEmpty(direction) && !string.IsNullOrEmpty(_rdfDirection))
            {
                if (_rdfDirection == "i18n-datatype")
                {
                    // Format: "value"^^<https://www.w3.org/ns/i18n#{language}_{direction}>
                    var datatype = $"<{I18nNamespace}{lang.ToLowerInvariant()}_{direction}>";
                    return $"\"{EscapeString(valueStr)}\"^^{datatype}";
                }
                // compound-literal mode is handled in ProcessObjectValueWithDirection
            }

            return $"\"{EscapeString(valueStr)}\"@{lang}";
        }

        // Handle direction without language (i18n-datatype mode only)
        if (!string.IsNullOrEmpty(direction) && _rdfDirection == "i18n-datatype")
        {
            // Format: "value"^^<https://www.w3.org/ns/i18n#_{direction}>
            var datatype = $"<{I18nNamespace}_{direction}>";
            return $"\"{EscapeString(valueStr)}\"^^{datatype}";
        }

        // Check for @type (datatype, or alias) - terms should be expanded for datatypes
        JsonElement typeProp = default;
        bool hasType = obj.TryGetProperty("@type", out typeProp);
        if (!hasType)
        {
            foreach (var alias in _typeAliases)
            {
                if (obj.TryGetProperty(alias, out typeProp))
                {
                    hasType = true;
                    break;
                }
            }
        }
        if (hasType)
        {
            var typeStr = typeProp.GetString() ?? "";
            // Datatype cannot be a blank node (er40)
            if (typeStr.StartsWith("_:"))
            {
                throw new InvalidOperationException("invalid typed value");
            }
            // Handle @json type - use canonical JSON representation (js23, js16)
            // Check for @json or aliases of @json
            if (typeStr == "@json" || _jsonAliases.Contains(typeStr))
            {
                var canonicalJson = CanonicalizeJson(valueProp);
                return $"\"{canonicalJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>";
            }
            var datatype = ExpandIri(typeStr, expandTerms: true);
            // Validate that the datatype IRI is well-formed (e123)
            // Strip angle brackets for validation, then check for invalid characters
            var iriToValidate = datatype;
            if (iriToValidate.StartsWith('<') && iriToValidate.EndsWith('>'))
            {
                iriToValidate = iriToValidate.Substring(1, iriToValidate.Length - 2);
            }
            // IRIs cannot contain spaces or other invalid characters
            if (iriToValidate.Contains(' ') || iriToValidate.Contains('<') || iriToValidate.Contains('>') ||
                iriToValidate.Contains('"') || iriToValidate.Contains('{') || iriToValidate.Contains('}') ||
                iriToValidate.Contains('|') || iriToValidate.Contains('^') || iriToValidate.Contains('`'))
            {
                throw new InvalidOperationException("invalid typed value");
            }
            return $"\"{EscapeString(valueStr)}\"^^{datatype}";
        }

        // Use inferred type for native JSON values (numbers, booleans)
        if (inferredType != null)
        {
            return $"\"{EscapeString(valueStr)}\"^^{inferredType}";
        }

        // Plain literal (strings without type)
        return $"\"{EscapeString(valueStr)}\"";
    }

    private void ProcessNumberLiteral(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        var rawText = value.GetRawText();
        var hasDecimalOrExp = rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E');

        // JSON-LD spec: a number that can be exactly represented as an integer should be xsd:integer (tn02)
        // But numbers >= 1e21 must be xsd:double even without fractions (rt01)
        // Parse the value to check if it's an exact integer even if written as a double (e.g., 10.0)
        var isDouble = hasDecimalOrExp;
        if (hasDecimalOrExp && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var checkValue))
        {
            // If the value has no fractional part and is within integer range, treat it as an integer
            // Numbers >= 1e21 cannot be exactly represented as integers and must use xsd:double
            if (Math.Floor(checkValue) == checkValue && !double.IsInfinity(checkValue) && !double.IsNaN(checkValue)
                && Math.Abs(checkValue) < 1e21)
            {
                isDouble = false;
            }
        }

        // Handle @json type - use canonical JSON representation (js04)
        if (coercedType == "@json")
        {
            // Canonicalize number: integers as integers, doubles as shortest form
            string canonicalNum;
            if (value.TryGetInt64(out var jsonIntVal))
            {
                canonicalNum = jsonIntVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var jsonDblVal))
            {
                // If it can be exactly represented as an integer, use integer form
                if (Math.Floor(jsonDblVal) == jsonDblVal && !double.IsInfinity(jsonDblVal) && !double.IsNaN(jsonDblVal) &&
                    jsonDblVal >= long.MinValue && jsonDblVal <= long.MaxValue)
                {
                    canonicalNum = ((long)jsonDblVal).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    canonicalNum = jsonDblVal.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                canonicalNum = rawText;
            }
            EmitQuad(handler, subject, predicate,
                $"\"{canonicalNum}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
            return;
        }

        // Check if custom datatype is specified (not @id, @vocab, or standard xsd types)
        if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab" &&
            !coercedType.Contains("XMLSchema#double") && !coercedType.Contains("XMLSchema#integer"))
        {
            // Custom datatype - use canonical double form for non-integers
            var customDatatype = ExpandIri(coercedType, expandTerms: true);
            string customLexical;
            if (isDouble && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var customDoubleValue))
            {
                var canonicalForm = customDoubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                customLexical = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                customLexical = rawText;
            }
            EmitQuad(handler, subject, predicate, $"\"{customLexical}\"^^{customDatatype}", graphIri);
            return;
        }

        // Check if type coercion specifies xsd:double
        var forceDouble = coercedType != null &&
            (coercedType == "http://www.w3.org/2001/XMLSchema#double" ||
             coercedType.EndsWith("#double"));

        // Check if type coercion specifies xsd:integer
        var forceInteger = coercedType != null &&
            (coercedType == "http://www.w3.org/2001/XMLSchema#integer" ||
             coercedType.EndsWith("#integer"));

        // Determine effective datatype
        string datatypeIri;
        string lexicalValue;

        if (forceDouble || (isDouble && !forceInteger))
        {
            // Format as double - must use canonical XSD form with exponent notation
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#double>";
            if (double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                var canonicalForm = doubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                lexicalValue = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                lexicalValue = rawText;
            }
        }
        else if (forceInteger)
        {
            // Type coercion to integer - if value has decimal, format as double lexically but with integer type
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#integer>";
            if (isDouble && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                // Format non-integer value as double canonical form but typed as integer
                var canonicalForm = doubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                lexicalValue = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                lexicalValue = rawText;
            }
        }
        else
        {
            // Default: integer
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#integer>";
            // If rawText has decimal point (like "10.0"), convert to integer form (tn02)
            if (hasDecimalOrExp && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var intLikeValue))
            {
                lexicalValue = ((long)intLikeValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                lexicalValue = rawText;
            }
        }

        EmitQuad(handler, subject, predicate, $"\"{lexicalValue}\"^^{datatypeIri}", graphIri);
    }

    /// <summary>
    /// Normalize a double to canonical XSD form.
    /// E.g., "5.300000000000000E+000" -> "5.3E0"
    /// </summary>
    private static string NormalizeDoubleCanonical(string formatted)
    {
        // Split into mantissa and exponent
        var eIndex = formatted.IndexOf('E');
        if (eIndex < 0) return formatted;

        var mantissa = formatted[..eIndex];
        var exponent = formatted[(eIndex + 1)..];

        // Trim trailing zeros from mantissa (but keep at least one digit after decimal)
        if (mantissa.Contains('.'))
        {
            mantissa = mantissa.TrimEnd('0');
            if (mantissa.EndsWith('.'))
                mantissa += '0'; // Keep at least "X.0"
        }

        // Normalize exponent: remove leading zeros and + sign
        // E.g., "+000" -> "0", "-002" -> "-2"
        if (int.TryParse(exponent, out var expValue))
        {
            exponent = expValue.ToString();
        }

        return mantissa + "E" + exponent;
    }

    private string ProcessList(JsonElement listElement, QuadHandler handler, string? graphIri, string? coercedType)
    {
        // Handle non-array values by treating them as single-item arrays
        if (listElement.ValueKind != JsonValueKind.Array)
        {
            // Null or @value:null produces empty list
            if (listElement.ValueKind == JsonValueKind.Null)
                return RdfNil;
            if (listElement.ValueKind == JsonValueKind.Object &&
                listElement.TryGetProperty("@value", out var valProp) &&
                valProp.ValueKind == JsonValueKind.Null)
                return RdfNil;

            // Single non-null value - wrap in list
            var singleNode = GenerateBlankNode();
            ProcessValue(singleNode, RdfFirst, listElement, handler, graphIri, coercedType);
            EmitQuad(handler, singleNode, RdfRest, RdfNil, graphIri);
            return singleNode;
        }

        // Empty array
        if (listElement.GetArrayLength() == 0)
        {
            return RdfNil;
        }

        // Filter out null values and @value:null objects
        var validItems = new List<JsonElement>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
                continue;
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("@value", out var vp) &&
                vp.ValueKind == JsonValueKind.Null)
                continue;
            validItems.Add(item);
        }

        // All items were null
        if (validItems.Count == 0)
        {
            return RdfNil;
        }

        string? firstNode = null;
        string? previousNode = null;

        foreach (var item in validItems)
        {
            var currentNode = GenerateBlankNode();

            if (firstNode == null)
            {
                firstNode = currentNode;
            }

            if (previousNode != null)
            {
                EmitQuad(handler, previousNode, RdfRest, currentNode, graphIri);
            }

            // Process list item - nested arrays become sublists (li05)
            // Note: In JSON-LD 1.1, @list containing @list is ALLOWED (li01-li04)
            // JSON-LD 1.0 rejects lists of lists (er24, er32)
            if (item.ValueKind == JsonValueKind.Array)
            {
                // List of lists is invalid in 1.0 mode (er24)
                if (_processingMode == "json-ld-1.0")
                {
                    throw new InvalidOperationException("list of lists");
                }
                // Nested array - recursively create a sublist
                var sublistHead = ProcessList(item, handler, graphIri, coercedType);
                EmitQuad(handler, currentNode, RdfFirst, sublistHead, graphIri);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("@list", out _))
            {
                // List of lists is invalid in 1.0 mode (er32)
                if (_processingMode == "json-ld-1.0")
                {
                    throw new InvalidOperationException("list of lists");
                }
                // Process the @list object (will call ProcessList recursively)
                ProcessValue(currentNode, RdfFirst, item, handler, graphIri, coercedType);
            }
            else
            {
                ProcessValue(currentNode, RdfFirst, item, handler, graphIri, coercedType);
            }

            previousNode = currentNode;
        }

        // Close list with rdf:nil
        if (previousNode != null)
        {
            EmitQuad(handler, previousNode, RdfRest, RdfNil, graphIri);
        }

        return firstNode ?? RdfNil;
    }
}
