using System;
using System.Runtime.CompilerServices;
using System.Threading;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// The terms an expansion adds to a query that are not in its source, marked by a negative <c>Term.Start</c>:
/// <list type="bullet">
/// <item>-1 to -8: the rdf: IRIs that reification and collections use.</item>
/// <item><see cref="JoinVariable"/>: the variables that join an expansion's triples (a quoted triple's reifier, a
/// sequence path's intermediate nodes, a blank-node property list's or a collection's nodes). They are numbered per
/// query with no limit. Their names have no sigil and contain <c>#</c>, which no variable or blank-node label can,
/// so they never meet a query's own variables, are never projected and are not in scope.</item>
/// <item><see cref="ListNode"/>: a CONSTRUCT template collection's nodes, a new blank node per solution.</item>
/// </list>
/// </summary>
internal static class SyntheticTerms
{
    // RDF namespace IRIs for reification patterns
    public const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    public const string RdfStatement = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement>";
    public const string RdfSubject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#subject>";
    public const string RdfPredicate = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate>";
    public const string RdfObject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#object>";

    // RDF namespace IRIs for RDF collections (lists)
    public const string RdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
    public const string RdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
    public const string RdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";

    private const int JoinVariableBase = -1_000;
    private const int ListNodeBase = -1_000_000_000;

    /// <summary>The offset of join variable <paramref name="index"/> (0, 1, 2, … per query).</summary>
    public static int JoinVariable(int index)
    {
        if (index < 0 || index >= JoinVariableBase - ListNodeBase)
            throw new SparqlParseException("A query expands into too many joined triples");
        return JoinVariableBase - index;
    }

    /// <summary>The offset of CONSTRUCT template list node <paramref name="index"/> (0, 1, 2, … per template).</summary>
    public static int ListNode(int index)
    {
        if (index < 0 || index >= ListNodeBase - int.MinValue)
            throw new SparqlParseException("A CONSTRUCT template has too many collection nodes");
        return ListNodeBase - index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSynthetic(int start) => start < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsListNodeOffset(int start) => start <= ListNodeBase;

    /// <summary>The index of the list node at <paramref name="start"/>.</summary>
    public static int ListNodeIndex(int start) => ListNodeBase - start;

    /// <summary>The rdf: IRI at <paramref name="start"/> (-1 to -8); empty for any other offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> GetSyntheticIri(int start)
    {
        return start switch
        {
            -1 => RdfType.AsSpan(),
            -2 => RdfStatement.AsSpan(),
            -3 => RdfSubject.AsSpan(),
            -4 => RdfPredicate.AsSpan(),
            -5 => RdfObject.AsSpan(),
            -6 => RdfFirst.AsSpan(),
            -7 => RdfRest.AsSpan(),
            -8 => RdfNil.AsSpan(),
            _ => ReadOnlySpan<char>.Empty
        };
    }

    // Join variable names, made once and shared: the scan looks a name up for every row
    private static string[] _joinVariableNames = [];
    private static readonly Lock NamesLock = new();

    /// <summary>The name of the join variable at <paramref name="start"/>; empty for any other offset.</summary>
    public static ReadOnlySpan<char> GetSyntheticVarName(int start)
    {
        if (start > JoinVariableBase || start <= ListNodeBase)
            return ReadOnlySpan<char>.Empty;
        int index = JoinVariableBase - start;
        var names = Volatile.Read(ref _joinVariableNames);
        if (index < names.Length)
            return names[index];
        lock (NamesLock)
        {
            names = _joinVariableNames;
            if (index >= names.Length)
            {
                var grown = new string[Math.Max(64, Math.Max(index + 1, 2 * names.Length))];
                names.CopyTo(grown, 0);
                for (int i = names.Length; i < grown.Length; i++)
                    grown[i] = "#j" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Volatile.Write(ref _joinVariableNames, grown);
                names = grown;
            }
            return names[index];
        }
    }
}
