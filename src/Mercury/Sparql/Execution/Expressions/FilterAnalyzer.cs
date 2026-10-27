using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// Finds the text:match FILTERs of a pattern tree whose variable can be pre-filtered by the trigram index (ADR-024).
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> an implementation detail of query execution.</para>
/// </remarks>
internal static class FilterAnalyzer
{
    #region Private Helpers

    /// <summary>
    /// Check if character is valid in a variable name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVarChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') || c == '_';

    /// <summary>True if the expression contains a logical-OR operator (<c>||</c>) outside of string literals.</summary>
    private static bool ContainsDisjunction(ReadOnlySpan<char> expr)
    {
        for (int i = 0; i + 1 < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"' || c == '\'')
            {
                char q = c; i++;
                while (i < expr.Length && expr[i] != q) { if (expr[i] == '\\') i++; i++; }
                continue;
            }
            if (c == '|' && expr[i + 1] == '|') return true;
        }
        return false;
    }

    /// <summary>
    /// Detect text:match(?var, "constant") filters suitable for trigram pre-filtering, over the pattern tree every
    /// WHERE is evaluated from (a query's and an Update's — ADR-052). Only a FILTER that is a direct child of the
    /// root group qualifies, and only for a variable that a direct-child triple of the root binds in object
    /// position: that triple is required for every solution, so narrowing its object to the term's candidates
    /// can drop no solution the FILTER would keep.
    /// </summary>
    public static List<TextMatchHint>? DetectTextMatchFilters(ref PatternArray tree, int root, ReadOnlySpan<char> source)
    {
        if (tree[root].Kind != PatternKind.GroupHeader)
            return null; // a bare sub-SELECT / SERVICE WHERE: its own filters sit in its own query

        List<TextMatchHint>? hints = null;
        var filters = tree.EnumerateDirectChildren(root);
        while (filters.MoveNext())
        {
            var filter = filters.Current;
            if (filter.Kind != PatternKind.Filter)
                continue;
            var expr = source.Slice(filter.FilterStart, filter.FilterLength);

            // Trigram pre-filter safety: a text:match inside a DISJUNCTION is not a required conjunct of the filter,
            // so restricting the variable to that one term's candidate atoms would drop rows that satisfy ANOTHER
            // branch (e.g. text:match(?n,"Stock") || text:match(?n,"Malmö") — pre-filtering ?n to "Stock" candidates
            // loses Malmö). Skip the hint for any filter containing '||' (outside string literals); FilterEvaluator
            // still evaluates text:match exactly, so the result stays correct — only the index pre-filter is forgone.
            if (ContainsDisjunction(expr))
                continue;

            if (!TryReadTextMatch(expr, out var varName, out var searchTerm))
                continue;

            bool boundAsObject = false;
            var triples = tree.EnumerateDirectChildren(root);
            while (triples.MoveNext())
            {
                var triple = triples.Current;
                if (triple.Kind != PatternKind.Triple || triple.ObjectType != TermType.Variable)
                    continue;
                var objVar = source.Slice(triple.ObjectStart + 1, triple.ObjectLength - 1); // skip '?'
                if (objVar.SequenceEqual(varName))
                {
                    boundAsObject = true;
                    break;
                }
            }
            if (!boundAsObject)
                continue;

            hints ??= new List<TextMatchHint>();
            hints.Add(new TextMatchHint { SearchTerm = searchTerm, VarName = varName.ToString() });
        }

        return hints;
    }

    /// <summary>
    /// Read <c>text:match(?var, "constant")</c> (or <c>match(…)</c>) from a FILTER expression span, with or without
    /// its enclosing parentheses. Returns the variable name without '?' and the constant without quotes.
    /// </summary>
    private static bool TryReadTextMatch(ReadOnlySpan<char> expr, out ReadOnlySpan<char> varName, out string searchTerm)
    {
        varName = default;
        searchTerm = "";

        var inner = expr.Trim();
        if (inner.Length > 0 && inner[0] == '(')
            inner = inner.Slice(1).Trim();

        int funcStart;
        if (inner.StartsWith("text:match(", StringComparison.Ordinal))
            funcStart = 11;
        else if (inner.StartsWith("match(", StringComparison.Ordinal))
            funcStart = 6;
        else
            return false;

        var args = inner.Slice(funcStart);

        // First arg must be ?variable
        if (args.Length < 2 || args[0] != '?')
            return false;
        int varEnd = 1;
        while (varEnd < args.Length && IsVarChar(args[varEnd]))
            varEnd++;
        if (varEnd <= 1)
            return false;

        // Skip whitespace and comma; the second arg must be "constant"
        int pos = varEnd;
        while (pos < args.Length && (args[pos] == ' ' || args[pos] == ','))
            pos++;
        if (pos >= args.Length || args[pos] != '"')
            return false;
        int strStart = pos + 1;
        int strEnd = strStart;
        while (strEnd < args.Length && args[strEnd] != '"')
            strEnd++;
        if (strEnd >= args.Length)
            return false;

        varName = args.Slice(1, varEnd - 1);
        searchTerm = args.Slice(strStart, strEnd - strStart).ToString();
        return true;
    }

    #endregion
}

/// <summary>
/// Describes a text:match filter suitable for trigram index pre-filtering.
/// </summary>
internal struct TextMatchHint
{
    /// <summary>The constant search string from text:match(?var, "constant").</summary>
    public string SearchTerm;

    /// <summary>The object variable name (without '?') — the tree maps trigram candidates by variable.</summary>
    public string VarName;
}
