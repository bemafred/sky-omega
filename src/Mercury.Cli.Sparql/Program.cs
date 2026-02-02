// Program.cs
// Mercury SPARQL CLI - Load RDF data and execute SPARQL queries

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Results;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.TriG;

namespace SkyOmega.Mercury.Cli.Sparql;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.Error != null)
        {
            Console.Error.WriteLine($"Error: {options.Error}");
            Console.Error.WriteLine("Use --help for usage information.");
            return 1;
        }

        // Determine store path: named (persistent) or temp (auto-deleted)
        var isTemp = options.StorePath == null;
        var storePath = options.StorePath
            ?? Path.Combine(Path.GetTempPath(), $"mercury-sparql-{Guid.NewGuid():N}");

        try
        {
            // Create store if it doesn't exist (works for both temp and named)
            using var store = new QuadStore(storePath);

            // Load RDF data if specified
            if (options.LoadFile != null)
            {
                var count = await LoadRdfFileAsync(store, options.LoadFile);
                Console.WriteLine($"Loaded {count:N0} triples from {options.LoadFile}");
            }

            // Execute based on mode
            if (options.Query != null)
            {
                return ExecuteQuery(store, options.Query, options.Format, options.RdfOutputFormat);
            }
            else if (options.QueryFile != null)
            {
                var query = await File.ReadAllTextAsync(options.QueryFile);
                return ExecuteQuery(store, query, options.Format, options.RdfOutputFormat);
            }
            else if (options.Explain != null)
            {
                return ShowExplainPlan(options.Explain);
            }
            else if (options.Repl)
            {
                return await RunReplAsync(store, options.Format, options.RdfOutputFormat, storePath);
            }
            else if (!isTemp && options.LoadFile != null)
            {
                // Just loaded data into named store - success
                Console.WriteLine($"Store ready at: {storePath}");
                return 0;
            }
            else if (options.LoadFile == null && options.StorePath == null)
            {
                // No action specified
                Console.Error.WriteLine("No action specified. Use --load, --query, --repl, or --help.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            // Only cleanup temp stores, preserve named stores
            if (isTemp && Directory.Exists(storePath))
            {
                try { Directory.Delete(storePath, recursive: true); }
                catch { /* Ignore cleanup errors */ }
            }
        }
    }

    static CliOptions ParseArgs(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    return options;

                case "-l":
                case "--load":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--load requires a file path";
                        return options;
                    }
                    options.LoadFile = args[++i];
                    break;

                case "-q":
                case "--query":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--query requires a SPARQL query string";
                        return options;
                    }
                    options.Query = args[++i];
                    break;

                case "-f":
                case "--query-file":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--query-file requires a file path";
                        return options;
                    }
                    options.QueryFile = args[++i];
                    break;

                case "-s":
                case "--store":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--store requires a directory path";
                        return options;
                    }
                    options.StorePath = args[++i];
                    break;

                case "--format":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--format requires a format (json, csv, tsv, xml)";
                        return options;
                    }
                    var format = args[++i].ToLowerInvariant();
                    options.Format = format switch
                    {
                        "json" => OutputFormat.Json,
                        "csv" => OutputFormat.Csv,
                        "tsv" => OutputFormat.Tsv,
                        "xml" => OutputFormat.Xml,
                        _ => OutputFormat.Unknown
                    };
                    if (options.Format == OutputFormat.Unknown)
                    {
                        options.Error = $"Unknown format: {format}. Use json, csv, tsv, or xml.";
                        return options;
                    }
                    break;

                case "--rdf-format":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--rdf-format requires a format (nt, ttl, rdf, nq, trig)";
                        return options;
                    }
                    var rdfFormat = args[++i].ToLowerInvariant();
                    options.RdfOutputFormat = rdfFormat switch
                    {
                        "nt" or "ntriples" => RdfFormat.NTriples,
                        "ttl" or "turtle" => RdfFormat.Turtle,
                        "rdf" or "rdfxml" or "xml" => RdfFormat.RdfXml,
                        "nq" or "nquads" => RdfFormat.NQuads,
                        "trig" => RdfFormat.TriG,
                        _ => RdfFormat.Unknown
                    };
                    if (options.RdfOutputFormat == RdfFormat.Unknown)
                    {
                        options.Error = $"Unknown RDF format: {rdfFormat}. Use nt, ttl, rdf, nq, or trig.";
                        return options;
                    }
                    break;

                case "-e":
                case "--explain":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--explain requires a SPARQL query string";
                        return options;
                    }
                    options.Explain = args[++i];
                    break;

                case "-r":
                case "--repl":
                    options.Repl = true;
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        options.Error = $"Unknown option: {arg}";
                        return options;
                    }
                    // Positional argument - treat as query if no query specified
                    if (options.Query == null && options.QueryFile == null)
                    {
                        options.Query = arg;
                    }
                    break;
            }
        }

        return options;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            Mercury SPARQL CLI - Load RDF data and execute SPARQL queries

            USAGE:
                mercury-sparql [OPTIONS] [QUERY]

            OPTIONS:
                -h, --help              Show this help message
                -l, --load <FILE>       Load RDF file (Turtle, N-Triples, RDF/XML, N-Quads, TriG, JSON-LD)
                -q, --query <SPARQL>    Execute SPARQL query
                -f, --query-file <FILE> Execute SPARQL query from file
                -s, --store <PATH>      Use persistent store at path (created if doesn't exist)
                -e, --explain <SPARQL>  Show query execution plan
                -r, --repl              Start interactive REPL mode
                --format <FORMAT>       Output format for SELECT: json (default), csv, tsv, xml
                --rdf-format <FORMAT>   Output format for CONSTRUCT: nt (default), ttl, rdf, nq, trig

            EXAMPLES:
                # Load data and run query (temp store, auto-deleted)
                mercury-sparql --load data.ttl --query "SELECT * WHERE { ?s ?p ?o } LIMIT 10"

                # Use persistent named store
                mercury-sparql --store ./mydb --load data.ttl

                # Query existing store
                mercury-sparql --store ./mydb --query "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"

                # Output in CSV format
                mercury-sparql --load data.ttl -q "SELECT * WHERE { ?s ?p ?o }" --format csv

                # CONSTRUCT query with Turtle output
                mercury-sparql --load data.ttl -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format ttl

                # Show execution plan
                mercury-sparql --explain "SELECT * WHERE { ?s <http://ex.org/knows> ?o }"

                # Read query from file
                mercury-sparql --store ./mydb --query-file query.rq

                # Interactive REPL with persistent store
                mercury-sparql --store ./mydb --repl

            SUPPORTED RDF FORMATS:
                .ttl, .turtle    Turtle
                .nt, .ntriples   N-Triples
                .rdf, .xml       RDF/XML
                .nq, .nquads     N-Quads
                .trig            TriG
                .jsonld          JSON-LD
            """);
    }

    static async Task<long> LoadRdfFileAsync(QuadStore store, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var extension = Path.GetExtension(filePath);
        var format = RdfFormatNegotiator.FromExtension(extension.AsSpan());

        if (format == RdfFormat.Unknown)
            throw new NotSupportedException($"Unknown RDF format for extension: {extension}");

        await using var stream = File.OpenRead(filePath);
        long count = 0;

        // Note: Using AddCurrent for simplicity. For large files, batch API would be faster.
        switch (format)
        {
            case RdfFormat.Turtle:
                using (var parser = new TurtleStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj) =>
                    {
                        store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        count++;
                    });
                }
                break;

            case RdfFormat.NTriples:
                using (var parser = new NTriplesStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj) =>
                    {
                        store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        count++;
                    });
                }
                break;

            case RdfFormat.RdfXml:
                using (var parser = new RdfXmlStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj) =>
                    {
                        store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        count++;
                    });
                }
                break;

            case RdfFormat.NQuads:
                using (var parser = new NQuadsStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj, graph) =>
                    {
                        if (graph.IsEmpty)
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        else
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString(), graph.ToString());
                        count++;
                    });
                }
                break;

            case RdfFormat.TriG:
                using (var parser = new TriGStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj, graph) =>
                    {
                        if (graph.IsEmpty)
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        else
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString(), graph.ToString());
                        count++;
                    });
                }
                break;

            case RdfFormat.JsonLd:
                using (var parser = new JsonLdStreamParser(stream))
                {
                    await parser.ParseAsync((subject, predicate, obj, graph) =>
                    {
                        if (graph.IsEmpty)
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
                        else
                            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString(), graph.ToString());
                        count++;
                    });
                }
                break;

            default:
                throw new NotSupportedException($"Unsupported RDF format: {format}");
        }

        return count;
    }

    static int ExecuteQuery(QuadStore store, string queryString, OutputFormat format, RdfFormat rdfFormat = RdfFormat.NTriples)
    {
        // Parse the query
        var parser = new SparqlParser(queryString.AsSpan());
        Query query;

        try
        {
            query = parser.ParseQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Parse error: {ex.Message}");
            return 1;
        }

        // Handle different query types
        switch (query.Type)
        {
            case QueryType.Select:
                return ExecuteSelectQuery(store, queryString, query, format);

            case QueryType.Ask:
                return ExecuteAskQuery(store, queryString, query);

            case QueryType.Construct:
                return ExecuteConstructQuery(store, queryString, query, rdfFormat);

            case QueryType.Describe:
                Console.Error.WriteLine("DESCRIBE queries not yet supported in CLI");
                return 1;

            default:
                Console.Error.WriteLine($"Unsupported query type: {query.Type}");
                return 1;
        }
    }

    static int ExecuteSelectQuery(QuadStore store, string queryString, Query query, OutputFormat format)
    {
        store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(store, queryString.AsSpan(), query);
            var results = executor.Execute();

            // Get variable names from select clause
            var variables = GetSelectVariables(query, queryString);

            // Write results in requested format
            using var writer = Console.Out;

            switch (format)
            {
                case OutputFormat.Json:
                    WriteJsonResults(writer, variables, ref results);
                    break;

                case OutputFormat.Csv:
                    WriteCsvResults(writer, variables, ref results, delimiter: ',');
                    break;

                case OutputFormat.Tsv:
                    WriteCsvResults(writer, variables, ref results, delimiter: '\t');
                    break;

                case OutputFormat.Xml:
                    WriteXmlResults(writer, variables, ref results);
                    break;

                default:
                    WriteJsonResults(writer, variables, ref results);
                    break;
            }

            results.Dispose();
            return 0;
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    static string[] GetSelectVariables(Query query, string source)
    {
        if (query.SelectClause.SelectAll)
        {
            // For SELECT *, we need to extract variables from patterns
            // For now, return common variable names
            return ["s", "p", "o"];
        }

        var variables = new string[query.SelectClause.ProjectedVariableCount];
        for (int i = 0; i < query.SelectClause.ProjectedVariableCount; i++)
        {
            var (start, length) = query.SelectClause.GetProjectedVariable(i);
            var varSpan = source.AsSpan().Slice(start, length);
            // Remove leading ? or $
            if (varSpan.Length > 0 && (varSpan[0] == '?' || varSpan[0] == '$'))
                varSpan = varSpan.Slice(1);
            variables[i] = varSpan.ToString();
        }
        return variables;
    }

    static void WriteJsonResults(TextWriter writer, string[] variables, ref QueryResults results)
    {
        writer.WriteLine("{");
        writer.WriteLine("  \"head\": { \"vars\": [" + string.Join(", ", variables.Select(v => $"\"{v}\"")) + "] },");
        writer.WriteLine("  \"results\": {");
        writer.WriteLine("    \"bindings\": [");

        bool first = true;
        while (results.MoveNext())
        {
            if (!first) writer.WriteLine(",");
            first = false;

            writer.Write("      { ");
            bool firstVar = true;

            var bindings = results.Current;
            foreach (var variable in variables)
            {
                // Try with and without ? prefix
                var idx = bindings.FindBinding(variable.AsSpan());
                if (idx < 0)
                    idx = bindings.FindBinding(("?" + variable).AsSpan());
                if (idx >= 0)
                {
                    if (!firstVar) writer.Write(", ");
                    firstVar = false;

                    var valueStr = bindings.GetString(idx).ToString();
                    var (type, val, extra) = ClassifyRdfTerm(valueStr);

                    writer.Write($"\"{variable}\": {{ \"type\": \"{type}\", \"value\": \"{EscapeJson(val)}\"");
                    if (extra != null)
                    {
                        if (type == "literal" && extra.StartsWith("@"))
                            writer.Write($", \"xml:lang\": \"{extra.Substring(1)}\"");
                        else if (type == "literal" && extra.StartsWith("^^"))
                            writer.Write($", \"datatype\": \"{extra.Substring(2)}\"");
                    }
                    writer.Write(" }");
                }
            }

            writer.Write(" }");
        }

        writer.WriteLine();
        writer.WriteLine("    ]");
        writer.WriteLine("  }");
        writer.WriteLine("}");
    }

    static void WriteCsvResults(TextWriter writer, string[] variables, ref QueryResults results, char delimiter)
    {
        // Header row
        writer.WriteLine(string.Join(delimiter, variables));

        // Data rows
        while (results.MoveNext())
        {
            var bindings = results.Current;
            var values = new string[variables.Length];

            for (int i = 0; i < variables.Length; i++)
            {
                var idx = bindings.FindBinding(variables[i].AsSpan());
                if (idx < 0)
                    idx = bindings.FindBinding(("?" + variables[i]).AsSpan());
                values[i] = idx >= 0 ? bindings.GetString(idx).ToString() : "";

                // For CSV, escape quotes and wrap in quotes if needed
                if (delimiter == ',' && (values[i].Contains(',') || values[i].Contains('"') || values[i].Contains('\n')))
                {
                    values[i] = "\"" + values[i].Replace("\"", "\"\"") + "\"";
                }
            }

            writer.WriteLine(string.Join(delimiter, values));
        }
    }

    static void WriteXmlResults(TextWriter writer, string[] variables, ref QueryResults results)
    {
        writer.WriteLine("<?xml version=\"1.0\"?>");
        writer.WriteLine("<sparql xmlns=\"http://www.w3.org/2005/sparql-results#\">");
        writer.WriteLine("  <head>");
        foreach (var v in variables)
            writer.WriteLine($"    <variable name=\"{EscapeXml(v)}\"/>");
        writer.WriteLine("  </head>");
        writer.WriteLine("  <results>");

        while (results.MoveNext())
        {
            writer.WriteLine("    <result>");
            var bindings = results.Current;

            foreach (var variable in variables)
            {
                var idx = bindings.FindBinding(variable.AsSpan());
                if (idx < 0)
                    idx = bindings.FindBinding(("?" + variable).AsSpan());
                if (idx >= 0)
                {
                    var valueStr = bindings.GetString(idx).ToString();
                    var (type, val, extra) = ClassifyRdfTerm(valueStr);

                    writer.Write($"      <binding name=\"{EscapeXml(variable)}\">");

                    if (type == "uri")
                        writer.Write($"<uri>{EscapeXml(val)}</uri>");
                    else if (type == "bnode")
                        writer.Write($"<bnode>{EscapeXml(val)}</bnode>");
                    else
                    {
                        writer.Write("<literal");
                        if (extra != null && extra.StartsWith("@"))
                            writer.Write($" xml:lang=\"{extra.Substring(1)}\"");
                        else if (extra != null && extra.StartsWith("^^"))
                            writer.Write($" datatype=\"{extra.Substring(2)}\"");
                        writer.Write($">{EscapeXml(val)}</literal>");
                    }

                    writer.WriteLine("</binding>");
                }
            }

            writer.WriteLine("    </result>");
        }

        writer.WriteLine("  </results>");
        writer.WriteLine("</sparql>");
    }

    static (string type, string value, string? extra) ClassifyRdfTerm(string term)
    {
        if (term.StartsWith("<") && term.EndsWith(">"))
            return ("uri", term.Substring(1, term.Length - 2), null);

        if (term.StartsWith("_:"))
            return ("bnode", term.Substring(2), null);

        if (term.StartsWith("\""))
        {
            // Find closing quote (handling escapes)
            int closeQuote = term.LastIndexOf('"');
            if (closeQuote > 0)
            {
                var value = term.Substring(1, closeQuote - 1);
                var suffix = term.Substring(closeQuote + 1);
                return ("literal", value, suffix.Length > 0 ? suffix : null);
            }
        }

        return ("literal", term, null);
    }

    static int ExecuteAskQuery(QuadStore store, string queryString, Query query)
    {
        store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(store, queryString.AsSpan(), query);
            var result = executor.ExecuteAsk();
            Console.WriteLine(result ? "true" : "false");
            return 0;
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    static int ExecuteConstructQuery(QuadStore store, string queryString, Query query, RdfFormat rdfFormat)
    {
        store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(store, queryString.AsSpan(), query);
            var results = executor.ExecuteConstruct();

            // Handle RDF/XML specially (needs document start/end)
            if (rdfFormat == RdfFormat.RdfXml)
            {
                using var rdfWriter = new RdfXmlStreamWriter(Console.Out);
                rdfWriter.WriteStartDocument();
                while (results.MoveNext())
                {
                    var triple = results.Current;
                    rdfWriter.WriteTriple(triple.Subject, triple.Predicate, triple.Object);
                }
                rdfWriter.WriteEndDocument();
                results.Dispose();
                return 0;
            }

            // Create appropriate RDF writer for other formats
            using var writer = rdfFormat switch
            {
                RdfFormat.Turtle => (IDisposable)new TurtleStreamWriter(Console.Out),
                RdfFormat.NQuads => new NQuadsStreamWriter(Console.Out),
                RdfFormat.TriG => new TriGStreamWriter(Console.Out),
                _ => new NTriplesStreamWriter(Console.Out) // Default to N-Triples
            };

            // Output constructed triples
            while (results.MoveNext())
            {
                var triple = results.Current;

                // Write triple using the appropriate writer
                switch (writer)
                {
                    case NTriplesStreamWriter ntWriter:
                        ntWriter.WriteTriple(triple.Subject, triple.Predicate, triple.Object);
                        break;
                    case TurtleStreamWriter ttlWriter:
                        ttlWriter.WriteTriple(triple.Subject, triple.Predicate, triple.Object);
                        break;
                    case NQuadsStreamWriter nqWriter:
                        nqWriter.WriteQuad(triple.Subject, triple.Predicate, triple.Object, ReadOnlySpan<char>.Empty);
                        break;
                    case TriGStreamWriter trigWriter:
                        trigWriter.WriteQuad(triple.Subject, triple.Predicate, triple.Object, ReadOnlySpan<char>.Empty);
                        break;
                }
            }

            results.Dispose();
            return 0;
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    static int ShowExplainPlan(string queryString)
    {
        try
        {
            // Parse the query first
            var parser = new SparqlParser(queryString.AsSpan());
            var query = parser.ParseQuery();

            // Generate explain plan
            var explainer = new SparqlExplainer(queryString, in query);
            var plan = explainer.Explain();

            Console.WriteLine(plan.Format());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunReplAsync(QuadStore store, OutputFormat format, RdfFormat rdfFormat, string storePath)
    {
        Console.WriteLine("Mercury SPARQL REPL");
        Console.WriteLine("Type SPARQL queries (end with ';' to execute), or use dot commands.");
        Console.WriteLine("Type .help for available commands, .quit to exit.");
        Console.WriteLine();

        var queryBuffer = new StringBuilder();
        var state = new ReplState { Format = format, RdfFormat = rdfFormat };

        while (true)
        {
            // Show prompt
            if (queryBuffer.Length == 0)
                Console.Write("sparql> ");
            else
                Console.Write("     -> ");

            var line = Console.ReadLine();

            // Handle EOF (Ctrl+D)
            if (line == null)
            {
                Console.WriteLine();
                break;
            }

            // Handle dot commands
            if (queryBuffer.Length == 0 && line.TrimStart().StartsWith("."))
            {
                var cmdResult = await HandleReplCommandAsync(store, line.Trim(), state, storePath);
                if (cmdResult == -1) // quit
                    break;
                continue;
            }

            // Accumulate query lines
            queryBuffer.AppendLine(line);

            // Check if query is complete (ends with ;)
            var trimmed = queryBuffer.ToString().Trim();
            if (trimmed.EndsWith(";"))
            {
                // Remove trailing semicolon and execute
                var queryString = trimmed.Substring(0, trimmed.Length - 1).Trim();

                if (!string.IsNullOrWhiteSpace(queryString))
                {
                    try
                    {
                        ExecuteQuery(store, queryString, state.Format, state.RdfFormat);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error: {ex.Message}");
                    }
                }

                queryBuffer.Clear();
                Console.WriteLine();
            }
        }

        return 0;
    }

    static async Task<int> HandleReplCommandAsync(QuadStore store, string command, ReplState state, string storePath)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        switch (cmd)
        {
            case ".quit":
            case ".exit":
            case ".q":
                Console.WriteLine("Goodbye!");
                return -1;

            case ".help":
            case ".h":
                PrintReplHelp();
                break;

            case ".load":
            case ".l":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.Error.WriteLine("Usage: .load <file>");
                }
                else
                {
                    try
                    {
                        var count = await LoadRdfFileAsync(store, arg);
                        Console.WriteLine($"Loaded {count:N0} triples from {arg}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error loading file: {ex.Message}");
                    }
                }
                break;

            case ".format":
            case ".f":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine($"Current format: {state.Format.ToString().ToLowerInvariant()}");
                    Console.WriteLine("Available: json, csv, tsv, xml");
                }
                else
                {
                    state.Format = arg.ToLowerInvariant() switch
                    {
                        "json" => OutputFormat.Json,
                        "csv" => OutputFormat.Csv,
                        "tsv" => OutputFormat.Tsv,
                        "xml" => OutputFormat.Xml,
                        _ => state.Format
                    };
                    if (arg.ToLowerInvariant() is not ("json" or "csv" or "tsv" or "xml"))
                        Console.Error.WriteLine($"Unknown format: {arg}. Use json, csv, tsv, or xml.");
                    else
                        Console.WriteLine($"Output format set to: {state.Format.ToString().ToLowerInvariant()}");
                }
                break;

            case ".rdf-format":
            case ".rf":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine($"Current RDF format: {state.RdfFormat.ToString().ToLowerInvariant()}");
                    Console.WriteLine("Available: nt, ttl, rdf, nq, trig");
                }
                else
                {
                    state.RdfFormat = arg.ToLowerInvariant() switch
                    {
                        "nt" or "ntriples" => RdfFormat.NTriples,
                        "ttl" or "turtle" => RdfFormat.Turtle,
                        "rdf" or "rdfxml" => RdfFormat.RdfXml,
                        "nq" or "nquads" => RdfFormat.NQuads,
                        "trig" => RdfFormat.TriG,
                        _ => state.RdfFormat
                    };
                    if (arg.ToLowerInvariant() is not ("nt" or "ntriples" or "ttl" or "turtle" or "rdf" or "rdfxml" or "nq" or "nquads" or "trig"))
                        Console.Error.WriteLine($"Unknown RDF format: {arg}. Use nt, ttl, rdf, nq, or trig.");
                    else
                        Console.WriteLine($"RDF output format set to: {state.RdfFormat.ToString().ToLowerInvariant()}");
                }
                break;

            case ".count":
            case ".c":
                {
                    store.AcquireReadLock();
                    try
                    {
                        var results = store.QueryCurrent(null, null, null);
                        long count = 0;
                        while (results.MoveNext())
                            count++;
                        results.Dispose();
                        Console.WriteLine($"Store contains {count:N0} triples");
                    }
                    finally
                    {
                        store.ReleaseReadLock();
                    }
                }
                break;

            case ".clear":
                Console.WriteLine("Warning: .clear is not implemented (would require store recreation)");
                break;

            case ".store":
            case ".s":
                Console.WriteLine($"Store path: {storePath}");
                break;

            case ".explain":
            case ".e":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.Error.WriteLine("Usage: .explain <SPARQL query>");
                }
                else
                {
                    ShowExplainPlan(arg);
                }
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {cmd}. Type .help for available commands.");
                break;
        }

        return 0;
    }

    static void PrintReplHelp()
    {
        Console.WriteLine("""
            REPL Commands:
              .help, .h              Show this help
              .quit, .exit, .q       Exit REPL
              .load <file>, .l       Load RDF file into store
              .format [fmt], .f      Get/set SELECT output format (json, csv, tsv, xml)
              .rdf-format [fmt], .rf Get/set CONSTRUCT output format (nt, ttl, rdf, nq, trig)
              .count, .c             Count triples in store
              .store, .s             Show store path
              .explain <query>, .e   Show query execution plan

            Query Input:
              Type SPARQL queries across multiple lines.
              End with semicolon (;) to execute.

            Examples:
              SELECT * WHERE { ?s ?p ?o } LIMIT 10;

              CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 10;

              SELECT ?name
              WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ?name
              };
            """);
    }

    static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }
}

internal class CliOptions
{
    public string? LoadFile { get; set; }
    public string? Query { get; set; }
    public string? QueryFile { get; set; }
    public string? StorePath { get; set; }
    public string? Explain { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Json;
    public RdfFormat RdfOutputFormat { get; set; } = RdfFormat.NTriples;
    public bool ShowHelp { get; set; }
    public bool Repl { get; set; }
    public string? Error { get; set; }
}

internal enum OutputFormat
{
    Unknown,
    Json,
    Csv,
    Tsv,
    Xml
}

internal class ReplState
{
    public OutputFormat Format { get; set; } = OutputFormat.Json;
    public RdfFormat RdfFormat { get; set; } = RdfFormat.NTriples;
}
