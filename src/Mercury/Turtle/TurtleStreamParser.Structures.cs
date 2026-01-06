// TurtleStreamParser.Structures.cs
// Structural parsing: directives, collections, blank nodes, reification

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Rdf.Turtle;

public sealed partial class TurtleStreamParser
{
    /// <summary>
    /// [3] directive ::= prefixID | base | version | sparqlPrefix | sparqlBase | sparqlVersion
    /// </summary>
    /// Async because FillBufferAsync may be needed if directive spans buffer boundary
    private async ValueTask<bool> TryParseDirectiveAsync(CancellationToken cancellationToken)
    {
        SkipWhitespaceAndComments();
        
        // @prefix directive
        if (PeekString("@prefix"))
        {
            ConsumeString("@prefix");
            ParsePrefixDirective();
            return true;
        }
        
        // PREFIX directive (case-insensitive per SPARQL syntax)
        if (PeekStringIgnoreCase("PREFIX"))
        {
            ConsumeN(6); // "PREFIX".Length
            ParsePrefixDirective(requireDot: false);
            return true;
        }
        
        // @base directive
        if (PeekString("@base"))
        {
            ConsumeString("@base");
            ParseBaseDirective();
            return true;
        }
        
        // BASE directive (case-insensitive per SPARQL syntax)
        if (PeekStringIgnoreCase("BASE"))
        {
            ConsumeN(4); // "BASE".Length
            ParseBaseDirective(requireDot: false);
            return true;
        }
        
        // @version directive (RDF 1.2)
        if (PeekString("@version"))
        {
            ConsumeString("@version");
            ParseVersionDirective();
            return true;
        }
        
        // VERSION directive (RDF 1.2, case-insensitive per SPARQL syntax)
        if (PeekStringIgnoreCase("VERSION"))
        {
            ConsumeN(7); // "VERSION".Length
            ParseVersionDirective(requireDot: false);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// [4] prefixID ::= '@prefix' PNAME_NS IRIREF '.'
    /// [7] sparqlPrefix ::= "PREFIX" PNAME_NS IRIREF
    /// </summary>
    private void ParsePrefixDirective(bool requireDot = true)
    {
        SkipWhitespaceAndComments();
        
        // Parse prefix name (ends with ':')
        var prefix = ParsePrefixName();
        
        SkipWhitespaceAndComments();
        
        // Parse namespace IRI
        var namespaceIri = ParseIriRef();
        
        // Store in namespace map
        _namespaces[prefix] = namespaceIri;
        
        if (requireDot)
        {
            SkipWhitespaceAndComments();
            if (!TryConsume('.'))
                throw ParserException("Expected '.' after @prefix directive");
        }
    }
    
    private string ParsePrefixName()
    {
        _sb.Clear();

        // PN_PREFIX ::= PN_CHARS_BASE ((PN_CHARS | '.')* PN_CHARS)?
        // First character must be PN_CHARS_BASE
        var firstCh = Peek();
        if (firstCh == ':')
        {
            // Empty prefix (just ":")
            Consume();
            return string.Empty;
        }

        if (!IsPnCharsBase(firstCh))
            throw ParserException("Invalid prefix name");

        Consume();
        _sb.Append((char)firstCh);

        while (true)
        {
            var ch = Peek();

            if (ch == ':')
            {
                // Prefix cannot end with dot
                if (_sb.Length > 0 && _sb[_sb.Length - 1] == '.')
                    throw ParserException("Prefix name cannot end with '.'");
                Consume();
                return _sb.ToString();
            }

            // Allow PN_CHARS or '.' in the middle
            if (ch == '.')
            {
                Consume();
                _sb.Append('.');
                continue;
            }

            if (ch == -1 || !IsPnChars(ch))
                throw ParserException("Invalid prefix name");

            Consume();
            _sb.Append((char)ch);
        }
    }
    
    /// <summary>
    /// [5] base ::= '@base' IRIREF '.'
    /// [8] sparqlBase ::= "BASE" IRIREF
    /// </summary>
    private void ParseBaseDirective(bool requireDot = true)
    {
        SkipWhitespaceAndComments();

        var iriRef = ParseIriRef();

        // Strip angle brackets for internal storage - _baseUri is used with Uri class
        // which expects plain URI strings, not RDF IRI references
        if (iriRef.StartsWith('<') && iriRef.EndsWith('>'))
            _baseUri = iriRef[1..^1];
        else
            _baseUri = iriRef;

        if (requireDot)
        {
            SkipWhitespaceAndComments();
            if (!TryConsume('.'))
                throw ParserException("Expected '.' after @base directive");
        }
    }
    
    /// <summary>
    /// [6] version ::= '@version' VersionSpecifier '.'
    /// [9] sparqlVersion ::= "VERSION" VersionSpecifier
    /// </summary>
    private void ParseVersionDirective(bool requireDot = true)
    {
        SkipWhitespaceAndComments();
        
        // Parse version string literal
        var version = ParseStringLiteral();
        
        // Currently we just note the version but don't enforce behavior
        // A real implementation might validate features based on version

        if (!requireDot) 
            return;
        
        SkipWhitespaceAndComments();

        if (!TryConsume('.'))
            throw ParserException("Expected '.' after @version directive");
    }
    
    /// <summary>
    /// [19] blankNodePropertyList ::= '[' predicateObjectList ']'
    /// </summary>
    private string ParseBlankNodePropertyList()
    {
        if (!TryConsume('['))
            return string.Empty;

        SkipWhitespaceAndComments();

        // Check for anonymous blank node: []
        if (TryConsume(']'))
        {
            return string.Concat("_:b", _blankNodeCounter++.ToString());
        }

        // Generate blank node ID
        var blankNodeId = string.Concat("_:b", _blankNodeCounter++.ToString());

        // Parse predicate-object list with blank node as subject
        ParsePredicateObjectList(blankNodeId);

        SkipWhitespaceAndComments();

        if (!TryConsume(']'))
            throw ParserException("Expected ']' at end of blank node property list");

        return blankNodeId;
    }

    /// <summary>
    /// Parse predicate-object list and emit triples with given subject.
    /// [8] predicateObjectList ::= verb objectList (';' (verb objectList)?)*
    /// </summary>
    private void ParsePredicateObjectList(string subject)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            // Check for end
            var ch = Peek();
            if (ch == ']' || ch == '.' || ch == -1)
                break;

            // Parse verb (predicate)
            var predicate = ParseVerb();
            if (string.IsNullOrEmpty(predicate))
                break;

            // Parse object list
            ParseObjectList(subject, predicate);

            SkipWhitespaceAndComments();

            // Check for semicolon (more predicate-object pairs)
            if (!TryConsume(';'))
                break;
        }
    }

    /// <summary>
    /// Parse object list and emit triples.
    /// [9] objectList ::= object (',' object)*
    /// </summary>
    private void ParseObjectList(string subject, string predicate)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            var obj = ParseObject();
            if (string.IsNullOrEmpty(obj))
                break;

            // Emit triple
            _pendingTriples.Add(new RdfTriple(subject, predicate, obj));

            SkipWhitespaceAndComments();

            // Check for comma (more objects)
            if (!TryConsume(','))
                break;
        }
    }
    
    // RDF collection constants (with angle brackets)
    private const string RdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
    private const string RdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
    private const string RdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";

    /// <summary>
    /// [20] collection ::= '(' object* ')'
    /// Emits rdf:first and rdf:rest triples for the collection.
    /// </summary>
    private string ParseCollection()
    {
        if (!TryConsume('('))
            return string.Empty;

        SkipWhitespaceAndComments();

        // Empty collection is rdf:nil
        if (TryConsume(')'))
        {
            return RdfNil;
        }

        // Generate first blank node for collection
        var firstNode = string.Concat("_:b", _blankNodeCounter++.ToString());
        var currentNode = firstNode;

        // Parse collection items
        while (true)
        {
            SkipWhitespaceAndComments();

            if (TryConsume(')'))
                break;

            var obj = ParseObject();
            if (string.IsNullOrEmpty(obj))
                throw ParserException("Expected object in collection");

            // Emit rdf:first triple: currentNode rdf:first obj
            _pendingTriples.Add(new RdfTriple(currentNode, RdfFirst, obj));

            SkipWhitespaceAndComments();

            if (Peek() == ')')
            {
                // Last item - rest is rdf:nil
                _pendingTriples.Add(new RdfTriple(currentNode, RdfRest, RdfNil));
            }
            else
            {
                // Create next node and link
                var nextNode = string.Concat("_:b", _blankNodeCounter++.ToString());
                _pendingTriples.Add(new RdfTriple(currentNode, RdfRest, nextNode));
                currentNode = nextNode;
            }
        }

        return firstNode;
    }
    
    // RDF namespace constants for reification (with angle brackets to match parsed IRIs)
    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfStatement = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement>";
    private const string RdfSubjectProp = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#subject>";
    private const string RdfPredicateProp = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate>";
    private const string RdfObjectProp = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#object>";

    /// <summary>
    /// [29] reifiedTriple ::= '<<' rtSubject verb rtObject reifier? '>>'
    /// Emits standard RDF reification triples for queryability.
    /// </summary>
    private string ParseReifiedTriple()
    {
        if (!TryConsume('<') || !TryConsume('<'))
            return string.Empty;

        SkipWhitespaceAndComments();

        // Parse subject
        var subject = ParseReifiedTripleSubject();

        SkipWhitespaceAndComments();

        // Parse predicate
        var predicate = ParseVerb();

        SkipWhitespaceAndComments();

        // Parse object
        var obj = ParseReifiedTripleObject();

        SkipWhitespaceAndComments();

        // Optional reifier: ~ iri/blankNode
        string? reifier = null;

        if (TryConsume('~'))
        {
            SkipWhitespaceAndComments();
            reifier = ParseIri();
            if (string.IsNullOrEmpty(reifier))
            {
                reifier = ParseBlankNode();
            }
        }

        SkipWhitespaceAndComments();

        if (!TryConsume('>') || !TryConsume('>'))
            throw ParserException("Expected '>>' to close reified triple");

        // If no reifier provided, allocate blank node
        if (string.IsNullOrEmpty(reifier))
        {
            reifier = string.Concat("_:b", _blankNodeCounter++.ToString());
        }

        // Emit standard RDF reification triples
        // This allows querying reified triples with normal SPARQL patterns
        _pendingTriples.Add(new RdfTriple(reifier, RdfType, RdfStatement));
        _pendingTriples.Add(new RdfTriple(reifier, RdfSubjectProp, subject));
        _pendingTriples.Add(new RdfTriple(reifier, RdfPredicateProp, predicate));
        _pendingTriples.Add(new RdfTriple(reifier, RdfObjectProp, obj));

        // Also assert the triple itself (RDF-star "asserted" semantics)
        _pendingTriples.Add(new RdfTriple(subject, predicate, obj));

        // The reifier is what gets returned as the "term"
        // It represents the reification of the triple
        return reifier;
    }
    
    /// <summary>
    /// [30] rtSubject ::= iri | BlankNode | reifiedTriple
    /// </summary>
    private string ParseReifiedTripleSubject()
    {
        SkipWhitespaceAndComments();
        
        // Nested reified triple
        if (PeekString("<<"))
            return ParseReifiedTriple();
        
        // Blank node
        if (Peek() == '_' && PeekAhead(1) == ':')
            return ParseBlankNode();
        
        // IRI
        return ParseIri();
    }
    
    /// <summary>
    /// [31] rtObject ::= iri | BlankNode | literal | tripleTerm | reifiedTriple
    /// </summary>
    private string ParseReifiedTripleObject()
    {
        SkipWhitespaceAndComments();
        
        // Triple term
        if (PeekString("<<("))
            return ParseTripleTerm();
        
        // Nested reified triple
        if (PeekString("<<"))
            return ParseReifiedTriple();
        
        return ParseObject();
    }
    
    /// <summary>
    /// [32] tripleTerm ::= '<<(' ttSubject verb ttObject ')>>'
    /// </summary>
    private string ParseTripleTerm()
    {
        if (!TryConsume('<') || !TryConsume('<') || !TryConsume('('))
            return string.Empty;
        
        SkipWhitespaceAndComments();
        
        var subject = ParseTripleTermSubject();
        
        SkipWhitespaceAndComments();
        
        var predicate = ParseVerb();
        
        SkipWhitespaceAndComments();
        
        var obj = ParseTripleTermObject();
        
        SkipWhitespaceAndComments();
        
        if (!TryConsume(')') || !TryConsume('>') || !TryConsume('>'))
            throw ParserException("Expected ')>>' to close triple term");
        
        // Triple terms are represented as a special IRI or blank node
        // that encapsulates the triple
        return string.Concat("<<(", subject, ", ", predicate, ", ", obj, ")>>");
    }
    
    /// <summary>
    /// [33] ttSubject ::= iri | BlankNode
    /// </summary>
    private string ParseTripleTermSubject()
    {
        SkipWhitespaceAndComments();
        
        if (Peek() == '_' && PeekAhead(1) == ':')
            return ParseBlankNode();
        
        return ParseIri();
    }
    
    /// <summary>
    /// [34] ttObject ::= iri | BlankNode | literal | tripleTerm
    /// </summary>
    private string ParseTripleTermObject()
    {
        SkipWhitespaceAndComments();
        
        // Nested triple term
        if (PeekString("<<("))
            return ParseTripleTerm();
        
        return ParseObject();
    }
    
    /// <summary>
    /// [35] annotation ::= (reifier | annotationBlock)*
    /// Annotations are RDF 1.2 feature for adding metadata to triples
    /// </summary>
    private void ParseAnnotation()
    {
        SkipWhitespaceAndComments();
        
        while (true)
        {
            // Reifier: ~ iri/blankNode
            if (TryConsume('~'))
            {
                SkipWhitespaceAndComments();
                var reifier = ParseIri();
                if (string.IsNullOrEmpty(reifier))
                {
                    reifier = ParseBlankNode();
                }
                SkipWhitespaceAndComments();
                continue;
            }
            
            // Annotation block: {| predicateObjectList |}
            if (TryConsume('{') && TryConsume('|'))
            {
                ParseAnnotationBlock();
                continue;
            }
            
            break;
        }
    }
    
    /// <summary>
    /// [36] annotationBlock ::= '{|' predicateObjectList '|}'
    /// </summary>
    private void ParseAnnotationBlock()
    {
        SkipWhitespaceAndComments();
        
        // Parse predicate-object list
        // (Would emit triples with annotation context)
        
        // Find closing |}
        var depth = 1;
        while (depth > 0)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unexpected end of input in annotation block");
            
            if (ch == '{' && PeekAhead(1) == '|')
            {
                depth++;
                Consume(); Consume();
            }
            else if (ch == '|' && PeekAhead(1) == '}')
            {
                depth--;
                Consume(); Consume();
            }
            else
            {
                Consume();
            }
        }
    }

    #region Zero-GC Span-Based Structure Parsing

    /// <summary>
    /// Parse blank node property list and return span with blank node ID.
    /// </summary>
    private ReadOnlySpan<char> ParseBlankNodePropertyListSpan()
    {
        if (!TryConsume('['))
            return ReadOnlySpan<char>.Empty;

        SkipWhitespaceAndComments();

        int start = _outputOffset;

        // Check for anonymous blank node: []
        if (TryConsume(']'))
        {
            AppendToOutput("_:b".AsSpan());
            AppendToOutput(_blankNodeCounter++.ToString().AsSpan());
            return GetOutputSpan(start);
        }

        // Generate blank node ID
        AppendToOutput("_:b".AsSpan());
        AppendToOutput(_blankNodeCounter++.ToString().AsSpan());
        var blankNodeIdSpan = GetOutputSpan(start);
        // Convert to string for use as subject (triples need string subject)
        var blankNodeId = blankNodeIdSpan.ToString();

        // Parse predicate-object list with blank node as subject
        ParsePredicateObjectListZeroGCForBlankNode(blankNodeId);

        SkipWhitespaceAndComments();

        if (!TryConsume(']'))
            throw ParserException("Expected ']' at end of blank node property list");

        return blankNodeIdSpan;
    }

    /// <summary>
    /// Parse predicate-object list for blank node and emit triples via handler.
    /// </summary>
    private void ParsePredicateObjectListZeroGCForBlankNode(string subject)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            // Check for end
            var ch = Peek();
            if (ch == ']' || ch == '.' || ch == -1)
                break;

            // Parse verb (predicate)
            var predicate = ParseVerbSpan();
            if (predicate.IsEmpty)
                break;
            var predicateStr = predicate.ToString();

            // Parse object list
            ParseObjectListZeroGCForBlankNode(subject, predicateStr);

            SkipWhitespaceAndComments();

            // Check for semicolon (more predicate-object pairs)
            if (!TryConsume(';'))
                break;

            // Skip any additional consecutive semicolons
            SkipWhitespaceAndComments();
            while (TryConsume(';'))
                SkipWhitespaceAndComments();
        }
    }

    /// <summary>
    /// Parse object list for blank node and emit triples via handler.
    /// </summary>
    private void ParseObjectListZeroGCForBlankNode(string subject, string predicate)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            var obj = ParseObjectSpan();
            if (obj.IsEmpty)
                break;

            // Emit triple via handler
            _zeroGcHandler?.Invoke(subject.AsSpan(), predicate.AsSpan(), obj);

            SkipWhitespaceAndComments();

            // Check for comma (more objects)
            if (!TryConsume(','))
                break;
        }
    }

    // Span-based RDF collection constants (with angle brackets)
    private static ReadOnlySpan<char> RdfFirstSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>".AsSpan();
    private static ReadOnlySpan<char> RdfRestSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>".AsSpan();
    private static ReadOnlySpan<char> RdfNilSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>".AsSpan();

    /// <summary>
    /// Parse collection and return span with first node ID.
    /// Emits rdf:first and rdf:rest triples via the handler.
    /// </summary>
    private ReadOnlySpan<char> ParseCollectionSpan()
    {
        if (!TryConsume('('))
            return ReadOnlySpan<char>.Empty;

        SkipWhitespaceAndComments();

        // Empty collection is rdf:nil
        if (TryConsume(')'))
        {
            int nilStart = _outputOffset;
            AppendToOutput(RdfNilSpan);
            return GetOutputSpan(nilStart);
        }

        // Generate first blank node ID
        var firstNodeId = _blankNodeCounter++;
        var currentNodeId = firstNodeId;

        // Parse collection items
        while (true)
        {
            SkipWhitespaceAndComments();

            if (TryConsume(')'))
                break;

            var obj = ParseObjectSpan();
            if (obj.IsEmpty)
                throw ParserException("Expected object in collection");

            // Build current node span
            int emitNodeStart = _outputOffset;
            AppendToOutput("_:b".AsSpan());
            AppendToOutput(currentNodeId.ToString().AsSpan());
            var currentNodeSpan = GetOutputSpan(emitNodeStart);

            // Emit rdf:first triple: currentNode rdf:first obj
            _zeroGcHandler?.Invoke(currentNodeSpan, RdfFirstSpan, obj);

            SkipWhitespaceAndComments();

            if (Peek() == ')')
            {
                // Last item - rest is rdf:nil
                _zeroGcHandler?.Invoke(currentNodeSpan, RdfRestSpan, RdfNilSpan);
            }
            else
            {
                // Create next node and link
                var nextNodeId = _blankNodeCounter++;
                int nextNodeStart = _outputOffset;
                AppendToOutput("_:b".AsSpan());
                AppendToOutput(nextNodeId.ToString().AsSpan());
                var nextNode = GetOutputSpan(nextNodeStart);

                _zeroGcHandler?.Invoke(currentNodeSpan, RdfRestSpan, nextNode);

                currentNodeId = nextNodeId;
            }
        }

        // Return the first node
        int returnStart = _outputOffset;
        AppendToOutput("_:b".AsSpan());
        AppendToOutput(firstNodeId.ToString().AsSpan());
        return GetOutputSpan(returnStart);
    }

    // Span-based RDF namespace constants for zero-GC reification (with angle brackets)
    private static ReadOnlySpan<char> RdfTypeSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan();
    private static ReadOnlySpan<char> RdfStatementSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement>".AsSpan();
    private static ReadOnlySpan<char> RdfSubjectPropSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#subject>".AsSpan();
    private static ReadOnlySpan<char> RdfPredicatePropSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate>".AsSpan();
    private static ReadOnlySpan<char> RdfObjectPropSpan => "<http://www.w3.org/1999/02/22-rdf-syntax-ns#object>".AsSpan();

    /// <summary>
    /// Parse reified triple and return span with reifier ID.
    /// Emits standard RDF reification triples for queryability.
    /// </summary>
    private ReadOnlySpan<char> ParseReifiedTripleSpan()
    {
        if (!TryConsume('<') || !TryConsume('<'))
            return ReadOnlySpan<char>.Empty;

        SkipWhitespaceAndComments();

        // Parse rtSubject (IRI, BlankNode, or nested reifiedTriple)
        var subject = ParseReifiedTripleSubjectSpan();

        SkipWhitespaceAndComments();

        // Parse verb
        var predicate = ParseVerbSpan();

        SkipWhitespaceAndComments();

        // Parse rtObject (IRI, BlankNode, literal, tripleTerm, or nested reifiedTriple)
        var obj = ParseReifiedTripleObjectSpan();

        SkipWhitespaceAndComments();

        // Optional reifier: ~ iri/blankNode
        ReadOnlySpan<char> reifier = ReadOnlySpan<char>.Empty;
        int reifierStart = _outputOffset;

        if (TryConsume('~'))
        {
            SkipWhitespaceAndComments();
            reifier = ParseIriSpan();
            if (reifier.IsEmpty)
            {
                reifier = ParseBlankNodeSpan();
            }
        }

        SkipWhitespaceAndComments();

        if (!TryConsume('>') || !TryConsume('>'))
            throw ParserException("Expected '>>' to close reified triple");

        // If no reifier provided, generate blank node
        if (reifier.IsEmpty)
        {
            reifierStart = _outputOffset;
            AppendToOutput("_:b".AsSpan());
            AppendToOutput(_blankNodeCounter++.ToString().AsSpan());
            reifier = GetOutputSpan(reifierStart);
        }

        // Emit reification triples via handler if available
        if (_zeroGcHandler != null)
        {
            _zeroGcHandler(reifier, RdfTypeSpan, RdfStatementSpan);
            _zeroGcHandler(reifier, RdfSubjectPropSpan, subject);
            _zeroGcHandler(reifier, RdfPredicatePropSpan, predicate);
            _zeroGcHandler(reifier, RdfObjectPropSpan, obj);

            // Also assert the triple itself (RDF-star "asserted" semantics)
            _zeroGcHandler(subject, predicate, obj);
        }

        return reifier;
    }

    /// <summary>
    /// Parse rtSubject (IRI, BlankNode, or reifiedTriple) and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseReifiedTripleSubjectSpan()
    {
        SkipWhitespaceAndComments();

        // Nested reified triple
        if (PeekString("<<"))
            return ParseReifiedTripleSpan();

        // Blank node
        if (Peek() == '_' && PeekAhead(1) == ':')
            return ParseBlankNodeSpan();

        // IRI
        return ParseIriSpan();
    }

    /// <summary>
    /// Parse rtObject (IRI, BlankNode, literal, tripleTerm, reifiedTriple) and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseReifiedTripleObjectSpan()
    {
        SkipWhitespaceAndComments();

        // Triple term
        if (PeekString("<<("))
            return ParseTripleTermSpan();

        // Nested reified triple
        if (PeekString("<<"))
            return ParseReifiedTripleSpan();

        // Blank node
        if (Peek() == '_' && PeekAhead(1) == ':')
            return ParseBlankNodeSpan();

        var ch = Peek();

        // Literal
        if (ch == '"' || ch == '\'' || char.IsDigit((char)ch) || ch == '+' || ch == '-' || ch == '.')
            return ParseLiteralSpan();

        // Boolean
        if (PeekString("true") || PeekString("false"))
            return ParseBooleanLiteralSpan();

        // IRI
        return ParseIriSpan();
    }

    /// <summary>
    /// Parse triple term and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseTripleTermSpan()
    {
        if (!TryConsume('<') || !TryConsume('<') || !TryConsume('('))
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput("<<(".AsSpan());

        // Skip to matching ')>>' - simplified
        var depth = 1;
        while (depth > 0)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in triple term");

            if (ch == '(' && PeekAhead(-1) == '<' && PeekAhead(-2) == '<')
            {
                depth++;
            }
            else if (ch == ')' && PeekAhead(1) == '>' && PeekAhead(2) == '>')
            {
                depth--;
                if (depth == 0)
                {
                    Consume(); Consume(); Consume();
                    break;
                }
            }

            AppendToOutput((char)ch);
            Consume();
        }

        AppendToOutput(")>>".AsSpan());
        return GetOutputSpan(start);
    }

    #endregion
}
