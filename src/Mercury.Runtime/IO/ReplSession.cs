// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Result of a SPARQL query execution.
/// </summary>
public sealed class QueryResult
{
    public bool Success { get; init; }
    public ExecutionResultKind Kind { get; init; }
    public string[]? Variables { get; init; }
    public List<Dictionary<string, string>>? Rows { get; init; }
    public bool? AskResult { get; init; }
    public List<(string Subject, string Predicate, string Object)>? Triples { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// Result of a SPARQL update execution.
/// </summary>
public sealed class UpdateResult
{
    public bool Success { get; init; }
    public int AffectedCount { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// Store statistics for the :stats command.
/// </summary>
public sealed class StoreStatistics
{
    public long QuadCount { get; init; }
    public long AtomCount { get; init; }
    public long TotalBytes { get; init; }
    public long WalTxId { get; init; }
    public long WalCheckpoint { get; init; }
    public long WalSize { get; init; }
}

/// <summary>
/// The type of result from executing a REPL command.
/// </summary>
public enum ExecutionResultKind
{
    /// <summary>No result (empty input, comment only).</summary>
    Empty,

    /// <summary>SELECT query with bindings.</summary>
    Select,

    /// <summary>ASK query with boolean result.</summary>
    Ask,

    /// <summary>CONSTRUCT query with triples.</summary>
    Construct,

    /// <summary>DESCRIBE query with triples.</summary>
    Describe,

    /// <summary>SPARQL Update operation.</summary>
    Update,

    /// <summary>PREFIX declaration registered.</summary>
    PrefixRegistered,

    /// <summary>BASE declaration set.</summary>
    BaseSet,

    /// <summary>REPL command executed (e.g., :help, :clear).</summary>
    Command,

    /// <summary>Parse or execution error.</summary>
    Error
}

/// <summary>
/// Result of executing a query or command in the REPL.
/// Transport-agnostic - no dependencies on Mercury core.
/// </summary>
public sealed class ExecutionResult
{
    public ExecutionResultKind Kind { get; init; }
    public bool Success { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public TimeSpan TotalTime => ParseTime + ExecutionTime;
    public int RowCount { get; init; }
    public string[]? Variables { get; init; }
    public List<Dictionary<string, string>>? Rows { get; init; }
    public bool? AskResult { get; init; }
    public List<(string Subject, string Predicate, string Object)>? Triples { get; init; }
    public int AffectedCount { get; init; }
    public string? Message { get; init; }

    public static ExecutionResult Empty() => new()
    {
        Kind = ExecutionResultKind.Empty,
        Success = true
    };

    public static ExecutionResult Error(string message) => new()
    {
        Kind = ExecutionResultKind.Error,
        Success = false,
        Message = message
    };

    public static ExecutionResult Command(string message) => new()
    {
        Kind = ExecutionResultKind.Command,
        Success = true,
        Message = message
    };

    public static ExecutionResult PrefixRegistered(string prefix, string iri) => new()
    {
        Kind = ExecutionResultKind.PrefixRegistered,
        Success = true,
        Message = $"Prefix '{prefix}:' registered as <{iri}>"
    };

    public static ExecutionResult BaseSet(string iri) => new()
    {
        Kind = ExecutionResultKind.BaseSet,
        Success = true,
        Message = $"Base IRI set to <{iri}>"
    };
}

/// <summary>
/// Options for configuring interactive REPL behavior.
/// </summary>
public sealed class ReplOptions
{
    /// <summary>Default options for console use.</summary>
    public static ReplOptions Default { get; } = new();

    /// <summary>Options for pipe/remote connections (no color, no banner).</summary>
    public static ReplOptions Pipe { get; } = new()
    {
        UseColor = false,
        ShowBanner = false,
        Prompt = "mcp> "
    };

    /// <summary>Primary prompt shown before input.</summary>
    public string Prompt { get; init; } = "mercury> ";

    /// <summary>Continuation prompt for multi-line input.</summary>
    public string ContinuationPrompt { get; init; } = "      -> ";

    /// <summary>Whether to use ANSI color codes in output.</summary>
    public bool UseColor { get; init; } = true;

    /// <summary>Whether to show the welcome banner.</summary>
    public bool ShowBanner { get; init; } = true;

    /// <summary>Whether to detect and handle multi-line input.</summary>
    public bool EnableMultiLine { get; init; } = true;

    /// <summary>Maximum rows to display (0 = unlimited).</summary>
    public int MaxRows { get; init; } = 0;

    /// <summary>Maximum column width for table display.</summary>
    public int MaxColumnWidth { get; init; } = 50;

    /// <summary>Welcome message (null = default).</summary>
    public string? WelcomeMessage { get; init; }

    /// <summary>Goodbye message shown on exit (null = default, empty = none).</summary>
    public string? GoodbyeMessage { get; init; }
}

/// <summary>
/// Interactive REPL session for executing SPARQL queries and updates.
/// Transport-agnostic - uses injected functions for query execution.
/// </summary>
/// <remarks>
/// Maintains session state including:
/// - Registered prefixes (sticky across queries)
/// - Base IRI
/// - Query history
/// </remarks>
public sealed class ReplSession : IDisposable
{
    private readonly Func<string, QueryResult> _executeQuery;
    private readonly Func<string, UpdateResult> _executeUpdate;
    private readonly Func<StoreStatistics> _getStatistics;
    private readonly Func<IEnumerable<string>> _getNamedGraphs;

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
    /// Creates a new REPL session with injected dependencies.
    /// </summary>
    public ReplSession(
        Func<string, QueryResult> executeQuery,
        Func<string, UpdateResult> executeUpdate,
        Func<StoreStatistics> getStatistics,
        Func<IEnumerable<string>> getNamedGraphs)
    {
        _executeQuery = executeQuery ?? throw new ArgumentNullException(nameof(executeQuery));
        _executeUpdate = executeUpdate ?? throw new ArgumentNullException(nameof(executeUpdate));
        _getStatistics = getStatistics ?? throw new ArgumentNullException(nameof(getStatistics));
        _getNamedGraphs = getNamedGraphs ?? throw new ArgumentNullException(nameof(getNamedGraphs));

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
    /// Executes input (query, update, or command).
    /// </summary>
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
    /// Resets the session state (prefixes, history).
    /// </summary>
    public void Reset()
    {
        _prefixes.Clear();
        _history.Clear();
        _baseIri = string.Empty;

        foreach (var (prefix, iri) in WellKnownPrefixes)
        {
            _prefixes[prefix] = iri;
        }
    }

    /// <summary>
    /// Runs an interactive REPL loop until the user exits.
    /// </summary>
    /// <param name="input">Input stream (defaults to Console.In).</param>
    /// <param name="output">Output stream (defaults to Console.Out).</param>
    /// <param name="options">REPL options (defaults to ReplOptions.Default).</param>
    public void RunInteractive(TextReader? input = null, TextWriter? output = null, ReplOptions? options = null)
    {
        input ??= Console.In;
        output ??= Console.Out;
        options ??= ReplOptions.Default;

        var isInteractive = input == Console.In && !Console.IsInputRedirected;
        var useColor = options.UseColor && output == Console.Out && !Console.IsOutputRedirected &&
                       Environment.GetEnvironmentVariable("NO_COLOR") == null;

        var formatter = new ResultTableFormatter(output, useColor, options.MaxColumnWidth, options.MaxRows);

        // Banner
        if (options.ShowBanner && isInteractive)
        {
            output.WriteLine(options.WelcomeMessage ?? "Mercury SPARQL REPL");
            output.WriteLine("Type :help for commands, :quit to exit");
            output.WriteLine();
        }

        // Main loop
        while (true)
        {
            var line = ReadInput(input, output, options, isInteractive);
            if (line == null)
                break; // EOF

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var result = Execute(line);
            FormatResult(result, output, formatter);

            if (result.Kind == ExecutionResultKind.Command && result.Message == "EXIT")
                break;
        }

        // Goodbye
        if (isInteractive)
        {
            var goodbye = options.GoodbyeMessage ?? "Goodbye!";
            if (!string.IsNullOrEmpty(goodbye))
            {
                output.WriteLine();
                output.WriteLine(goodbye);
            }
        }
    }

    private static string? ReadInput(TextReader input, TextWriter output, ReplOptions options, bool isInteractive)
    {
        if (isInteractive)
            output.Write(options.Prompt);

        var firstLine = input.ReadLine();
        if (firstLine == null)
            return null;

        if (!options.EnableMultiLine || !NeedsMoreInput(firstLine))
            return firstLine;

        // Multi-line mode
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(firstLine);

        while (true)
        {
            if (isInteractive)
                output.Write(options.ContinuationPrompt);

            var line = input.ReadLine();
            if (line == null)
                break;

            sb.AppendLine(line);

            // Empty line or semicolon ends multi-line input
            if (string.IsNullOrWhiteSpace(line) || line.TrimEnd().EndsWith(';'))
                break;

            if (!NeedsMoreInput(sb.ToString()))
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool NeedsMoreInput(string input)
    {
        var trimmed = input.Trim();

        // Commands are single-line
        if (trimmed.StartsWith(':'))
            return false;

        // PREFIX and BASE are single-line
        if (trimmed.StartsWith("PREFIX", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("BASE", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check for unclosed braces
        int braceCount = 0;
        int parenCount = 0;
        bool inString = false;
        bool inIri = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (inString)
            {
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                    inString = false;
                continue;
            }

            if (inIri)
            {
                if (c == '>')
                    inIri = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '<': inIri = true; break;
                case '{': braceCount++; break;
                case '}': braceCount--; break;
                case '(': parenCount++; break;
                case ')': parenCount--; break;
            }
        }

        return braceCount > 0 || parenCount > 0 || inString || inIri;
    }

    private static void FormatResult(ExecutionResult result, TextWriter output, ResultTableFormatter formatter)
    {
        switch (result.Kind)
        {
            case ExecutionResultKind.Empty:
                break;

            case ExecutionResultKind.Select:
                formatter.FormatSelect(result);
                break;

            case ExecutionResultKind.Ask:
                formatter.FormatAsk(result);
                break;

            case ExecutionResultKind.Construct:
            case ExecutionResultKind.Describe:
                formatter.FormatTriples(result);
                break;

            case ExecutionResultKind.Update:
                formatter.FormatUpdate(result);
                break;

            case ExecutionResultKind.PrefixRegistered:
            case ExecutionResultKind.BaseSet:
            case ExecutionResultKind.Command:
                if (!string.IsNullOrEmpty(result.Message) && result.Message != "EXIT")
                    output.WriteLine(result.Message);
                break;

            case ExecutionResultKind.Error:
                formatter.FormatError(result);
                break;
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
            ":graphs" => ExecuteListGraphs(),
            ":count" => ExecuteCount(args),
            ":stats" or ":s" => ExecuteStats(),
            ":quit" or ":q" or ":exit" => ExecutionResult.Command("EXIT"),
            _ => ExecutionResult.Error($"Unknown command: {command}. Type :help for available commands.")
        };
    }

    private ExecutionResult ExecutePrefixDeclaration(string input)
    {
        var span = input.AsSpan();
        var rest = span[6..].TrimStart();

        var colonIdx = rest.IndexOf(':');
        if (colonIdx < 0)
            return ExecutionResult.Error("Invalid PREFIX declaration. Expected: PREFIX prefix: <iri>");

        var prefix = rest[..colonIdx].Trim().ToString();
        var afterColon = rest[(colonIdx + 1)..].Trim();

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
        var result = _executeQuery(query);

        if (!result.Success)
        {
            return new ExecutionResult
            {
                Kind = ExecutionResultKind.Error,
                Success = false,
                Message = result.ErrorMessage,
                ParseTime = result.ParseTime,
                ExecutionTime = result.ExecutionTime
            };
        }

        return new ExecutionResult
        {
            Kind = result.Kind,
            Success = true,
            Variables = result.Variables,
            Rows = result.Rows,
            RowCount = result.Rows?.Count ?? result.Triples?.Count ?? 0,
            AskResult = result.AskResult,
            Triples = result.Triples,
            ParseTime = result.ParseTime,
            ExecutionTime = result.ExecutionTime
        };
    }

    private ExecutionResult ExecuteUpdate(string update)
    {
        var result = _executeUpdate(update);

        return new ExecutionResult
        {
            Kind = ExecutionResultKind.Update,
            Success = result.Success,
            AffectedCount = result.AffectedCount,
            Message = result.ErrorMessage,
            ParseTime = result.ParseTime,
            ExecutionTime = result.ExecutionTime
        };
    }

    private string PrependPrefixes(string query)
    {
        if (_prefixes.Count == 0 && string.IsNullOrEmpty(_baseIri))
            return query;

        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(_baseIri))
        {
            sb.Append("BASE <").Append(_baseIri).AppendLine(">");
        }

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

    private ExecutionResult ExecuteListGraphs()
    {
        var graphs = _getNamedGraphs().ToList();

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
        var query = string.IsNullOrWhiteSpace(pattern)
            ? "SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o }"
            : $"SELECT (COUNT(*) AS ?count) WHERE {{ {pattern} }}";

        var result = Execute(query);
        if (result.Success && result.Rows?.Count > 0)
        {
            var row = result.Rows[0];
            string? count = null;
            if (row.TryGetValue("?count", out var c1))
                count = c1;
            else if (row.TryGetValue("count", out var c2))
                count = c2;
            else if (row.Count > 0)
                count = row.Values.First();

            return ExecutionResult.Command($"Count: {count ?? "0"}");
        }

        return result;
    }

    private ExecutionResult ExecuteStats()
    {
        var stats = _getStatistics();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("Store Statistics:");
        sb.AppendLine($"  Quads:           {stats.QuadCount:N0}");
        sb.AppendLine($"  Atoms:           {stats.AtomCount:N0}");
        sb.AppendLine($"  Storage:         {FormatBytes(stats.TotalBytes)}");
        sb.AppendLine();
        sb.AppendLine("Write-Ahead Log:");
        sb.AppendLine($"  Current TxId:    {stats.WalTxId:N0}");
        sb.AppendLine($"  Last Checkpoint: {stats.WalCheckpoint:N0}");
        sb.AppendLine($"  Log Size:        {FormatBytes(stats.WalSize)}");
        sb.AppendLine();
        sb.AppendLine("Session:");
        sb.AppendLine($"  Prefixes:        {_prefixes.Count}");
        sb.AppendLine($"  History:         {_history.Count} queries");

        return ExecutionResult.Command(sb.ToString().TrimEnd());
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:N0} {units[unit]}" : $"{size:N1} {units[unit]}";
    }

    private static string GetHelpText() => """
        Mercury REPL Commands:

          :help, :h, :?     Show this help
          :prefixes, :p     List registered prefixes
          :stats, :s        Show store statistics
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
    }
}
