// JsonLdStreamParser.Context.cs
// @context processing and type-scoped context handling

using System;
using System.Collections.Generic;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    private void ProcessContext(JsonElement contextElement)
    {
        if (contextElement.ValueKind == JsonValueKind.String)
        {
            // Remote/external context - load using context resolver
            var contextUri = contextElement.GetString() ?? "";

            // Resolve relative URI against base
            string resolvedUri = contextUri;
            if (!Uri.IsWellFormedUriString(contextUri, UriKind.Absolute) && _baseIri != null)
            {
                if (Uri.TryCreate(_baseIri, UriKind.Absolute, out var baseUriObj) &&
                    Uri.TryCreate(baseUriObj, contextUri, out var resolved))
                {
                    resolvedUri = resolved.ToString();
                }
            }

            // Check for recursive context inclusion
            if (_loadedContexts.Contains(resolvedUri))
            {
                // In JSON-LD 1.0, recursive contexts are an error
                // In JSON-LD 1.1, scoped contexts can include themselves (we handle that elsewhere)
                if (_processingMode == "json-ld-1.0")
                {
                    throw new InvalidOperationException("recursive context inclusion");
                }
                // Already loaded - skip to prevent infinite recursion
                return;
            }

            // Load the context
            string? contextJson;
            try
            {
                contextJson = _contextResolver.ResolveAsync(contextUri, _baseIri).GetAwaiter().GetResult();
            }
            catch (JsonLdContextException ex)
            {
                throw new InvalidOperationException(ex.ErrorCode, ex);
            }

            if (contextJson == null)
            {
                // Context not found/not supported
                throw new InvalidOperationException("loading remote context failed");
            }

            // Track this context to detect recursion
            _loadedContexts.Add(resolvedUri);

            try
            {
                // Parse and process the loaded context
                using var doc = JsonDocument.Parse(contextJson);
                var root = doc.RootElement;

                // The loaded document should have a @context property
                if (root.TryGetProperty("@context", out var loadedContext))
                {
                    ProcessContext(loadedContext);
                }
                else
                {
                    // Invalid context document - must have @context
                    throw new InvalidOperationException("invalid remote context");
                }
            }
            finally
            {
                // Allow re-loading this context in different scopes
                // (but not recursively within the same scope)
                _loadedContexts.Remove(resolvedUri);
            }
            return;
        }

        if (contextElement.ValueKind == JsonValueKind.Null)
        {
            // Check for protected terms - cannot clear context with null if any terms are protected
            if (_protectedTerms.Count > 0)
            {
                throw new InvalidOperationException("invalid context nullification");
            }
            // Null context clears all term definitions
            _context.Clear();
            _typeCoercion.Clear();
            _containerList.Clear();
            _containerLanguage.Clear();
            _containerIndex.Clear();
            _termLanguage.Clear();
            _reverseProperty.Clear();
            _scopedContext.Clear();
            _indexProperty.Clear();
            _typeAliases.Clear();
            _idAliases.Clear();
            _graphAliases.Clear();
            _includedAliases.Clear();
            _nullTerms.Clear();
            _prefixable.Clear();
            _vocabIri = null;
            _defaultLanguage = null;
            // @context: null resets @base to the document base IRI (e060)
            // This is different from @base: null which disables relative IRI resolution
            _baseIri = _documentBaseIri;
            return;
        }

        if (contextElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ctx in contextElement.EnumerateArray())
            {
                ProcessContext(ctx);
            }
            return;
        }

        if (contextElement.ValueKind != JsonValueKind.Object)
        {
            // Context must be null, string, object, or array (er06)
            throw new InvalidOperationException("invalid local context");
        }

        // Check for context-level @protected first
        bool contextProtectedMode = false;
        if (contextElement.TryGetProperty("@protected", out var protectedProp))
        {
            if (protectedProp.ValueKind == JsonValueKind.True)
            {
                contextProtectedMode = true;
            }
            else if (protectedProp.ValueKind != JsonValueKind.False)
            {
                throw new InvalidOperationException("invalid @protected value");
            }
        }

        // Track terms defined in this context (for context-level @protected)
        var termsDefinedInThisContext = new List<string>();
        // Track terms that explicitly have @protected: false (should not be marked protected even if context-level is true)
        var explicitlyUnprotectedTerms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in contextElement.EnumerateObject())
        {
            var term = prop.Name;
            var value = prop.Value;

            // Empty string as term name is invalid (er52)
            if (term == "")
            {
                throw new InvalidOperationException("invalid term definition");
            }

            // @context cannot be redefined (er56)
            if (term == "@context")
            {
                throw new InvalidOperationException("keyword redefinition");
            }

            // Terms that look like relative IRIs are invalid (er48)
            // Relative IRIs start with "./" or "../"
            if (term.StartsWith("./") || term.StartsWith("../"))
            {
                throw new InvalidOperationException("invalid IRI mapping");
            }

            // Check if this term is already protected (cannot be redefined)
            // Skip keywords like @base, @vocab, @language, etc.
            if (!term.StartsWith('@') && _protectedTerms.Contains(term))
            {
                // Identical redefinition is allowed (pr23, pr15, pr16, pr17, pr18, pr19)
                // Check if the new definition matches the existing one
                if (!IsIdenticalDefinition(term, value))
                {
                    throw new InvalidOperationException("protected term redefinition");
                }
                // Identical redefinition - continue processing normally
            }

            if (term == "@base")
            {
                if (value.ValueKind == JsonValueKind.Null)
                {
                    // @base: null clears the base IRI
                    _baseIri = null;
                }
                else
                {
                    var newBase = value.GetString();
                    if (string.IsNullOrEmpty(newBase))
                    {
                        // @base: "" means keep current base (no change)
                        // This is important when an empty @base is used with a base option
                    }
                    else if (!string.IsNullOrEmpty(_baseIri) && !IsAbsoluteIri(newBase))
                    {
                        // Relative @base is resolved against current base
                        _baseIri = ResolveRelativeIri(_baseIri, newBase);
                    }
                    else
                    {
                        // Absolute @base or no current base
                        _baseIri = newBase;
                    }
                }
            }
            else if (term == "@vocab")
            {
                var vocabValue = value.GetString();
                // @vocab can be a compact IRI like "ex:ns/" - expand it (e124)
                // @vocab can also be a relative IRI like "/relative" - resolve against @base (e110)
                // @vocab can be "" (empty string) meaning use @base as vocabulary (e092)
                if (vocabValue == null)
                {
                    _vocabIri = null;
                }
                else if (vocabValue == "")
                {
                    // Empty @vocab is invalid in 1.0 mode (e115)
                    if (_processingMode == "json-ld-1.0")
                    {
                        throw new InvalidOperationException("invalid vocab mapping");
                    }
                    // Empty @vocab means use @base as vocabulary base (e092)
                    _vocabIri = _baseIri ?? "";
                }
                else
                {
                    // First try to expand as compact IRI or term - this handles cases
                    // like "ex:ns/" where "ex" is a defined prefix with @prefix: true (e124)
                    var expanded = ExpandCompactIri(vocabValue);
                    if (expanded != vocabValue)
                    {
                        // Compact IRI was expanded
                        _vocabIri = expanded;
                    }
                    else if (!IsAbsoluteIri(vocabValue))
                    {
                        // Relative @vocab is invalid in 1.0 mode (e116)
                        if (_processingMode == "json-ld-1.0")
                        {
                            throw new InvalidOperationException("invalid vocab mapping");
                        }
                        // Not expanded and not absolute - concatenate with existing @vocab (e111, e112)
                        // JSON-LD 1.1: relative @vocab is concatenated with current vocabulary (not resolved)
                        // This preserves the relative path segments for later concatenation with terms
                        if (!string.IsNullOrEmpty(_vocabIri) && IsAbsoluteIri(_vocabIri))
                        {
                            // Concatenate with existing vocabulary (no path resolution)
                            _vocabIri = _vocabIri + vocabValue;
                        }
                        else if (!string.IsNullOrEmpty(_baseIri))
                        {
                            // Fall back to resolving against @base
                            _vocabIri = ResolveRelativeIri(_baseIri, vocabValue);
                        }
                        else
                        {
                            _vocabIri = vocabValue;
                        }
                    }
                    else
                    {
                        // Absolute IRI - use as-is
                        _vocabIri = vocabValue;
                    }
                }
            }
            else if (term == "@language")
            {
                // @language must be a string or null (er09)
                if (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidOperationException("invalid default language");
                }
                _defaultLanguage = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            }
            else if (term == "@propagate")
            {
                // @propagate is a 1.1 feature - invalid in 1.0 (ep02)
                if (_processingMode == "json-ld-1.0")
                {
                    throw new InvalidOperationException("invalid context entry");
                }
                // @propagate must be a boolean (c030)
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                {
                    throw new InvalidOperationException("invalid @propagate value");
                }
                // @propagate: true means type-scoped context propagates to nested nodes
                // Default is false for type-scoped contexts, true for property-scoped
                if (value.ValueKind == JsonValueKind.True)
                {
                    _typeScopedPropagate = true;
                }
            }
            else if (term == "@version")
            {
                // @version must be 1.1 (ep03)
                // Check for conflict with processingMode (er34)
                if (value.ValueKind == JsonValueKind.Number)
                {
                    var version = value.GetDouble();
                    if (version != 1.1)
                    {
                        throw new InvalidOperationException("invalid @version value");
                    }
                    // er34: processingMode json-ld-1.0 conflicts with @version: 1.1
                    if (_processingMode == "json-ld-1.0")
                    {
                        throw new InvalidOperationException("processing mode conflict");
                    }
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    var versionStr = value.GetString();
                    if (versionStr != "1.1")
                    {
                        throw new InvalidOperationException("invalid @version value");
                    }
                    // er34: processingMode json-ld-1.0 conflicts with @version: 1.1
                    if (_processingMode == "json-ld-1.0")
                    {
                        throw new InvalidOperationException("processing mode conflict");
                    }
                }
            }
            else if (term == "@direction")
            {
                // @direction must be "ltr" or "rtl" (or null) (di08)
                if (value.ValueKind == JsonValueKind.String)
                {
                    var dir = value.GetString();
                    if (dir != "ltr" && dir != "rtl")
                    {
                        throw new InvalidOperationException("invalid base direction");
                    }
                }
                else if (value.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidOperationException("invalid base direction");
                }
            }
            else if (term == "@protected")
            {
                // Context-level @protected must be a boolean
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                {
                    throw new InvalidOperationException("invalid @protected value");
                }
                // Context-level @protected: true marks all following terms in this context as protected
                // We handle this by processing all terms first, then marking them protected below
                // For now, just continue - we'll handle this after the loop
            }
            else if (term == "@import")
            {
                // @import is a 1.1 feature - invalid in 1.0 (so01)
                if (_processingMode == "json-ld-1.0")
                {
                    throw new InvalidOperationException("invalid context entry");
                }

                // @import must be a string (so02)
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("invalid @import value");
                }

                // @import cannot be used inside an imported context (so12)
                if (_importDepth > 0)
                {
                    throw new InvalidOperationException("invalid context entry");
                }

                // Check for import depth overflow (so10)
                if (_importDepth >= MaxImportDepth)
                {
                    throw new InvalidOperationException("context overflow");
                }

                var importUri = value.GetString() ?? "";

                // Load the imported context
                string? importedJson;
                try
                {
                    importedJson = _contextResolver.ResolveAsync(importUri, _baseIri).GetAwaiter().GetResult();
                }
                catch (JsonLdContextException ex)
                {
                    throw new InvalidOperationException(ex.ErrorCode, ex);
                }

                if (importedJson == null)
                {
                    throw new InvalidOperationException("loading remote context failed");
                }

                // Track terms before import to identify newly imported terms
                var termsBefore = new HashSet<string>(_context.Keys, StringComparer.Ordinal);
                foreach (var alias in _typeAliases) termsBefore.Add(alias);
                foreach (var alias in _idAliases) termsBefore.Add(alias);
                foreach (var alias in _graphAliases) termsBefore.Add(alias);
                foreach (var alias in _includedAliases) termsBefore.Add(alias);
                foreach (var alias in _nestAliases) termsBefore.Add(alias);
                foreach (var alias in _noneAliases) termsBefore.Add(alias);
                foreach (var alias in _valueAliases) termsBefore.Add(alias);

                // Parse and process the imported context
                _importDepth++;
                try
                {
                    using var doc = JsonDocument.Parse(importedJson);
                    var root = doc.RootElement;

                    // The imported document should have a @context property (so13 - must be single context)
                    if (root.TryGetProperty("@context", out var importedContext))
                    {
                        // @import can only reference a single context object, not an array (so13)
                        if (importedContext.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidOperationException("invalid remote context");
                        }
                        ProcessContext(importedContext);
                    }
                    else
                    {
                        throw new InvalidOperationException("invalid remote context");
                    }
                }
                finally
                {
                    _importDepth--;
                }

                // Track terms added by import (for @protected handling)
                foreach (var t in _context.Keys)
                {
                    if (!termsBefore.Contains(t))
                        termsDefinedInThisContext.Add(t);
                }
                foreach (var alias in _typeAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _idAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _graphAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _includedAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _nestAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _noneAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
                foreach (var alias in _valueAliases)
                {
                    if (!termsBefore.Contains(alias))
                        termsDefinedInThisContext.Add(alias);
                }
            }
            else if (value.ValueKind == JsonValueKind.Null)
            {
                // Term mapped to null - decouple from @vocab
                _nullTerms.Add(term);
                termsDefinedInThisContext.Add(term);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var mappedValue = value.GetString() ?? "";

                // Keywords cannot be aliased to other keywords (er01)
                if (term.StartsWith('@') && IsJsonLdKeyword(term) && mappedValue.StartsWith('@') && IsJsonLdKeyword(mappedValue))
                {
                    throw new InvalidOperationException("keyword redefinition");
                }

                // Check for keyword alias or transitive alias chain
                if (mappedValue == "@type" || _typeAliases.Contains(mappedValue))
                {
                    _typeAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@id" || _idAliases.Contains(mappedValue))
                {
                    _idAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@graph" || _graphAliases.Contains(mappedValue))
                {
                    _graphAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@included" || _includedAliases.Contains(mappedValue))
                {
                    _includedAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@nest" || _nestAliases.Contains(mappedValue))
                {
                    _nestAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@none" || _noneAliases.Contains(mappedValue))
                {
                    _noneAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@value" || _valueAliases.Contains(mappedValue))
                {
                    _valueAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@language" || _languageAliases.Contains(mappedValue))
                {
                    _languageAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue == "@json" || _jsonAliases.Contains(mappedValue))
                {
                    _jsonAliases.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
                else if (mappedValue.StartsWith('@') && IsKeywordLike(mappedValue))
                {
                    // Per JSON-LD 1.1: Terms mapping to keyword-like values (@ followed by
                    // only lowercase a-z) that aren't real keywords are ignored (pr37)
                    // The term will use @vocab instead when expanded
                }
                else
                {
                    // Simple term -> IRI mapping (always prefix-able)
                    // Check if term looks like compact IRI and verify consistency (er44)
                    var termColonIdx = term.IndexOf(':');
                    if (termColonIdx > 0)
                    {
                        var termPrefix = term.Substring(0, termColonIdx);
                        var termLocalPart = term.Substring(termColonIdx + 1);
                        // If prefix is defined, the mapped value must match prefix expansion + local part
                        if (!termLocalPart.StartsWith("//") && _context.TryGetValue(termPrefix, out var prefixIri))
                        {
                            var expectedIri = prefixIri + termLocalPart;
                            // Expand mappedValue if it's a compact IRI
                            var expandedMappedValue = ExpandCompactIri(mappedValue);
                            if (expandedMappedValue != expectedIri)
                            {
                                throw new InvalidOperationException("invalid IRI mapping");
                            }
                        }
                    }
                    _context[term] = mappedValue;
                    _prefixable.Add(term);
                    termsDefinedInThisContext.Add(term);
                }
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                // Special handling for keyword term definitions (ec02)
                // @type can only have @container and @protected in its term definition
                if (term == "@type")
                {
                    // Keyword redefinition is invalid in 1.0 (er42)
                    if (_processingMode == "json-ld-1.0")
                    {
                        throw new InvalidOperationException("keyword redefinition");
                    }

                    // Check if @type is already protected (pr32)
                    if (_protectedTerms.Contains("@type"))
                    {
                        // @type is protected - any redefinition is invalid
                        throw new InvalidOperationException("protected term redefinition");
                    }

                    bool hasValidContent = false;
                    bool makingProtected = false;
                    foreach (var typeDefProp in value.EnumerateObject())
                    {
                        var k = typeDefProp.Name;
                        if (k == "@container" || k == "@protected")
                        {
                            hasValidContent = true;
                            if (k == "@protected" && typeDefProp.Value.ValueKind == JsonValueKind.True)
                            {
                                makingProtected = true;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException("invalid term definition");
                        }
                    }
                    if (!hasValidContent)
                    {
                        // Empty object is invalid
                        throw new InvalidOperationException("invalid term definition");
                    }
                    // Mark @type as protected if specified (pr30)
                    if (makingProtected)
                    {
                        _protectedTerms.Add("@type");
                    }
                    // Process @type @container if present
                    if (value.TryGetProperty("@container", out var typeContainerProp))
                    {
                        // @type can have @container: @set
                        // We don't need to do anything special here as @set is the default behavior
                    }
                    continue;
                }

                // Expanded term definition
                // Validate that only valid keywords are used (ec01)
                foreach (var termProp in value.EnumerateObject())
                {
                    var key = termProp.Name;
                    if (key.StartsWith('@'))
                    {
                        // Valid keywords in expanded term definitions
                        if (key != "@id" && key != "@type" && key != "@container" &&
                            key != "@context" && key != "@language" && key != "@direction" &&
                            key != "@reverse" && key != "@protected" && key != "@prefix" &&
                            key != "@nest" && key != "@propagate" && key != "@index")
                        {
                            throw new InvalidOperationException("invalid term definition");
                        }
                        // @index is only valid when @container includes @index
                        if (key == "@index")
                        {
                            bool hasIndexContainer = false;
                            if (value.TryGetProperty("@container", out var containerCheck))
                            {
                                if (containerCheck.ValueKind == JsonValueKind.String)
                                    hasIndexContainer = containerCheck.GetString() == "@index";
                                else if (containerCheck.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var c in containerCheck.EnumerateArray())
                                        if (c.GetString() == "@index") hasIndexContainer = true;
                                }
                            }
                            if (!hasIndexContainer)
                            {
                                throw new InvalidOperationException("invalid term definition");
                            }

                            // Validate and store @index value in term definition (property-valued index)
                            if (value.TryGetProperty("@index", out var indexValueProp))
                            {
                                // Property-valued @index is 1.1-only (pi01)
                                if (_processingMode == "json-ld-1.0")
                                {
                                    throw new InvalidOperationException("invalid term definition");
                                }
                                // @index must be a string (pi04)
                                if (indexValueProp.ValueKind != JsonValueKind.String)
                                {
                                    throw new InvalidOperationException("invalid term definition");
                                }
                                // @index must not be a keyword (pi03)
                                var indexVal = indexValueProp.GetString();
                                if (!string.IsNullOrEmpty(indexVal) && indexVal.StartsWith('@'))
                                {
                                    throw new InvalidOperationException("invalid term definition");
                                }
                                // Store the @index property for property-valued index expansion (pi06-pi11)
                                if (!string.IsNullOrEmpty(indexVal))
                                {
                                    _indexProperty[term] = indexVal;
                                }
                            }
                        }
                    }
                }

                if (value.TryGetProperty("@id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.Null)
                    {
                        // @id: null - decouple term from @vocab
                        _nullTerms.Add(term);
                    }
                    else
                    {
                        var idValue = idProp.GetString() ?? "";

                        // @context cannot be aliased (er19)
                        if (idValue == "@context")
                        {
                            throw new InvalidOperationException("invalid keyword alias");
                        }

                        // Handle @id mapping to actual keywords - these create aliases (c037)
                        // In 1.0 mode, keyword aliasing is not allowed (er26)
                        // @type as @id is allowed for aliasing, but NOT with @type coercion (er43)
                        if (idValue == "@type" || _typeAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            // Only allow @id: "@type" if there's no @type coercion defined
                            // er43 has "@id": "@type" AND "@type": "@id" which is invalid
                            // pr30 has "@id": "@type" with @container and @protected only - valid alias
                            if (value.TryGetProperty("@type", out _))
                            {
                                throw new InvalidOperationException("invalid IRI mapping");
                            }
                            _typeAliases.Add(term);
                        }
                        else if (idValue == "@nest" || _nestAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _nestAliases.Add(term);
                        }
                        else if (idValue == "@id" || _idAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _idAliases.Add(term);
                        }
                        else if (idValue == "@graph" || _graphAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _graphAliases.Add(term);
                        }
                        else if (idValue == "@included" || _includedAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _includedAliases.Add(term);
                        }
                        else if (idValue == "@value" || _valueAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _valueAliases.Add(term);
                        }
                        else if (idValue == "@language" || _languageAliases.Contains(idValue))
                        {
                            // Keyword redefinition is invalid in 1.0 (er26)
                            if (_processingMode == "json-ld-1.0")
                            {
                                throw new InvalidOperationException("keyword redefinition");
                            }
                            _languageAliases.Add(term);
                        }
                        // Per JSON-LD 1.1: @id values that look like keywords (e.g., "@ignoreMe")
                        // but aren't real keywords should be ignored (e120)
                        else if (idValue.StartsWith('@') && IsKeywordLike(idValue))
                        {
                            // Ignore keyword-like @id, term uses @vocab
                            if (!string.IsNullOrEmpty(_vocabIri))
                            {
                                _context[term] = _vocabIri + term;
                            }
                        }
                        else
                        {
                            // Check for cyclic IRI mapping (er10)
                            // If @id looks like a compact IRI using the term itself as prefix, it's cyclic
                            var colonIdx = idValue.IndexOf(':');
                            if (colonIdx > 0)
                            {
                                var prefix = idValue.Substring(0, colonIdx);
                                var localPart = idValue.Substring(colonIdx + 1);
                                // Cyclic if prefix equals the term being defined
                                // and local part doesn't start with // (not absolute IRI like http://)
                                if (prefix == term && !localPart.StartsWith("//"))
                                {
                                    throw new InvalidOperationException("cyclic IRI mapping");
                                }
                            }

                            // Check if term looks like compact IRI and verify consistency (er44)
                            var termColonIdx = term.IndexOf(':');
                            if (termColonIdx > 0)
                            {
                                var termPrefix = term.Substring(0, termColonIdx);
                                var termLocalPart = term.Substring(termColonIdx + 1);
                                // If prefix is defined, the mapped value must match prefix expansion + local part
                                if (!termLocalPart.StartsWith("//") && _context.TryGetValue(termPrefix, out var prefixIri))
                                {
                                    var expectedIri = prefixIri + termLocalPart;
                                    // Expand idValue if it's a compact IRI
                                    var expandedIdValue = ExpandCompactIri(idValue);
                                    if (expandedIdValue != expectedIri)
                                    {
                                        throw new InvalidOperationException("invalid IRI mapping");
                                    }
                                }
                            }

                            _context[term] = idValue;
                        }
                    }
                }
                else
                {
                    // No explicit @id - need to derive the IRI
                    // First check if term looks like a compact IRI (prefix:localName)
                    // Within context definitions, compact IRIs are expanded regardless of @prefix flag (e050)
                    var termColonIdx = term.IndexOf(':');
                    if (termColonIdx > 0)
                    {
                        var termPrefix = term.Substring(0, termColonIdx);
                        var termLocalName = term.Substring(termColonIdx + 1);
                        // In context definitions, we can use any term as a prefix, not just _prefixable ones
                        if (!termLocalName.StartsWith("//") && termPrefix != "_" &&
                            _context.TryGetValue(termPrefix, out var termPrefixIri))
                        {
                            _context[term] = termPrefixIri + termLocalName;
                        }
                        else if (!string.IsNullOrEmpty(_vocabIri))
                        {
                            _context[term] = _vocabIri + term;
                        }
                    }
                    else if (!string.IsNullOrEmpty(_vocabIri))
                    {
                        // Simple term - use @vocab + term (JSON-LD 1.1)
                        // This is important for type-scoped contexts where term is used as @type value
                        _context[term] = _vocabIri + term;
                    }
                }

                // Handle @prefix - marks term as usable as a prefix in compact IRIs (e124)
                // Expanded term definitions are NOT prefix-able by default (unlike simple string mappings)
                if (value.TryGetProperty("@prefix", out var prefixProp))
                {
                    // @prefix must be a boolean (er53)
                    if (prefixProp.ValueKind != JsonValueKind.True && prefixProp.ValueKind != JsonValueKind.False)
                    {
                        throw new InvalidOperationException("invalid @prefix value");
                    }
                    if (prefixProp.ValueKind == JsonValueKind.True)
                    {
                        // pr33: Keyword aliases cannot be declared as prefixes
                        // Check if the term maps to a keyword via @id
                        if (_typeAliases.Contains(term) || _idAliases.Contains(term) ||
                            _graphAliases.Contains(term) || _includedAliases.Contains(term) ||
                            _valueAliases.Contains(term) || _languageAliases.Contains(term) ||
                            _nestAliases.Contains(term))
                        {
                            throw new InvalidOperationException("invalid term definition");
                        }
                        _prefixable.Add(term);
                    }
                }

                // Handle @reverse - the term maps to a reverse property
                if (value.TryGetProperty("@reverse", out var reverseProp))
                {
                    // Per JSON-LD 1.1: @nest MUST NOT be used with @reverse (en06)
                    if (value.TryGetProperty("@nest", out _))
                    {
                        throw new InvalidOperationException("invalid @nest value");
                    }
                    // Per JSON-LD 1.1: @id MUST NOT be used with @reverse (er14)
                    if (value.TryGetProperty("@id", out _))
                    {
                        throw new InvalidOperationException("invalid reverse property");
                    }
                    // Per JSON-LD 1.1: @reverse can only have @container: @set or @index (er17)
                    if (value.TryGetProperty("@container", out var reverseContainerProp))
                    {
                        bool isValidReverseContainer = false;
                        if (reverseContainerProp.ValueKind == JsonValueKind.String)
                        {
                            var c = reverseContainerProp.GetString();
                            isValidReverseContainer = c == "@set" || c == "@index";
                        }
                        else if (reverseContainerProp.ValueKind == JsonValueKind.Array)
                        {
                            isValidReverseContainer = true; // Assume valid, check for invalid
                            foreach (var c in reverseContainerProp.EnumerateArray())
                            {
                                var cv = c.GetString();
                                if (cv != "@set" && cv != "@index")
                                {
                                    isValidReverseContainer = false;
                                    break;
                                }
                            }
                        }
                        if (!isValidReverseContainer)
                        {
                            throw new InvalidOperationException("invalid reverse property");
                        }
                    }

                    var reverseIri = reverseProp.GetString();
                    // Per JSON-LD 1.1: keyword-like values that aren't real keywords are ignored (pr38)
                    if (!string.IsNullOrEmpty(reverseIri) &&
                        !(reverseIri.StartsWith('@') && IsKeywordLike(reverseIri)))
                    {
                        // Validate @reverse value can expand to valid IRI (er50)
                        // The value must be an absolute IRI, a term, a compact IRI, or @vocab-expandable
                        var expandedReverse = ExpandTermValue(reverseIri);
                        if (string.IsNullOrEmpty(expandedReverse))
                        {
                            throw new InvalidOperationException("invalid IRI mapping");
                        }
                        // Also check for invalid IRI characters (spaces, etc.)
                        var iriContent = expandedReverse;
                        if (iriContent.StartsWith('<') && iriContent.EndsWith('>'))
                        {
                            iriContent = iriContent.Substring(1, iriContent.Length - 2);
                        }
                        if (iriContent.Contains(' ') || iriContent.Contains('"') ||
                            iriContent.Contains('{') || iriContent.Contains('}') ||
                            iriContent.Contains('|') || iriContent.Contains('^') || iriContent.Contains('`'))
                        {
                            throw new InvalidOperationException("invalid IRI mapping");
                        }
                        _reverseProperty[term] = reverseIri;
                    }
                }

                if (value.TryGetProperty("@type", out var typeProp))
                {
                    // @type in term definition must be a string (er12)
                    if (typeProp.ValueKind != JsonValueKind.String && typeProp.ValueKind != JsonValueKind.Null)
                    {
                        throw new InvalidOperationException("invalid type mapping");
                    }
                    var typeVal = typeProp.ValueKind == JsonValueKind.String ? typeProp.GetString() : null;
                    if (typeVal == "@id")
                    {
                        _typeCoercion[term] = "@id";
                    }
                    else if (typeVal == "@none" || _noneAliases.Contains(typeVal ?? ""))
                    {
                        // @type: @none is 1.1-only (tn01)
                        if (_processingMode == "json-ld-1.0")
                        {
                            throw new InvalidOperationException("invalid type mapping");
                        }
                        // @type: @none means no type coercion (tn02) - don't add to _typeCoercion
                        // This ensures values are emitted as plain literals without datatype
                    }
                    else if (typeVal == "@vocab" || typeVal == "@json")
                    {
                        _typeCoercion[term] = typeVal;
                    }
                    else if (!string.IsNullOrEmpty(typeVal))
                    {
                        // @type value can be @id, @vocab, @json, @none, an absolute IRI, or a term to expand (er13)
                        // Blank nodes are NOT valid type mappings
                        if (typeVal.StartsWith("_:"))
                        {
                            throw new InvalidOperationException("invalid type mapping");
                        }
                        // Relative IRIs are NOT valid type mappings (er23)
                        // Must be: absolute IRI, term, or compact IRI (contains ':' with non-relative local part)
                        var colonIdx = typeVal.IndexOf(':');
                        if (colonIdx < 0)
                        {
                            // No colon - must be a term or keyword
                            // If it looks like a relative path (contains '/'), it's invalid
                            if (typeVal.Contains('/'))
                            {
                                throw new InvalidOperationException("invalid type mapping");
                            }
                        }
                        else
                        {
                            // Has colon - could be absolute IRI or compact IRI
                            var localPart = typeVal.Substring(colonIdx + 1);
                            // If local part starts with "//" it's a scheme-relative URL (valid)
                            // If no scheme prefix followed by "//", it needs to be a valid prefix
                            if (!localPart.StartsWith("//"))
                            {
                                var prefix = typeVal.Substring(0, colonIdx);
                                // Valid if prefix is a defined term/prefix
                                // For now, allow if it looks like a valid prefix (short string without path separators)
                                // Invalid: "relative/iri" type patterns without a valid scheme
                            }
                        }
                        _typeCoercion[term] = typeVal;
                    }
                }

                if (value.TryGetProperty("@container", out var containerProp))
                {
                    // Clear all existing container flags for this term before setting new ones
                    // This ensures that redefining @container replaces the old container type
                    _containerList.Remove(term);
                    _containerLanguage.Remove(term);
                    _containerIndex.Remove(term);
                    _containerGraph.Remove(term);
                    _containerId.Remove(term);
                    _containerType.Remove(term);

                    // @container can be a string or an array in JSON-LD 1.1
                    void ProcessContainerValue(string? containerVal)
                    {
                        if (containerVal == "@list")
                            _containerList[term] = true;
                        else if (containerVal == "@language")
                            _containerLanguage[term] = true;
                        else if (containerVal == "@index")
                            _containerIndex[term] = true;
                        else if (containerVal == "@graph")
                        {
                            // @container: @graph is 1.1-only (er21)
                            if (_processingMode == "json-ld-1.0")
                                throw new InvalidOperationException("invalid container mapping");
                            _containerGraph[term] = true;
                        }
                        else if (containerVal == "@id")
                        {
                            // @container: @id is 1.1-only (er21)
                            if (_processingMode == "json-ld-1.0")
                                throw new InvalidOperationException("invalid container mapping");
                            _containerId[term] = true;
                        }
                        else if (containerVal == "@type")
                        {
                            // @container: @type is 1.1-only (er21)
                            if (_processingMode == "json-ld-1.0")
                                throw new InvalidOperationException("invalid container mapping");
                            _containerType[term] = true;
                        }
                        else if (containerVal == "@set")
                        {
                            // @set containers treated as simple containers
                        }
                        else if (!string.IsNullOrEmpty(containerVal))
                        {
                            // Invalid container value (em01)
                            throw new InvalidOperationException("invalid container mapping");
                        }
                    }

                    if (containerProp.ValueKind == JsonValueKind.String)
                    {
                        ProcessContainerValue(containerProp.GetString());
                    }
                    else if (containerProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var containerItem in containerProp.EnumerateArray())
                        {
                            if (containerItem.ValueKind == JsonValueKind.String)
                                ProcessContainerValue(containerItem.GetString());
                        }
                    }

                    // When @container includes @type, @type must be @id, @vocab, or @none (m020)
                    // A datatype IRI like "literal" is invalid with @container: @type
                    if (_containerType.ContainsKey(term) && _typeCoercion.TryGetValue(term, out var typeCoercionVal))
                    {
                        if (typeCoercionVal != "@id" && typeCoercionVal != "@vocab" && typeCoercionVal != "@none")
                        {
                            throw new InvalidOperationException("invalid type mapping");
                        }
                    }
                }

                // Handle term-level @language
                if (value.TryGetProperty("@language", out var langProp))
                {
                    if (langProp.ValueKind == JsonValueKind.Null)
                    {
                        // @language: null means no language tag (override default)
                        _termLanguage[term] = "";
                    }
                    else
                    {
                        var langVal = langProp.GetString();
                        if (!string.IsNullOrEmpty(langVal))
                        {
                            _termLanguage[term] = langVal;
                        }
                    }
                }

                // Handle scoped context - nested @context for this term
                if (value.TryGetProperty("@context", out var scopedContextProp))
                {
                    _scopedContext[term] = scopedContextProp.GetRawText();
                }

                // Handle term-level @protected
                bool termExplicitlyProtected = false;
                bool termExplicitlyUnprotected = false;
                if (value.TryGetProperty("@protected", out var termProtectedProp))
                {
                    if (termProtectedProp.ValueKind == JsonValueKind.True)
                    {
                        termExplicitlyProtected = true;
                    }
                    else if (termProtectedProp.ValueKind == JsonValueKind.False)
                    {
                        termExplicitlyUnprotected = true;
                    }
                    else
                    {
                        throw new InvalidOperationException("invalid @protected value");
                    }
                }

                // Track this term as defined in this context
                termsDefinedInThisContext.Add(term);

                // If term explicitly has @protected: true, mark it protected immediately
                if (termExplicitlyProtected)
                {
                    _protectedTerms.Add(term);
                    // Track type-scoped protected terms separately (pr22)
                    if (_isApplyingTypeScopedContext)
                    {
                        _typeScopedProtectedTerms.Add(term);
                    }
                }
                // If term explicitly has @protected: false, don't mark it even if context-level is true
                else if (termExplicitlyUnprotected)
                {
                    explicitlyUnprotectedTerms.Add(term);
                }

                // Handle @nest - the value MUST be @nest or an alias of @nest (en05)
                if (value.TryGetProperty("@nest", out var nestProp))
                {
                    var nestVal = nestProp.GetString() ?? "";
                    if (nestVal != "@nest" && !_nestAliases.Contains(nestVal))
                    {
                        throw new InvalidOperationException("invalid @nest value");
                    }
                }

                // Validate that term has a valid IRI mapping when @container is present (er20)
                // If term definition has @container but no @id/@reverse
                // and can't be expanded via @vocab, it's invalid
                bool hasContainer = value.TryGetProperty("@container", out _);
                bool hasIriMapping = _context.ContainsKey(term) ||
                                     _reverseProperty.ContainsKey(term) ||
                                     _nullTerms.Contains(term) ||
                                     _typeAliases.Contains(term) ||
                                     _idAliases.Contains(term) ||
                                     _graphAliases.Contains(term) ||
                                     _nestAliases.Contains(term) ||
                                     _includedAliases.Contains(term) ||
                                     _valueAliases.Contains(term) ||
                                     _languageAliases.Contains(term);
                // Only check for IRI mapping if @container was specified
                if (hasContainer && !hasIriMapping && !term.StartsWith('@'))
                {
                    throw new InvalidOperationException("invalid IRI mapping");
                }
            }
            else
            {
                // Term definitions must be null, string, or object (er11)
                throw new InvalidOperationException("invalid term definition");
            }
        }

        // If context-level @protected is true, mark all terms defined in this context as protected
        // (except those explicitly marked @protected: false)
        if (contextProtectedMode)
        {
            foreach (var definedTerm in termsDefinedInThisContext)
            {
                if (!explicitlyUnprotectedTerms.Contains(definedTerm))
                {
                    _protectedTerms.Add(definedTerm);
                    // Track type-scoped protected terms separately (pr22)
                    if (_isApplyingTypeScopedContext)
                    {
                        _typeScopedProtectedTerms.Add(definedTerm);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check if any type in @type has a scoped context.
    /// </summary>
    private bool HasTypeScopedContext(JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeTerm = typeElement.GetString() ?? "";
            return _scopedContext.ContainsKey(typeTerm);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeTerm = t.GetString() ?? "";
                    if (_scopedContext.ContainsKey(typeTerm))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Expand type IRIs from @type element. Called BEFORE type-scoped context is applied.
    /// </summary>
    private List<string> ExpandTypeIris(JsonElement typeElement)
    {
        var result = new List<string>();

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeIri = ExpandTypeIri(typeElement.GetString() ?? "");
            // Skip null type IRIs (unresolvable relative IRIs when @base is null) (e060)
            if (typeIri != null)
                result.Add(typeIri);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeIri = ExpandTypeIri(t.GetString() ?? "");
                    // Skip null type IRIs (unresolvable relative IRIs when @base is null) (e060)
                    if (typeIri != null)
                        result.Add(typeIri);
                }
                else
                {
                    // @type array elements must be strings (er28)
                    throw new InvalidOperationException("invalid type value");
                }
            }
        }
        else
        {
            // @type must be a string or array of strings (er28)
            throw new InvalidOperationException("invalid type value");
        }

        return result;
    }

    /// <summary>
    /// Apply type-scoped contexts from @type values.
    /// Per JSON-LD spec, types are processed in lexicographical order of expanded IRIs.
    /// </summary>
    private void ApplyTypeScopedContexts(JsonElement typeElement)
    {
        // Set flag so ProcessContext knows to track type-scoped protected terms (pr22)
        var wasApplyingTypeScopedContext = _isApplyingTypeScopedContext;
        _isApplyingTypeScopedContext = true;

        try
        {
            if (typeElement.ValueKind == JsonValueKind.String)
            {
                var typeTerm = typeElement.GetString() ?? "";
                if (_scopedContext.TryGetValue(typeTerm, out var scopedJson))
                {
                    using var doc = JsonDocument.Parse(scopedJson);
                    ProcessContext(doc.RootElement);
                }
            }
            else if (typeElement.ValueKind == JsonValueKind.Array)
            {
                // Collect types with scoped contexts AND save their context JSON
                // IMPORTANT: We must save the JSON strings before applying any contexts because
                // applying a context with `null` will clear _scopedContext, losing other type contexts
                var typesWithContexts = new List<(string term, string expandedIri, string scopedJson)>();
                foreach (var t in typeElement.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var typeTerm = t.GetString() ?? "";
                        if (_scopedContext.TryGetValue(typeTerm, out var scopedJson))
                        {
                            // Expand the type IRI for sorting
                            var expandedIri = ExpandTypeIri(typeTerm);
                            typesWithContexts.Add((typeTerm, expandedIri, scopedJson));
                        }
                    }
                }

                // Sort lexicographically by expanded IRI
                typesWithContexts.Sort((a, b) => string.Compare(a.expandedIri, b.expandedIri, StringComparison.Ordinal));

                // Apply scoped contexts in sorted order using saved JSON
                foreach (var (_, _, scopedJson) in typesWithContexts)
                {
                    using var doc = JsonDocument.Parse(scopedJson);
                    ProcessContext(doc.RootElement);
                }
            }
        }
        finally
        {
            _isApplyingTypeScopedContext = wasApplyingTypeScopedContext;
        }
    }

    private void ProcessType(string subject, JsonElement typeElement, QuadHandler handler, string? graphIri)
    {
        var graph = graphIri ?? _currentGraph;

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeIri = ExpandTypeIri(typeElement.GetString() ?? "");
            // Skip null type IRIs (unresolvable relative IRIs when @base is null) (e060)
            if (typeIri != null)
                EmitQuad(handler, subject, RdfType, typeIri, graph);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeIri = ExpandTypeIri(t.GetString() ?? "");
                    // Skip null type IRIs (unresolvable relative IRIs when @base is null) (e060)
                    if (typeIri != null)
                        EmitQuad(handler, subject, RdfType, typeIri, graph);
                }
                else
                {
                    // Array elements must be strings (er28)
                    throw new InvalidOperationException("invalid type value");
                }
            }
        }
        else
        {
            // @type must be a string or array of strings (er28)
            throw new InvalidOperationException("invalid type value");
        }
    }

    /// <summary>
    /// Expand a type IRI using @vocab for relative IRIs (not @base).
    /// </summary>
    private string ExpandTypeIri(string value)
    {
        if (string.IsNullOrEmpty(value))
            return GenerateBlankNode();

        // Blank node
        if (value.StartsWith("_:"))
            return value;

        // Check context first (exact term match)
        if (_context.TryGetValue(value, out var expanded))
        {
            // Check if expanded value is a compact IRI using a known prefix before IsAbsoluteIri (c038)
            // e.g., "Print" -> "bibo:Book" where "bibo" is a prefix
            var expandedColonIdx = expanded.IndexOf(':');
            if (expandedColonIdx > 0)
            {
                var expandedPrefix = expanded.Substring(0, expandedColonIdx);
                var expandedLocalName = expanded.Substring(expandedColonIdx + 1);
                if (!expandedLocalName.StartsWith("//") && expandedPrefix != "_" &&
                    _prefixable.Contains(expandedPrefix) && _context.TryGetValue(expandedPrefix, out var expandedPrefixIri))
                {
                    return FormatIri(expandedPrefixIri + expandedLocalName);
                }
            }
            if (IsAbsoluteIri(expanded))
                return FormatIri(expanded);
            return ExpandTypeIri(expanded);
        }

        // Check for compact IRI (prefix:localName) before checking absolute IRI
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            // Key check: if localName starts with "//", this is NOT a compact IRI
            // Also, "_" as a prefix would conflict with blank nodes
            // The prefix must be in _prefixable to be used for expansion (pr29)
            if (!localName.StartsWith("//") && prefix != "_" &&
                _prefixable.Contains(prefix) && _context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // Already an absolute IRI
        if (IsAbsoluteIri(value))
        {
            return FormatIri(value);
        }

        // For types, resolve against @vocab (not @base)
        if (!string.IsNullOrEmpty(_vocabIri))
        {
            return FormatIri(_vocabIri + value);
        }

        // Fallback to @base
        if (!string.IsNullOrEmpty(_baseIri))
        {
            return FormatIri(ResolveRelativeIri(_baseIri, value));
        }

        // No @vocab or @base - cannot resolve relative IRI (e060)
        // Return null to indicate the type should be skipped
        return null!;
    }

    /// <summary>
    /// Check if a new term definition is identical to the existing protected definition.
    /// Identical redefinition of protected terms is allowed (pr23, pr15-pr19).
    /// </summary>
    private bool IsIdenticalDefinition(string term, JsonElement newValue)
    {
        // Check keyword aliases first
        if (newValue.ValueKind == JsonValueKind.String)
        {
            var newMapping = newValue.GetString() ?? "";

            // Check if term is an @id alias
            if (_idAliases.Contains(term) && newMapping == "@id")
                return true;
            // Check if term is a @type alias
            if (_typeAliases.Contains(term) && newMapping == "@type")
                return true;
            // Check if term is a @value alias
            if (_valueAliases.Contains(term) && newMapping == "@value")
                return true;
            // Check if term is a @language alias
            if (_languageAliases.Contains(term) && newMapping == "@language")
                return true;
            // Check if term is a @json alias
            if (_jsonAliases.Contains(term) && newMapping == "@json")
                return true;
            // Check if term is a @none alias
            if (_noneAliases.Contains(term) && newMapping == "@none")
                return true;

            // Check simple IRI mappings
            if (_context.TryGetValue(term, out var existingMapping) && existingMapping == newMapping)
                return true;
        }
        else if (newValue.ValueKind == JsonValueKind.Object)
        {
            // Expanded term definition - check @id property
            if (newValue.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var newId = idProp.GetString() ?? "";

                // Check if it matches keyword alias
                if (_idAliases.Contains(term) && newId == "@id")
                    return true;
                if (_typeAliases.Contains(term) && newId == "@type")
                    return true;

                // Check simple IRI mapping
                if (_context.TryGetValue(term, out var existingMapping) && existingMapping == newId)
                    return true;

                // For keyword aliases, also check if @id points to the keyword
                if (newId.StartsWith('@'))
                {
                    if (newId == "@id" && _idAliases.Contains(term)) return true;
                    if (newId == "@type" && _typeAliases.Contains(term)) return true;
                }
            }
        }

        return false;
    }
}
