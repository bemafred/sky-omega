// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Repl;

/// <summary>
/// Interactive REPL session for executing SPARQL queries and updates.
/// </summary>
/// <remarks>
/// Maintains session state including:
/// - Registered prefixes (sticky across queries)
/// - Base IRI
/// - Query history
/// - Connected store
/// </remarks>
public sealed class ReplSession : IDisposable
{
    private readonly QuadStore _store;
    private readonly Dictionary<string, string> _prefixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _history = new();
    private string _baseIri = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Well-known prefixes that can be auto-registered.
    /// </summary>
    private static readonly Dictionary<string, string> WellKnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rdf"] = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        ["rdfs"] = "http://www.w3.org/2000/01/rdf-schema#",
        ["xsd"] = "http://www.w3.org/2001/XMLSchema#",
        ["owl"] = "http://www.w3.org/2002/07/owl#",
        ["foaf"] = "http://xmlns.com/foaf/0.1/",
        ["dc"] = "http://purl.org/dc/elements/1.1/",
        ["dcterms"] = "http://purl.org/dc/terms/",
        ["skos"] = "http://www.w3.org/2004/02/skos/core#",
        ["schema"] = "http://schema.org/",
        ["ex"] = "http://example.org/",
    };

    /// <summary>
    /// Creates a new REPL session connected to a store.
    /// </summary>
    /// <param name="store">The quad store to query.</param>
    public ReplSession(QuadStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        // Pre-register common prefixes
        foreach (var (prefix, iri) in WellKnownPrefixes)
        {
            _prefixes[prefix] = iri;
        }
    }

    /// <summary>
    /// Gets the registered prefixes.
    /// </summary>
    public IReadOnlyDictionary<string, string> Prefixes => _prefixes;

    /// <summary>
    /// Gets the query history.
    /// </summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Gets or sets whether to auto-suggest prefixes for unknown ones.
    /// </summary>
    public bool AutoSuggestPrefixes { get; set; } = true;

    /// <summary>
    /// Executes input (query, update, or command).
    /// </summary>
    /// <param name="input">The input string to execute.</param>
    /// <returns>The execution result.</returns>
    public ExecutionResult Execute(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ExecutionResult.Empty();

        var trimmed = input.Trim();

        // Check for REPL commands (start with :)
        if (trimmed.StartsWith(':'))
            return ExecuteCommand(trimmed);

        // Check for PREFIX declaration
        if (trimmed.StartsWith("PREFIX", StringComparison.OrdinalIgnoreCase))
            return ExecutePrefixDeclaration(trimmed);

        // Check for BASE declaration
        if (trimmed.StartsWith("BASE", StringComparison.OrdinalIgnoreCase))
            return ExecuteBaseDeclaration(trimmed);

        // Add to history
        _history.Add(input);

        // Prepend registered prefixes to input
        var fullQuery = PrependPrefixes(trimmed);

        // Determine if query or update
        if (IsUpdateOperation(trimmed))
            return ExecuteUpdate(fullQuery);
        else
            return ExecuteQuery(fullQuery);
    }

    /// <summary>
    /// Registers a prefix for use in subsequent queries.
    /// </summary>
    public void RegisterPrefix(string prefix, string iri)
    {
        _prefixes[prefix] = iri;
    }

    /// <summary>
    /// Clears all registered prefixes.
    /// </summary>
    public void ClearPrefixes()
    {
        _prefixes.Clear();
    }

    /// <summary>
    /// Resets the session state (prefixes, history).
    /// </summary>
    public void Reset()
    {
        _prefixes.Clear();
        _history.Clear();
        _baseIri = string.Empty;

        // Re-register common prefixes
        foreach (var (prefix, iri) in WellKnownPrefixes)
        {
            _prefixes[prefix] = iri;
        }
    }

    private ExecutionResult ExecuteCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        return command switch
        {
            ":help" or ":h" or ":?" => ExecutionResult.Command(GetHelpText()),
            ":prefixes" or ":p" => ExecutionResult.Command(GetPrefixesText()),
            ":clear" => ExecuteClear(),
            ":reset" => ExecuteReset(),
            ":history" => ExecutionResult.Command(GetHistoryText()),
            ":load" => ExecuteLoad(args),
            ":graphs" => ExecuteListGraphs(),
            ":count" => ExecuteCount(args),
            ":quit" or ":q" or ":exit" => ExecutionResult.Command("EXIT"),
            _ => ExecutionResult.Error($"Unknown command: {command}. Type :help for available commands.")
        };
    }

    private ExecutionResult ExecutePrefixDeclaration(string input)
    {
        // Parse: PREFIX prefix: <iri>
        var span = input.AsSpan();

        // Skip "PREFIX"
        var rest = span[6..].TrimStart();

        // Find the colon
        var colonIdx = rest.IndexOf(':');
        if (colonIdx < 0)
            return ExecutionResult.Error("Invalid PREFIX declaration. Expected: PREFIX prefix: <iri>");

        var prefix = rest[..colonIdx].Trim().ToString();
        var afterColon = rest[(colonIdx + 1)..].Trim();

        // Extract IRI
        if (afterColon.Length < 2 || afterColon[0] != '<')
            return ExecutionResult.Error("Invalid PREFIX declaration. IRI must be enclosed in <>");

        var closeIdx = afterColon.IndexOf('>');
        if (closeIdx < 0)
            return ExecutionResult.Error("Invalid PREFIX declaration. Missing closing '>'");

        var iri = afterColon[1..closeIdx].ToString();

        _prefixes[prefix] = iri;
        return ExecutionResult.PrefixRegistered(prefix, iri);
    }

    private ExecutionResult ExecuteBaseDeclaration(string input)
    {
        // Parse: BASE <iri>
        var span = input.AsSpan();
        var rest = span[4..].Trim();

        if (rest.Length < 2 || rest[0] != '<')
            return ExecutionResult.Error("Invalid BASE declaration. Expected: BASE <iri>");

        var closeIdx = rest.IndexOf('>');
        if (closeIdx < 0)
            return ExecutionResult.Error("Invalid BASE declaration. Missing closing '>'");

        _baseIri = rest[1..closeIdx].ToString();
        return ExecutionResult.BaseSet(_baseIri);
    }

    private ExecutionResult ExecuteQuery(string query)
    {
        var diagnostics = new DiagnosticBag();
        var sw = Stopwatch.StartNew();

        try
        {
            // Parse
            var parser = new SparqlParser(query.AsSpan());
            Query parsed;

            try
            {
                parsed = parser.ParseQuery();
            }
            catch (SparqlParseException ex)
            {
                sw.Stop();
                diagnostics.Add(DiagnosticCode.UnexpectedToken,
                    new SourceSpan(0, query.Length, 1, 1),
                    ex.Message.AsSpan());

                var materializedDiags = MaterializedDiagnostic.FromBag(ref diagnostics);
                diagnostics.Dispose();

                return new ExecutionResult
                {
                    Kind = ExecutionResultKind.Error,
                    Success = false,
                    Diagnostics = materializedDiags,
                    ParseTime = sw.Elapsed,
                    Message = ex.Message
                };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            // Execute based on query type
            return parsed.Type switch
            {
                QueryType.Select => ExecuteSelect(query, parsed, parseTime, ref diagnostics),
                QueryType.Ask => ExecuteAsk(query, parsed, parseTime, ref diagnostics),
                QueryType.Construct => ExecuteConstruct(query, parsed, parseTime, ref diagnostics),
                QueryType.Describe => ExecuteDescribe(query, parsed, parseTime, ref diagnostics),
                _ => ExecutionResult.Error($"Unsupported query type: {parsed.Type}")
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var materializedDiags = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
            diagnostics.Dispose();

            return new ExecutionResult
            {
                Kind = ExecutionResultKind.Error,
                Success = false,
                Diagnostics = materializedDiags,
                ParseTime = sw.Elapsed,
                Message = ex.Message
            };
        }
    }

    private ExecutionResult ExecuteSelect(string query, Query parsed, TimeSpan parseTime, ref DiagnosticBag diagnostics)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, string>>();

        // Extract variable names from query (for explicit SELECT or aggregates)
        var varNames = ExtractVariableNames(parsed, query);
        bool hasExplicitVars = varNames.Length > 0;

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.Execute();

            try
            {
                // For SELECT *, we need to get variable names from first result
                if (!hasExplicitVars && results.MoveNext())
                {
                    var bindings = results.Current;
                    varNames = ExtractVariablesFromBindings(bindings, query);

                    // Process first row by index
                    var firstRow = new Dictionary<string, string>();
                    for (int i = 0; i < bindings.Count; i++)
                    {
                        var varName = varNames.Length > i ? varNames[i] : $"?var{i}";
                        firstRow[varName] = bindings.GetString(i).ToString();
                    }
                    rows.Add(firstRow);
                }

                // Process remaining rows
                while (results.MoveNext())
                {
                    var bindings = results.Current;
                    var row = new Dictionary<string, string>();

                    if (hasExplicitVars)
                    {
                        // For explicit variables/aggregates, use FindBinding by name
                        foreach (var varName in varNames)
                        {
                            var idx = bindings.FindBinding(varName.AsSpan());
                            row[varName] = idx >= 0 ? bindings.GetString(idx).ToString() : "";
                        }
                    }
                    else
                    {
                        // For SELECT *, iterate by index
                        for (int i = 0; i < bindings.Count; i++)
                        {
                            var varName = varNames.Length > i ? varNames[i] : $"?var{i}";
                            row[varName] = bindings.GetString(i).ToString();
                        }
                    }
                    rows.Add(row);
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        sw.Stop();

        var materializedDiags = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
        diagnostics.Dispose();

        return new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Success = true,
            Diagnostics = materializedDiags,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed,
            RowCount = rows.Count,
            Variables = varNames,
            Rows = rows
        };
    }

    /// <summary>
    /// Extracts variable names from a parsed query's SELECT clause.
    /// </summary>
    private static string[] ExtractVariableNames(Query query, string source)
    {
        // For SELECT *, return empty - caller should extract from first result
        if (query.SelectClause.SelectAll)
            return [];

        // Check aggregates for aliases (e.g., SELECT (COUNT(*) AS ?count))
        var vars = new List<string>();
        for (int i = 0; i < query.SelectClause.AggregateCount; i++)
        {
            var agg = query.SelectClause.GetAggregate(i);
            if (agg.AliasLength > 0)
            {
                var alias = source.AsSpan().Slice(agg.AliasStart, agg.AliasLength).ToString();
                // Keep the ? prefix for consistency
                vars.Add(alias.StartsWith('?') ? alias : "?" + alias);
            }
        }

        return vars.Count > 0 ? vars.ToArray() : [];
    }

    /// <summary>
    /// Extracts variable names from bindings by scanning the source query.
    /// </summary>
    /// <remarks>
    /// BindingTable only stores variable name hashes, not the names themselves.
    /// We scan the source for ?varName patterns and match by hash.
    /// </remarks>
    private static string[] ExtractVariablesFromBindings(BindingTable bindings, string source)
    {
        // Collect all variable names from source with their hashes
        var knownVars = new List<(string Name, int Hash)>();
        var span = source.AsSpan();

        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '?')
            {
                int start = i;
                int end = i + 1;
                while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                    end++;

                if (end > start + 1)
                {
                    var varName = span.Slice(start, end - start).ToString();
                    var hash = ComputeVariableHash(varName.AsSpan());
                    if (!knownVars.Exists(v => v.Hash == hash))
                        knownVars.Add((varName, hash));
                }
            }
        }

        // Match bindings to known variables by hash
        var result = new string[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            var bindingHash = bindings.GetVariableHash(i);
            var found = knownVars.Find(v => v.Hash == bindingHash);
            result[i] = found.Name ?? $"?var{i}";
        }

        return result;
    }

    /// <summary>
    /// Computes a hash for a variable name (must match BindingTable.ComputeHash).
    /// </summary>
    private static int ComputeVariableHash(ReadOnlySpan<char> name)
    {
        // FNV-1a hash - must match BindingTable.ComputeHash
        uint hash = 2166136261;
        foreach (var ch in name)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    private ExecutionResult ExecuteAsk(string query, Query parsed, TimeSpan parseTime, ref DiagnosticBag diagnostics)
    {
        var sw = Stopwatch.StartNew();
        bool result;

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            result = executor.ExecuteAsk();
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        sw.Stop();

        var materializedDiags = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
        diagnostics.Dispose();

        return new ExecutionResult
        {
            Kind = ExecutionResultKind.Ask,
            Success = true,
            Diagnostics = materializedDiags,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed,
            AskResult = result
        };
    }

    private ExecutionResult ExecuteConstruct(string query, Query parsed, TimeSpan parseTime, ref DiagnosticBag diagnostics)
    {
        var sw = Stopwatch.StartNew();
        var triples = new List<(string, string, string)>();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.ExecuteConstruct();

            try
            {
                while (results.MoveNext())
                {
                    var triple = results.Current;
                    triples.Add((
                        triple.Subject.ToString(),
                        triple.Predicate.ToString(),
                        triple.Object.ToString()
                    ));
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        sw.Stop();

        var materializedDiags = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
        diagnostics.Dispose();

        return new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Success = true,
            Diagnostics = materializedDiags,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed,
            RowCount = triples.Count,
            Triples = triples
        };
    }

    private ExecutionResult ExecuteDescribe(string query, Query parsed, TimeSpan parseTime, ref DiagnosticBag diagnostics)
    {
        var sw = Stopwatch.StartNew();
        var triples = new List<(string, string, string)>();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.ExecuteDescribe();

            try
            {
                while (results.MoveNext())
                {
                    var triple = results.Current;
                    triples.Add((
                        triple.Subject.ToString(),
                        triple.Predicate.ToString(),
                        triple.Object.ToString()
                    ));
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        sw.Stop();

        var materializedDiagsD = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
        diagnostics.Dispose();

        return new ExecutionResult
        {
            Kind = ExecutionResultKind.Describe,
            Success = true,
            Diagnostics = materializedDiagsD,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed,
            RowCount = triples.Count,
            Triples = triples
        };
    }

    private ExecutionResult ExecuteUpdate(string update)
    {
        var diagnostics = new DiagnosticBag();
        var sw = Stopwatch.StartNew();

        try
        {
            var parser = new SparqlParser(update.AsSpan());
            UpdateOperation parsed;

            try
            {
                parsed = parser.ParseUpdate();
            }
            catch (SparqlParseException ex)
            {
                sw.Stop();
                var materializedDiags = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
                diagnostics.Dispose();

                return new ExecutionResult
                {
                    Kind = ExecutionResultKind.Error,
                    Success = false,
                    Diagnostics = materializedDiags,
                    ParseTime = sw.Elapsed,
                    Message = ex.Message
                };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            var executor = new UpdateExecutor(_store, update.AsSpan(), parsed);
            var result = executor.Execute();

            sw.Stop();

            var materializedDiagsU = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
            diagnostics.Dispose();

            return new ExecutionResult
            {
                Kind = ExecutionResultKind.Update,
                Success = result.Success,
                Diagnostics = materializedDiagsU,
                ParseTime = parseTime,
                ExecutionTime = sw.Elapsed,
                AffectedCount = result.AffectedCount,
                Message = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var materializedDiagsE = diagnostics.IsEmpty ? null : MaterializedDiagnostic.FromBag(ref diagnostics);
            diagnostics.Dispose();

            return new ExecutionResult
            {
                Kind = ExecutionResultKind.Error,
                Success = false,
                Diagnostics = materializedDiagsE,
                ParseTime = sw.Elapsed,
                Message = ex.Message
            };
        }
    }

    private string PrependPrefixes(string query)
    {
        if (_prefixes.Count == 0 && string.IsNullOrEmpty(_baseIri))
            return query;

        var sb = new System.Text.StringBuilder();

        // Add BASE if set
        if (!string.IsNullOrEmpty(_baseIri))
        {
            sb.Append("BASE <").Append(_baseIri).AppendLine(">");
        }

        // Add registered prefixes
        foreach (var (prefix, iri) in _prefixes)
        {
            sb.Append("PREFIX ").Append(prefix).Append(": <").Append(iri).AppendLine(">");
        }

        sb.Append(query);
        return sb.ToString();
    }

    private static bool IsUpdateOperation(string input)
    {
        var upper = input.TrimStart().ToUpperInvariant();
        return upper.StartsWith("INSERT") ||
               upper.StartsWith("DELETE") ||
               upper.StartsWith("LOAD") ||
               upper.StartsWith("CLEAR") ||
               upper.StartsWith("DROP") ||
               upper.StartsWith("CREATE") ||
               upper.StartsWith("COPY") ||
               upper.StartsWith("MOVE") ||
               upper.StartsWith("ADD") ||
               upper.StartsWith("WITH");
    }

    private ExecutionResult ExecuteClear()
    {
        _history.Clear();
        return ExecutionResult.Command("History cleared.");
    }

    private ExecutionResult ExecuteReset()
    {
        Reset();
        return ExecutionResult.Command("Session reset. Prefixes restored to defaults.");
    }

    private ExecutionResult ExecuteLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ExecutionResult.Error("Usage: :load <file-path>");

        // This would need file loading implementation
        return ExecutionResult.Error("File loading not yet implemented. Use LOAD <url> in SPARQL Update.");
    }

    private ExecutionResult ExecuteListGraphs()
    {
        var graphs = new List<string>();

        _store.AcquireReadLock();
        try
        {
            var enumerator = _store.GetNamedGraphs();
            while (enumerator.MoveNext())
            {
                graphs.Add(enumerator.Current.ToString());
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (graphs.Count == 0)
            return ExecutionResult.Command("No named graphs. Only the default graph exists.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Named graphs ({graphs.Count}):");
        foreach (var g in graphs.OrderBy(g => g))
        {
            sb.AppendLine($"  <{g}>");
        }
        return ExecutionResult.Command(sb.ToString().TrimEnd());
    }

    private ExecutionResult ExecuteCount(string pattern)
    {
        // Quick count query - use GROUP BY for proper aggregation
        var query = string.IsNullOrWhiteSpace(pattern)
            ? "SELECT (COUNT(?s) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?dummy"
            : $"SELECT (COUNT(?s) AS ?count) WHERE {{ {pattern} }} GROUP BY ?dummy";

        var result = Execute(query);
        if (result.Success && result.Rows?.Count > 0)
        {
            // Try both with and without ? prefix
            var row = result.Rows[0];
            string? count = null;
            if (row.TryGetValue("?count", out var c1))
                count = c1;
            else if (row.TryGetValue("count", out var c2))
                count = c2;
            else if (row.Count > 0)
                count = row.Values.First(); // Fallback to first value

            return ExecutionResult.Command($"Count: {count ?? "0"}");
        }

        return result;
    }

    private static string GetHelpText() => """
        Mercury REPL Commands:

          :help, :h, :?     Show this help
          :prefixes, :p     List registered prefixes
          :clear            Clear query history
          :reset            Reset session (prefixes, history)
          :history          Show query history
          :graphs           List named graphs
          :count [pattern]  Count triples (optionally matching pattern)
          :quit, :q, :exit  Exit the REPL

        SPARQL:
          PREFIX ex: <...>  Register a prefix
          BASE <...>        Set base IRI
          SELECT ...        Execute SELECT query
          ASK ...           Execute ASK query
          CONSTRUCT ...     Execute CONSTRUCT query
          DESCRIBE ...      Execute DESCRIBE query
          INSERT DATA ...   Execute INSERT
          DELETE DATA ...   Execute DELETE

        Tips:
          - Prefixes persist across queries
          - Common prefixes (rdf, rdfs, owl, etc.) are pre-registered
          - Use Tab for completion (if supported by terminal)
        """;

    private string GetPrefixesText()
    {
        if (_prefixes.Count == 0)
            return "No prefixes registered.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Registered prefixes ({_prefixes.Count}):");
        foreach (var (prefix, iri) in _prefixes.OrderBy(p => p.Key))
        {
            sb.AppendLine($"  {prefix}: <{iri}>");
        }
        return sb.ToString().TrimEnd();
    }

    private string GetHistoryText()
    {
        if (_history.Count == 0)
            return "No query history.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Query history ({_history.Count}):");
        for (int i = 0; i < _history.Count; i++)
        {
            var query = _history[i];
            if (query.Length > 60)
                query = query[..57] + "...";
            sb.AppendLine($"  [{i + 1}] {query}");
        }
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Note: We don't own the store, so we don't dispose it
    }
}
