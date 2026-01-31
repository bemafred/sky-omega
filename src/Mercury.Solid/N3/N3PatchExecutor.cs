// N3PatchExecutor.cs
// Executes N3 Patch operations against a QuadStore.
// Based on W3C Solid N3 Patch specification.
// https://solidproject.org/TR/n3-patch
// No external dependencies, only BCL.
// .NET 10 / C# 14

using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Solid.N3;

/// <summary>
/// Executes N3 Patch operations against a QuadStore.
/// Translates N3 Patch to SPARQL-like DELETE/INSERT operations.
/// </summary>
public sealed class N3PatchExecutor
{
    private readonly QuadStore _store;

    public N3PatchExecutor(QuadStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Executes an N3 Patch against a resource.
    /// </summary>
    /// <param name="patch">The parsed N3 Patch.</param>
    /// <param name="resourceUri">The target resource URI (graph).</param>
    /// <returns>The result of the patch operation.</returns>
    public N3PatchResult Execute(N3Patch patch, string resourceUri)
    {
        if (patch.IsEmpty)
        {
            return N3PatchResult.Success(0, 0);
        }

        try
        {
            // Find all variable bindings from WHERE clause
            var bindings = FindBindings(patch.Where, resourceUri);

            if (bindings.Count == 0 && patch.Where != null && patch.Where.Patterns.Count > 0)
            {
                // WHERE clause didn't match anything
                return N3PatchResult.Success(0, 0);
            }

            // If no WHERE clause, use empty binding (ground patterns only)
            if (bindings.Count == 0)
            {
                bindings.Add(new Dictionary<string, N3Term>());
            }

            int deletedCount = 0;
            int insertedCount = 0;

            _store.BeginBatch();
            try
            {
                // Apply deletes for each binding
                if (patch.Deletes != null)
                {
                    foreach (var binding in bindings)
                    {
                        deletedCount += ApplyDeletes(patch.Deletes, binding, resourceUri);
                    }
                }

                // Apply inserts for each binding
                if (patch.Inserts != null)
                {
                    foreach (var binding in bindings)
                    {
                        insertedCount += ApplyInserts(patch.Inserts, binding, resourceUri);
                    }
                }

                _store.CommitBatch();
            }
            catch
            {
                _store.RollbackBatch();
                throw;
            }

            return N3PatchResult.Success(deletedCount, insertedCount);
        }
        catch (Exception ex)
        {
            return N3PatchResult.Failure(ex.Message);
        }
    }

    private List<Dictionary<string, N3Term>> FindBindings(N3Formula? whereFormula, string graphUri)
    {
        var bindings = new List<Dictionary<string, N3Term>>();

        if (whereFormula == null || whereFormula.Patterns.Count == 0)
        {
            return bindings;
        }

        // Start with empty binding
        bindings.Add(new Dictionary<string, N3Term>());

        // Match each pattern, extending bindings
        foreach (var pattern in whereFormula.Patterns)
        {
            var newBindings = new List<Dictionary<string, N3Term>>();

            foreach (var binding in bindings)
            {
                var matches = MatchPattern(pattern, binding, graphUri);
                newBindings.AddRange(matches);
            }

            bindings = newBindings;

            if (bindings.Count == 0)
                break;
        }

        return bindings;
    }

    private List<Dictionary<string, N3Term>> MatchPattern(
        N3TriplePattern pattern,
        Dictionary<string, N3Term> currentBinding,
        string graphUri)
    {
        var results = new List<Dictionary<string, N3Term>>();

        // Resolve pattern terms with current bindings
        var subjectTerm = ResolveTerm(pattern.Subject, currentBinding);
        var predicateTerm = ResolveTerm(pattern.Predicate, currentBinding);
        var objectTerm = ResolveTerm(pattern.Object, currentBinding);

        // Convert to store query format
        var subject = subjectTerm.IsGround ? subjectTerm.ToRdfString() : null;
        var predicate = predicateTerm.IsGround ? predicateTerm.ToRdfString() : null;
        var obj = objectTerm.IsGround ? objectTerm.ToRdfString() : null;

        // Query the store
        _store.AcquireReadLock();
        try
        {
            var queryResults = _store.QueryCurrent(subject, predicate, obj, graphUri);

            while (queryResults.MoveNext())
            {
                var newBinding = new Dictionary<string, N3Term>(currentBinding);
                var current = queryResults.Current;

                // Bind variables from results
                if (pattern.Subject.IsVariable)
                {
                    newBinding[pattern.Subject.Value] = ParseTermFromStore(current.Subject.ToString());
                }

                if (pattern.Predicate.IsVariable)
                {
                    newBinding[pattern.Predicate.Value] = ParseTermFromStore(current.Predicate.ToString());
                }

                if (pattern.Object.IsVariable)
                {
                    newBinding[pattern.Object.Value] = ParseTermFromStore(current.Object.ToString());
                }

                results.Add(newBinding);
            }

            queryResults.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        return results;
    }

    private static N3Term ResolveTerm(N3Term term, Dictionary<string, N3Term> binding)
    {
        if (term.IsVariable && binding.TryGetValue(term.Value, out var bound))
        {
            return bound;
        }
        return term;
    }

    private static N3Term ParseTermFromStore(string storeValue)
    {
        if (string.IsNullOrEmpty(storeValue))
            return N3Term.Literal("");

        // IRI
        if (storeValue.StartsWith("<") && storeValue.EndsWith(">"))
        {
            return N3Term.Iri(storeValue.Substring(1, storeValue.Length - 2));
        }

        // Blank node
        if (storeValue.StartsWith("_:"))
        {
            return N3Term.BlankNode(storeValue.Substring(2));
        }

        // Literal with language tag or datatype
        if (storeValue.StartsWith("\""))
        {
            var lastQuote = storeValue.LastIndexOf('"');
            if (lastQuote <= 0)
                return N3Term.Literal(storeValue);

            var value = storeValue.Substring(1, lastQuote - 1);
            var suffix = storeValue.Substring(lastQuote + 1);

            if (suffix.StartsWith("@"))
            {
                return N3Term.Literal(value, language: suffix.Substring(1));
            }
            else if (suffix.StartsWith("^^"))
            {
                var datatype = suffix.Substring(2);
                if (datatype.StartsWith("<") && datatype.EndsWith(">"))
                    datatype = datatype.Substring(1, datatype.Length - 2);
                return N3Term.Literal(value, datatype: datatype);
            }

            return N3Term.Literal(value);
        }

        // Default: treat as IRI without brackets
        return N3Term.Iri(storeValue);
    }

    private int ApplyDeletes(N3Formula deletes, Dictionary<string, N3Term> binding, string graphUri)
    {
        int count = 0;

        foreach (var pattern in deletes.Patterns)
        {
            var subject = ResolveTerm(pattern.Subject, binding);
            var predicate = ResolveTerm(pattern.Predicate, binding);
            var obj = ResolveTerm(pattern.Object, binding);

            if (!subject.IsGround || !predicate.IsGround || !obj.IsGround)
            {
                // Skip patterns with unbound variables
                continue;
            }

            _store.DeleteCurrentBatched(
                subject.ToRdfString(),
                predicate.ToRdfString(),
                obj.ToRdfString(),
                graphUri);
            count++;
        }

        return count;
    }

    private int ApplyInserts(N3Formula inserts, Dictionary<string, N3Term> binding, string graphUri)
    {
        int count = 0;

        foreach (var pattern in inserts.Patterns)
        {
            var subject = ResolveTerm(pattern.Subject, binding);
            var predicate = ResolveTerm(pattern.Predicate, binding);
            var obj = ResolveTerm(pattern.Object, binding);

            if (!subject.IsGround || !predicate.IsGround || !obj.IsGround)
            {
                // Skip patterns with unbound variables
                continue;
            }

            _store.AddCurrentBatched(
                subject.ToRdfString(),
                predicate.ToRdfString(),
                obj.ToRdfString(),
                graphUri);
            count++;
        }

        return count;
    }
}

/// <summary>
/// Result of an N3 Patch operation.
/// </summary>
public readonly struct N3PatchResult
{
    /// <summary>
    /// Whether the patch succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Number of triples deleted.
    /// </summary>
    public int DeletedCount { get; }

    /// <summary>
    /// Number of triples inserted.
    /// </summary>
    public int InsertedCount { get; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; }

    private N3PatchResult(bool success, int deleted, int inserted, string? error)
    {
        IsSuccess = success;
        DeletedCount = deleted;
        InsertedCount = inserted;
        ErrorMessage = error;
    }

    public static N3PatchResult Success(int deleted, int inserted)
    {
        return new N3PatchResult(true, deleted, inserted, null);
    }

    public static N3PatchResult Failure(string message)
    {
        return new N3PatchResult(false, 0, 0, message);
    }
}
