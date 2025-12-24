// TurtleStreamParser.Structures.cs
// Structural parsing: directives, collections, blank nodes, reification

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Rdf.Turtle;

public sealed partial class TurtleStreamParser
{
    /// <summary>
    /// [3] directive ::= prefixID | base | version | sparqlPrefix | sparqlBase | sparqlVersion
    /// </summary>
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
        
        // PREFIX directive (case-insensitive)
        if (PeekString("PREFIX") || PeekString("prefix") || PeekString("Prefix"))
        {
            ConsumeString("PREFIX");
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
        
        // BASE directive (case-insensitive)
        if (PeekString("BASE") || PeekString("base") || PeekString("Base"))
        {
            ConsumeString("BASE");
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
        
        // VERSION directive (RDF 1.2, case-insensitive)
        if (PeekString("VERSION") || PeekString("version") || PeekString("Version"))
        {
            ConsumeString("VERSION");
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
        var sb = new System.Text.StringBuilder(32);
        
        while (true)
        {
            var ch = Peek();
            
            if (ch == ':')
            {
                Consume();
                return sb.ToString();
            }
            
            if (ch == -1 || !IsPnCharsBase(ch) && !IsPnChars(ch))
                throw ParserException("Invalid prefix name");
            
            Consume();
            sb.Append((char)ch);
        }
    }
    
    /// <summary>
    /// [5] base ::= '@base' IRIREF '.'
    /// [8] sparqlBase ::= "BASE" IRIREF
    /// </summary>
    private void ParseBaseDirective(bool requireDot = true)
    {
        SkipWhitespaceAndComments();
        
        _baseUri = ParseIriRef();
        
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
        
        if (requireDot)
        {
            SkipWhitespaceAndComments();
            if (!TryConsume('.'))
                throw ParserException("Expected '.' after @version directive");
        }
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
            return $"_:b{_blankNodeCounter++}";
        }
        
        // Generate blank node ID
        var blankNodeId = $"_:b{_blankNodeCounter++}";
        
        // Parse predicate-object list
        // (This would emit triples with blankNodeId as subject)
        // For simplicity, we'll skip the recursive parsing here
        
        // Find matching ']'
        var depth = 1;
        while (depth > 0)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unexpected end of input in blank node property list");
            
            if (ch == '[')
                depth++;
            else if (ch == ']')
                depth--;
            
            Consume();
        }
        
        return blankNodeId;
    }
    
    /// <summary>
    /// [20] collection ::= '(' object* ')'
    /// </summary>
    private string ParseCollection()
    {
        if (!TryConsume('('))
            return string.Empty;
        
        SkipWhitespaceAndComments();
        
        // Empty collection is rdf:nil
        if (TryConsume(')'))
        {
            return "http://www.w3.org/1999/02/22-rdf-syntax-ns#nil";
        }
        
        // Generate first blank node for collection
        var firstNode = $"_:b{_blankNodeCounter++}";
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
            
            // Emit triples for this collection item
            // currentNode rdf:first obj
            // currentNode rdf:rest nextNode (or rdf:nil if last)
            
            SkipWhitespaceAndComments();
            
            if (Peek() == ')')
            {
                // Last item - rest is rdf:nil
                continue;
            }
            
            // Create next node
            var nextNode = $"_:b{_blankNodeCounter++}";
            currentNode = nextNode;
        }
        
        return firstNode;
    }
    
    /// <summary>
    /// [29] reifiedTriple ::= '<<' rtSubject verb rtObject reifier? '>>'
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
            reifier = $"_:b{_blankNodeCounter++}";
        }
        
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
        return $"<<({subject}, {predicate}, {obj})>>";
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
}
