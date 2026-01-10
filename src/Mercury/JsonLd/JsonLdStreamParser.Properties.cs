// JsonLdStreamParser.Properties.cs
// Property processing, reverse properties, nest keyword

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    private void ProcessProperty(string subject, string predicate, string term, JsonElement value,
        QuadHandler handler, string? graphIri)
    {
        // Check for type coercion and term-level language
        _typeCoercion.TryGetValue(term, out var coercedType);
        _termLanguage.TryGetValue(term, out var termLang);
        var isListContainer = _containerList.TryGetValue(term, out var isList) && isList;
        var isLanguageContainer = _containerLanguage.TryGetValue(term, out var isLang) && isLang;
        var isIndexContainer = _containerIndex.TryGetValue(term, out var isIdx) && isIdx;
        var isGraphContainer = _containerGraph.TryGetValue(term, out var isGraph) && isGraph;
        var isIdContainer = _containerId.TryGetValue(term, out var isId) && isId;
        var isTypeContainer = _containerType.TryGetValue(term, out var isType) && isType;
        // Check if this is a reverse property
        if (_reverseProperty.TryGetValue(term, out var reversePredicate))
        {
            // For reverse properties, values become subjects and the current node becomes object
            var expandedReversePredicate = ExpandTermValue(reversePredicate);

            // Handle index container with reverse property
            if (isIndexContainer && value.ValueKind == JsonValueKind.Object)
            {
                // Value is an index map - iterate over values (keys are ignored)
                foreach (var prop in value.EnumerateObject())
                {
                    ProcessReverseProperty(subject, expandedReversePredicate, prop.Value, handler, graphIri, coercedType);
                }
            }
            else
            {
                ProcessReverseProperty(subject, expandedReversePredicate, value, handler, graphIri, coercedType);
            }
            return;
        }

        // Apply scoped context if defined for this term
        Dictionary<string, string>? savedContext = null;
        Dictionary<string, string>? savedTypeCoercion = null;
        Dictionary<string, bool>? savedContainerList = null;
        Dictionary<string, bool>? savedContainerLanguage = null;
        Dictionary<string, bool>? savedContainerIndex = null;
        Dictionary<string, bool>? savedContainerGraph = null;
        Dictionary<string, bool>? savedContainerId = null;
        Dictionary<string, bool>? savedContainerType = null;
        Dictionary<string, string>? savedTermLanguage = null;
        Dictionary<string, string>? savedReverseProperty = null;
        Dictionary<string, string>? savedScopedContext = null;
        HashSet<string>? savedTypeAliases = null;
        HashSet<string>? savedIdAliases = null;
        HashSet<string>? savedGraphAliases = null;
        HashSet<string>? savedIncludedAliases = null;
        HashSet<string>? savedNestAliases = null;
        HashSet<string>? savedNoneAliases = null;
        HashSet<string>? savedValueAliases = null;
        HashSet<string>? savedLanguageAliases = null;
        HashSet<string>? savedJsonAliases = null;
        HashSet<string>? savedNullTerms = null;
        HashSet<string>? savedPrefixable = null;
        HashSet<string>? savedProtectedTerms = null;
        string? savedVocabIri = null;
        string? savedBaseIri = null;
        string? savedDefaultLanguage = null;
        if (_scopedContext.TryGetValue(term, out var scopedContextJson))
        {
            // Save current context state (all fields that ProcessContext can modify)
            savedContext = new Dictionary<string, string>(_context);
            savedTypeCoercion = new Dictionary<string, string>(_typeCoercion);
            savedContainerList = new Dictionary<string, bool>(_containerList);
            savedContainerLanguage = new Dictionary<string, bool>(_containerLanguage);
            savedContainerIndex = new Dictionary<string, bool>(_containerIndex);
            savedContainerGraph = new Dictionary<string, bool>(_containerGraph);
            savedContainerId = new Dictionary<string, bool>(_containerId);
            savedContainerType = new Dictionary<string, bool>(_containerType);
            savedTermLanguage = new Dictionary<string, string>(_termLanguage);
            savedReverseProperty = new Dictionary<string, string>(_reverseProperty);
            savedScopedContext = new Dictionary<string, string>(_scopedContext);
            savedTypeAliases = new HashSet<string>(_typeAliases);
            savedIdAliases = new HashSet<string>(_idAliases);
            savedGraphAliases = new HashSet<string>(_graphAliases);
            savedIncludedAliases = new HashSet<string>(_includedAliases);
            savedNestAliases = new HashSet<string>(_nestAliases);
            savedNoneAliases = new HashSet<string>(_noneAliases);
            savedValueAliases = new HashSet<string>(_valueAliases);
            savedLanguageAliases = new HashSet<string>(_languageAliases);
            savedJsonAliases = new HashSet<string>(_jsonAliases);
            savedNullTerms = new HashSet<string>(_nullTerms);
            savedPrefixable = new HashSet<string>(_prefixable);
            savedProtectedTerms = new HashSet<string>(_protectedTerms);
            savedVocabIri = _vocabIri;
            savedBaseIri = _baseIri;
            savedDefaultLanguage = _defaultLanguage;

            // Clear protected terms before applying scoped context
            // Property-scoped @context: null is allowed to clear protected terms
            // (protection is restored after property processing)
            _protectedTerms.Clear();

            // Check if the scoped context has @propagate: false (c027, c028)
            // If so, we need to track changes to revert for nested objects
            using var scopedDoc = JsonDocument.Parse(scopedContextJson);
            bool hasPropagateFalse = false;
            if (scopedDoc.RootElement.ValueKind == JsonValueKind.Object &&
                scopedDoc.RootElement.TryGetProperty("@propagate", out var propagateProp) &&
                propagateProp.ValueKind == JsonValueKind.False)
            {
                hasPropagateFalse = true;
            }

            if (hasPropagateFalse)
            {
                // Snapshot current state before applying context
                // We'll compute the diff after to track changes
                _propScopedNoPropagate = true;
                _propScopedTermChanges = new Dictionary<string, string?>(StringComparer.Ordinal);
                _propScopedCoercionChanges = new Dictionary<string, string?>(StringComparer.Ordinal);
                _propScopedContainerTypeChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);
                _propScopedContainerIndexChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);
                _propScopedContainerListChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);
                _propScopedContainerLangChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);
                _propScopedContainerGraphChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);
                _propScopedContainerIdChanges = new Dictionary<string, bool?>(StringComparer.Ordinal);

                // Snapshot before state for computing diffs
                var termsBefore = new Dictionary<string, string>(_context);
                var coercionBefore = new Dictionary<string, string>(_typeCoercion);
                var containerTypeBefore = new Dictionary<string, bool>(_containerType);
                var containerIndexBefore = new Dictionary<string, bool>(_containerIndex);
                var containerListBefore = new Dictionary<string, bool>(_containerList);
                var containerLangBefore = new Dictionary<string, bool>(_containerLanguage);
                var containerGraphBefore = new Dictionary<string, bool>(_containerGraph);
                var containerIdBefore = new Dictionary<string, bool>(_containerId);

                // Apply the scoped context
                ProcessContext(scopedDoc.RootElement);

                // Compute diffs - track what changed
                foreach (var kv in _context)
                {
                    if (!termsBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedTermChanges[kv.Key] = termsBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _typeCoercion)
                {
                    if (!coercionBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedCoercionChanges[kv.Key] = coercionBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerType)
                {
                    if (!containerTypeBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerTypeChanges[kv.Key] = containerTypeBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerIndex)
                {
                    if (!containerIndexBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerIndexChanges[kv.Key] = containerIndexBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerList)
                {
                    if (!containerListBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerListChanges[kv.Key] = containerListBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerLanguage)
                {
                    if (!containerLangBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerLangChanges[kv.Key] = containerLangBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerGraph)
                {
                    if (!containerGraphBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerGraphChanges[kv.Key] = containerGraphBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
                foreach (var kv in _containerId)
                {
                    if (!containerIdBefore.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value)
                    {
                        _propScopedContainerIdChanges[kv.Key] = containerIdBefore.TryGetValue(kv.Key, out var orig) ? orig : null;
                    }
                }
            }
            else
            {
                // Apply the scoped context without tracking
                ProcessContext(scopedDoc.RootElement);
            }
        }

        try
        {
            // Handle graph container - values are graph objects
            // When combined with @index, object keys are ignored (just indexes)
            // When combined with @id, object keys are used as graph @id
            if (isGraphContainer)
            {
                // Check for property-valued @index (pi11)
                _indexProperty.TryGetValue(term, out var graphIndexProp);
                string? graphIndexPropIri = null;
                string? graphIndexPropCoercedType = null;
                if (!string.IsNullOrEmpty(graphIndexProp))
                {
                    graphIndexPropIri = ExpandTerm(graphIndexProp);
                    _typeCoercion.TryGetValue(graphIndexProp, out graphIndexPropCoercedType);
                }
                ProcessGraphContainer(subject, predicate, value, handler, graphIri, coercedType, termLang, isIndexContainer, isIdContainer, graphIndexPropIri, graphIndexPropCoercedType);
            }
            // Handle language container - object keys are language tags
            else if (isLanguageContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessLanguageMap(subject, predicate, value, handler, graphIri);
            }
            // Handle index container - object keys are index values
            // For property-valued @index, keys become property values on the object (pi06-pi11)
            else if (isIndexContainer && value.ValueKind == JsonValueKind.Object)
            {
                // Check for property-valued @index
                _indexProperty.TryGetValue(term, out var indexPropName);
                string? indexPropIri = null;
                string? indexPropCoercedType = null;
                if (!string.IsNullOrEmpty(indexPropName))
                {
                    indexPropIri = ExpandTerm(indexPropName);
                    _typeCoercion.TryGetValue(indexPropName, out indexPropCoercedType);
                }

                foreach (var prop in value.EnumerateObject())
                {
                    var indexKey = prop.Name;
                    // @none or alias means no @index property emission (pi10)
                    var isNone = indexKey == "@none" || _noneAliases.Contains(indexKey);

                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            ProcessValueWithIndex(subject, predicate, item, handler, graphIri, coercedType, termLang,
                                indexPropIri, indexKey, indexPropCoercedType, isNone);
                        }
                    }
                    else
                    {
                        ProcessValueWithIndex(subject, predicate, prop.Value, handler, graphIri, coercedType, termLang,
                            indexPropIri, indexKey, indexPropCoercedType, isNone);
                    }
                }
            }
            // Handle @id container - object keys are @id values for nested objects
            else if (isIdContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessIdMap(subject, predicate, value, handler, graphIri);
            }
            // Handle @type container - object keys are @type values for nested objects
            else if (isTypeContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessTypeMap(subject, predicate, value, handler, graphIri, coercedType);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                // Handle @json type - serialize entire array as canonical JSON literal (js06, js07)
                if (coercedType == "@json")
                {
                    var canonicalJson = CanonicalizeJson(value);
                    EmitQuad(handler, subject, predicate,
                        $"\"{canonicalJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else if (isListContainer)
                {
                    // Create RDF list
                    var listHead = ProcessList(value, handler, graphIri, coercedType);
                    EmitQuad(handler, subject, predicate, listHead, graphIri);
                }
                else
                {
                    // Multiple values
                    foreach (var item in value.EnumerateArray())
                    {
                        ProcessValue(subject, predicate, item, handler, graphIri, coercedType, termLang);
                    }
                }
            }
            else
            {
                // If @container: @list with a non-array value, wrap in list
                if (isListContainer)
                {
                    // If the value is an object with @list, extract the inner list
                    // Don't double-wrap explicit @list objects
                    JsonElement listValue = value;
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@list", out var innerList))
                    {
                        listValue = innerList;
                    }
                    var listHead = ProcessList(listValue, handler, graphIri, coercedType);
                    EmitQuad(handler, subject, predicate, listHead, graphIri);
                }
                else
                {
                    ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLang);
                }
            }
        }
        finally
        {
            // Restore context if scoped context was applied
            if (savedContext != null)
            {
                _context.Clear();
                foreach (var kv in savedContext) _context[kv.Key] = kv.Value;

                _typeCoercion.Clear();
                foreach (var kv in savedTypeCoercion!) _typeCoercion[kv.Key] = kv.Value;

                _containerList.Clear();
                foreach (var kv in savedContainerList!) _containerList[kv.Key] = kv.Value;

                _containerLanguage.Clear();
                foreach (var kv in savedContainerLanguage!) _containerLanguage[kv.Key] = kv.Value;

                _containerIndex.Clear();
                foreach (var kv in savedContainerIndex!) _containerIndex[kv.Key] = kv.Value;

                _containerGraph.Clear();
                foreach (var kv in savedContainerGraph!) _containerGraph[kv.Key] = kv.Value;

                _containerId.Clear();
                foreach (var kv in savedContainerId!) _containerId[kv.Key] = kv.Value;

                _containerType.Clear();
                foreach (var kv in savedContainerType!) _containerType[kv.Key] = kv.Value;

                _termLanguage.Clear();
                foreach (var kv in savedTermLanguage!) _termLanguage[kv.Key] = kv.Value;

                _reverseProperty.Clear();
                foreach (var kv in savedReverseProperty!) _reverseProperty[kv.Key] = kv.Value;

                _scopedContext.Clear();
                foreach (var kv in savedScopedContext!) _scopedContext[kv.Key] = kv.Value;

                _typeAliases.Clear();
                foreach (var alias in savedTypeAliases!) _typeAliases.Add(alias);

                _idAliases.Clear();
                foreach (var alias in savedIdAliases!) _idAliases.Add(alias);

                _graphAliases.Clear();
                foreach (var alias in savedGraphAliases!) _graphAliases.Add(alias);

                _includedAliases.Clear();
                foreach (var alias in savedIncludedAliases!) _includedAliases.Add(alias);

                _nestAliases.Clear();
                foreach (var alias in savedNestAliases!) _nestAliases.Add(alias);

                _noneAliases.Clear();
                foreach (var alias in savedNoneAliases!) _noneAliases.Add(alias);

                _valueAliases.Clear();
                foreach (var alias in savedValueAliases!) _valueAliases.Add(alias);

                _languageAliases.Clear();
                foreach (var alias in savedLanguageAliases!) _languageAliases.Add(alias);

                _jsonAliases.Clear();
                foreach (var alias in savedJsonAliases!) _jsonAliases.Add(alias);

                _nullTerms.Clear();
                foreach (var t in savedNullTerms!) _nullTerms.Add(t);

                _prefixable.Clear();
                foreach (var t in savedPrefixable!) _prefixable.Add(t);

                _protectedTerms.Clear();
                foreach (var t in savedProtectedTerms!) _protectedTerms.Add(t);

                _vocabIri = savedVocabIri;
                _baseIri = savedBaseIri;
                _defaultLanguage = savedDefaultLanguage;
            }

            // Clear property-scoped tracking (c027, c028)
            // This happens after context restore to ensure nested objects processed in Values.cs
            // had access to the tracking for reverting non-propagating property-scoped changes
            _propScopedNoPropagate = false;
            _propScopedTermChanges = null;
            _propScopedCoercionChanges = null;
            _propScopedContainerTypeChanges = null;
            _propScopedContainerIndexChanges = null;
            _propScopedContainerListChanges = null;
            _propScopedContainerLangChanges = null;
            _propScopedContainerGraphChanges = null;
            _propScopedContainerIdChanges = null;
        }
    }

    /// <summary>
    /// Process a value in an @index container with optional property-valued @index.
    /// Emits main triple and, if property-valued @index is defined, emits the @index property triple.
    /// </summary>
    private void ProcessValueWithIndex(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLang,
        string? indexPropIri, string indexKey, string? indexPropCoercedType, bool isNone)
    {
        // Get the value's subject IRI (for property-valued @index emission)
        string? valueSubject = null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            if (coercedType == "@id")
            {
                // Type coerced to IRI - this IRI is both the object and the @index property subject
                valueSubject = ExpandIri(strVal);
            }
            else if (coercedType == "@vocab")
            {
                valueSubject = ExpandTerm(strVal);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            // Get @id from the object if present
            if (value.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                valueSubject = ExpandIri(idProp.GetString() ?? "");
            }
            // Also check @id aliases
            if (valueSubject == null)
            {
                foreach (var alias in _idAliases)
                {
                    if (value.TryGetProperty(alias, out var aliasIdProp) && aliasIdProp.ValueKind == JsonValueKind.String)
                    {
                        valueSubject = ExpandIri(aliasIdProp.GetString() ?? "");
                        break;
                    }
                }
            }
        }

        // Process the value normally (emit main triple)
        ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLang);

        // Emit @index property triple if property-valued @index is defined and not @none
        if (!string.IsNullOrEmpty(indexPropIri) && !string.IsNullOrEmpty(valueSubject) && !isNone)
        {
            // Expand the index key according to the @index property's type coercion
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
            EmitQuad(handler, valueSubject, indexPropIri, indexValue, graphIri);
        }
    }

    private void ProcessReverseProperty(string currentNode, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        // For reverse properties, each value becomes a subject with currentNode as object
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ProcessReverseValue(currentNode, predicate, item, handler, graphIri, coercedType);
            }
        }
        else
        {
            ProcessReverseValue(currentNode, predicate, value, handler, graphIri, coercedType);
        }
    }

    private void ProcessReverseValue(string currentNode, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        // The value becomes the subject, currentNode becomes the object
        string newSubject;

        if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            // If type coercion is @id, treat as IRI
            if (coercedType == "@id" || strVal.StartsWith("_:"))
            {
                newSubject = ExpandIri(strVal);
            }
            else
            {
                // String value becomes IRI
                newSubject = ExpandIri(strVal);
            }
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            // @list values are not allowed for reverse properties (er36)
            if (value.TryGetProperty("@list", out _))
            {
                throw new InvalidOperationException("invalid reverse property value");
            }

            // Nested object - parse it fully to process its properties
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            newSubject = ParseNode(ref tempReader, handler, null);

            // Skip reverse triple if node was dropped (e.g., @id unresolvable with null @base) (e060)
            if (newSubject == null)
                return;

            // Emit the reverse triple
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
        else
        {
            // Other types: generate blank node
            newSubject = GenerateBlankNode();
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
    }

    /// <summary>
    /// Process the @nest keyword which contains properties to be "un-nested" onto the current node.
    /// Properties inside @nest are processed as if they were direct properties of the containing node.
    /// </summary>
    private void ProcessNestKeyword(string currentNode, JsonElement nestElement, QuadHandler handler, string? graphIri)
    {
        if (nestElement.ValueKind == JsonValueKind.Object)
        {
            // @nest MUST NOT contain value objects (en04)
            if (nestElement.TryGetProperty("@value", out _))
            {
                throw new InvalidOperationException("invalid @nest value");
            }

            foreach (var prop in nestElement.EnumerateObject())
            {
                var propName = prop.Name;

                // Handle nested @nest recursively (n005)
                if (propName == "@nest" || _nestAliases.Contains(propName))
                {
                    ProcessNestKeyword(currentNode, prop.Value, handler, graphIri);
                    continue;
                }

                // Handle @type inside @nest - emit rdf:type for the parent node (n008)
                if (propName == "@type" || _typeAliases.Contains(propName))
                {
                    ProcessType(currentNode, prop.Value, handler, graphIri);
                    continue;
                }

                // Skip other JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
                if (propName.StartsWith('@') && IsKeywordLike(propName))
                    continue;

                var predicate = ExpandTerm(propName);
                if (string.IsNullOrEmpty(predicate))
                    continue;

                ProcessProperty(currentNode, predicate, propName, prop.Value, handler, graphIri);
            }
        }
        else if (nestElement.ValueKind == JsonValueKind.Array)
        {
            // @nest can be an array of objects
            foreach (var item in nestElement.EnumerateArray())
            {
                ProcessNestKeyword(currentNode, item, handler, graphIri);
            }
        }
        else
        {
            // @nest MUST be an object or array - strings, booleans, numbers, and value objects are invalid
            throw new InvalidOperationException("invalid @nest value");
        }
    }

    /// <summary>
    /// Process the @reverse keyword which contains reverse properties.
    /// Each property in @reverse becomes a predicate where values are subjects and currentNode is object.
    /// If the property is itself a reverse property (defined with @reverse in context),
    /// the double-negation results in a forward triple.
    /// </summary>
    private void ProcessReverseKeyword(string currentNode, JsonElement reverseElement, QuadHandler handler, string? graphIri)
    {
        // @reverse must be an object (er33)
        if (reverseElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("invalid @reverse value");
        }

        foreach (var prop in reverseElement.EnumerateObject())
        {
            var propName = prop.Name;
            var propValue = prop.Value;

            // JSON-LD keywords cannot be used as properties in @reverse (er25)
            if (propName.StartsWith("@") && IsJsonLdKeyword(propName))
            {
                throw new InvalidOperationException("invalid reverse property map");
            }

            // Check if this property is itself a reverse property (double-negation)
            if (_reverseProperty.TryGetValue(propName, out var reversePredicate))
            {
                // Double-negation: reverse of reverse = forward
                // Process as a forward property from currentNode to values
                var expandedPredicate = ExpandTermValue(reversePredicate);
                if (string.IsNullOrEmpty(expandedPredicate))
                    continue;

                if (propValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in propValue.EnumerateArray())
                    {
                        ProcessReverseKeywordValueForward(currentNode, expandedPredicate, item, handler, graphIri);
                    }
                }
                else
                {
                    ProcessReverseKeywordValueForward(currentNode, expandedPredicate, propValue, handler, graphIri);
                }
            }
            else
            {
                // Normal reverse property in @reverse block
                var predicate = ExpandTerm(propName);
                if (string.IsNullOrEmpty(predicate))
                    continue;

                if (propValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in propValue.EnumerateArray())
                    {
                        ProcessReverseKeywordValue(currentNode, predicate, item, handler, graphIri);
                    }
                }
                else
                {
                    ProcessReverseKeywordValue(currentNode, predicate, propValue, handler, graphIri);
                }
            }
        }
    }

    /// <summary>
    /// Process a forward triple from @reverse block (when property is itself @reverse, causing double-negation).
    /// currentNode becomes subject, value becomes object.
    /// </summary>
    private void ProcessReverseKeywordValueForward(string currentNode, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Parse the nested node and get its subject, then emit forward triple
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            var nestedSubject = ParseNode(ref tempReader, handler, null);

            // Skip forward triple if node was dropped (e.g., @id unresolvable with null @base) (e060)
            if (nestedSubject == null)
                return;

            // Forward triple: currentNode -> predicate -> nestedSubject
            EmitQuad(handler, currentNode, predicate, nestedSubject, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            var objectIri = ExpandIri(strVal);
            // Skip if IRI couldn't be resolved (e.g., relative IRI with null @base)
            if (objectIri == null)
                return;
            EmitQuad(handler, currentNode, predicate, objectIri, graphIri);
        }
    }

    /// <summary>
    /// Process a single value from @reverse - the value becomes a subject, currentNode becomes object.
    /// </summary>
    private void ProcessReverseKeywordValue(string currentNode, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Parse the nested node and get its subject
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            var nestedSubject = ParseNode(ref tempReader, handler, null);

            // Skip reverse triple if node was dropped (e.g., @id unresolvable with null @base) (e060)
            if (nestedSubject == null)
                return;

            // Emit the reverse triple
            EmitQuad(handler, nestedSubject, predicate, currentNode, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            var newSubject = ExpandIri(strVal);
            // Validate that the value expands to a valid node reference (er34)
            // Must be non-empty and must not contain invalid IRI characters (spaces, etc.)
            if (string.IsNullOrEmpty(newSubject))
            {
                throw new InvalidOperationException("invalid reverse property value");
            }
            // Check for invalid IRI characters - IRIs cannot contain unencoded spaces
            var iriContent = newSubject;
            if (iriContent.StartsWith('<') && iriContent.EndsWith('>'))
            {
                iriContent = iriContent.Substring(1, iriContent.Length - 2);
            }
            if (iriContent.Contains(' ') || iriContent.Contains('"') ||
                iriContent.Contains('{') || iriContent.Contains('}') ||
                iriContent.Contains('|') || iriContent.Contains('^') || iriContent.Contains('`'))
            {
                throw new InvalidOperationException("invalid reverse property value");
            }
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
        else
        {
            // Reverse property values must be node objects or strings (er34)
            throw new InvalidOperationException("invalid reverse property value");
        }
    }
}
