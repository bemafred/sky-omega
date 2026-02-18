// SparqlTool.cs
// SPARQL tool library - testable logic extracted from CLI

using System.Text;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Tool;

/// <summary>
/// SPARQL tool - provides testable implementations of CLI functionality.
/// </summary>
public static class SparqlTool
{
    /// <summary>
    /// Runs the SPARQL tool with the specified options.
    /// </summary>
    /// <param name="options">Tool options.</param>
    /// <param name="output">Standard output writer.</param>
    /// <param name="error">Standard error writer.</param>
    /// <returns>Tool result with exit code.</returns>
    public static async Task<ToolResult> RunAsync(SparqlToolOptions options, TextWriter output, TextWriter error)
    {
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
                var count = await LoadRdfAsync(store, options.LoadFile);
                await output.WriteLineAsync($"Loaded {count:N0} triples from {options.LoadFile}");
            }

            // Execute based on mode
            if (options.Query != null)
            {
                return ExecuteQuery(store, options.Query, options.Format, options.RdfOutputFormat, output, error);
            }
            else if (options.QueryFile != null)
            {
                var query = await File.ReadAllTextAsync(options.QueryFile);
                return ExecuteQuery(store, query, options.Format, options.RdfOutputFormat, output, error);
            }
            else if (options.Explain != null)
            {
                return Explain(options.Explain, output, error);
            }
            else if (options.Repl)
            {
                return await RunReplAsync(store, options.Format, options.RdfOutputFormat, storePath, output, error, Console.In);
            }
            else if (!isTemp && options.LoadFile != null)
            {
                // Just loaded data into named store - success
                await output.WriteLineAsync($"Store ready at: {storePath}");
                return ToolResult.Ok();
            }
            else if (options.LoadFile == null && options.StorePath == null)
            {
                // No action specified
                await error.WriteLineAsync("No action specified. Use --load, --query, --repl, or --help.");
                return ToolResult.Fail("No action specified", 1);
            }

            return ToolResult.Ok();
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}");
            return ToolResult.FromException(ex);
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

    /// <summary>
    /// Loads an RDF file into the store.
    /// </summary>
    public static async Task<long> LoadRdfAsync(QuadStore store, string filePath)
    {
        return await RdfEngine.LoadFileAsync(store, filePath);
    }

    /// <summary>
    /// Executes a SPARQL query.
    /// </summary>
    public static ToolResult ExecuteQuery(QuadStore store, string queryString, OutputFormat format, RdfFormat rdfFormat, TextWriter output, TextWriter error)
    {
        var result = SparqlEngine.Query(store, queryString);
        if (!result.Success)
        {
            error.WriteLine($"Parse error: {result.ErrorMessage}");
            return ToolResult.Fail($"Parse error: {result.ErrorMessage}");
        }

        return result.Kind switch
        {
            ExecutionResultKind.Select => WriteSelectResult(result, format, output),
            ExecutionResultKind.Ask => WriteAskResult(result, output),
            ExecutionResultKind.Construct or ExecutionResultKind.Describe => WriteTripleResult(result, rdfFormat, output),
            _ => ToolResult.Fail("Unsupported query type")
        };
    }

    /// <summary>
    /// Shows the execution plan for a SPARQL query.
    /// </summary>
    public static ToolResult Explain(string queryString, TextWriter output, TextWriter error)
    {
        try
        {
            output.WriteLine(SparqlEngine.Explain(queryString));
            return ToolResult.Ok();
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return ToolResult.FromException(ex);
        }
    }

    /// <summary>
    /// Runs the interactive REPL.
    /// </summary>
    public static async Task<ToolResult> RunReplAsync(
        QuadStore store,
        OutputFormat format,
        RdfFormat rdfFormat,
        string storePath,
        TextWriter output,
        TextWriter error,
        TextReader input)
    {
        await output.WriteLineAsync("Mercury SPARQL REPL");
        await output.WriteLineAsync("Type SPARQL queries (end with ';' to execute), or use dot commands.");
        await output.WriteLineAsync("Type .help for available commands, .quit to exit.");
        await output.WriteLineAsync();

        var queryBuffer = new StringBuilder();
        var state = new ReplState { Format = format, RdfFormat = rdfFormat };

        while (true)
        {
            // Show prompt
            if (queryBuffer.Length == 0)
                await output.WriteAsync("sparql> ");
            else
                await output.WriteAsync("     -> ");

            var line = await input.ReadLineAsync();

            // Handle EOF (Ctrl+D)
            if (line == null)
            {
                await output.WriteLineAsync();
                break;
            }

            // Handle dot commands
            if (queryBuffer.Length == 0 && line.TrimStart().StartsWith("."))
            {
                var cmdResult = await HandleReplCommandAsync(store, line.Trim(), state, storePath, output, error);
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
                var queryStr = trimmed.Substring(0, trimmed.Length - 1).Trim();

                if (!string.IsNullOrWhiteSpace(queryStr))
                {
                    try
                    {
                        ExecuteQuery(store, queryStr, state.Format, state.RdfFormat, output, error);
                    }
                    catch (Exception ex)
                    {
                        await error.WriteLineAsync($"Error: {ex.Message}");
                    }
                }

                queryBuffer.Clear();
                await output.WriteLineAsync();
            }
        }

        return ToolResult.Ok();
    }

    #region Private Query Execution Methods

    private static ToolResult WriteSelectResult(QueryResult result, OutputFormat format, TextWriter output)
    {
        var variables = result.Variables ?? [];
        var rows = result.Rows ?? [];

        switch (format)
        {
            case OutputFormat.Json:
                WriteJsonResults(output, variables, rows);
                break;

            case OutputFormat.Csv:
                WriteCsvResults(output, variables, rows, delimiter: ',');
                break;

            case OutputFormat.Tsv:
                WriteCsvResults(output, variables, rows, delimiter: '\t');
                break;

            case OutputFormat.Xml:
                WriteXmlResults(output, variables, rows);
                break;

            default:
                WriteJsonResults(output, variables, rows);
                break;
        }

        return ToolResult.Ok();
    }

    private static ToolResult WriteAskResult(QueryResult result, TextWriter output)
    {
        output.WriteLine(result.AskResult == true ? "true" : "false");
        return ToolResult.Ok();
    }

    private static ToolResult WriteTripleResult(QueryResult result, RdfFormat rdfFormat, TextWriter output)
    {
        // CONSTRUCT/DESCRIBE produce triples; fall back to NTriples for quad-only formats
        var format = rdfFormat switch
        {
            RdfFormat.NTriples or RdfFormat.Turtle or RdfFormat.RdfXml => rdfFormat,
            _ => RdfFormat.NTriples
        };
        RdfEngine.WriteTriples(output, format, result.Triples ?? []);
        return ToolResult.Ok();
    }

    private static void WriteJsonResults(TextWriter writer, string[] variables, List<Dictionary<string, string>> rows)
    {
        writer.WriteLine("{");
        writer.WriteLine("  \"head\": { \"vars\": [" + string.Join(", ", variables.Select(v => $"\"{v}\"")) + "] },");
        writer.WriteLine("  \"results\": {");
        writer.WriteLine("    \"bindings\": [");

        bool first = true;
        foreach (var row in rows)
        {
            if (!first) writer.WriteLine(",");
            first = false;

            writer.Write("      { ");
            bool firstVar = true;

            foreach (var variable in variables)
            {
                if (row.TryGetValue(variable, out var valueStr))
                {
                    if (!firstVar) writer.Write(", ");
                    firstVar = false;

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

    private static void WriteCsvResults(TextWriter writer, string[] variables, List<Dictionary<string, string>> rows, char delimiter)
    {
        writer.WriteLine(string.Join(delimiter, variables));

        foreach (var row in rows)
        {
            var values = new string[variables.Length];

            for (int i = 0; i < variables.Length; i++)
            {
                values[i] = row.TryGetValue(variables[i], out var v) ? v : "";

                if (delimiter == ',' && (values[i].Contains(',') || values[i].Contains('"') || values[i].Contains('\n')))
                {
                    values[i] = "\"" + values[i].Replace("\"", "\"\"") + "\"";
                }
            }

            writer.WriteLine(string.Join(delimiter, values));
        }
    }

    private static void WriteXmlResults(TextWriter writer, string[] variables, List<Dictionary<string, string>> rows)
    {
        writer.WriteLine("<?xml version=\"1.0\"?>");
        writer.WriteLine("<sparql xmlns=\"http://www.w3.org/2005/sparql-results#\">");
        writer.WriteLine("  <head>");
        foreach (var v in variables)
            writer.WriteLine($"    <variable name=\"{EscapeXml(v)}\"/>");
        writer.WriteLine("  </head>");
        writer.WriteLine("  <results>");

        foreach (var row in rows)
        {
            writer.WriteLine("    <result>");

            foreach (var variable in variables)
            {
                if (row.TryGetValue(variable, out var valueStr))
                {
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

    private static (string type, string value, string? extra) ClassifyRdfTerm(string term)
    {
        if (term.StartsWith("<") && term.EndsWith(">"))
            return ("uri", term.Substring(1, term.Length - 2), null);

        if (term.StartsWith("_:"))
            return ("bnode", term.Substring(2), null);

        if (term.StartsWith("\""))
        {
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

    #endregion

    #region REPL Helpers

    private static async Task<int> HandleReplCommandAsync(
        QuadStore store,
        string command,
        ReplState state,
        string storePath,
        TextWriter output,
        TextWriter error)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        switch (cmd)
        {
            case ".quit":
            case ".exit":
            case ".q":
                await output.WriteLineAsync("Goodbye!");
                return -1;

            case ".help":
            case ".h":
                PrintReplHelp(output);
                break;

            case ".load":
            case ".l":
                if (string.IsNullOrEmpty(arg))
                {
                    await error.WriteLineAsync("Usage: .load <file>");
                }
                else
                {
                    try
                    {
                        var count = await LoadRdfAsync(store, arg);
                        await output.WriteLineAsync($"Loaded {count:N0} triples from {arg}");
                    }
                    catch (Exception ex)
                    {
                        await error.WriteLineAsync($"Error loading file: {ex.Message}");
                    }
                }
                break;

            case ".format":
            case ".f":
                if (string.IsNullOrEmpty(arg))
                {
                    await output.WriteLineAsync($"Current format: {state.Format.ToString().ToLowerInvariant()}");
                    await output.WriteLineAsync("Available: json, csv, tsv, xml");
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
                        await error.WriteLineAsync($"Unknown format: {arg}. Use json, csv, tsv, or xml.");
                    else
                        await output.WriteLineAsync($"Output format set to: {state.Format.ToString().ToLowerInvariant()}");
                }
                break;

            case ".rdf-format":
            case ".rf":
                if (string.IsNullOrEmpty(arg))
                {
                    await output.WriteLineAsync($"Current RDF format: {state.RdfFormat.ToString().ToLowerInvariant()}");
                    await output.WriteLineAsync("Available: nt, ttl, rdf, nq, trig");
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
                        await error.WriteLineAsync($"Unknown RDF format: {arg}. Use nt, ttl, rdf, nq, or trig.");
                    else
                        await output.WriteLineAsync($"RDF output format set to: {state.RdfFormat.ToString().ToLowerInvariant()}");
                }
                break;

            case ".count":
            case ".c":
                {
                    var (quadCount, _, _) = store.GetStatistics();
                    await output.WriteLineAsync($"Store contains {quadCount:N0} triples");
                }
                break;

            case ".clear":
                await output.WriteLineAsync("Warning: .clear is not implemented (would require store recreation)");
                break;

            case ".store":
            case ".s":
                await output.WriteLineAsync($"Store path: {storePath}");
                break;

            case ".explain":
            case ".e":
                if (string.IsNullOrEmpty(arg))
                {
                    await error.WriteLineAsync("Usage: .explain <SPARQL query>");
                }
                else
                {
                    Explain(arg, output, error);
                }
                break;

            default:
                await error.WriteLineAsync($"Unknown command: {cmd}. Type .help for available commands.");
                break;
        }

        return 0;
    }

    private static void PrintReplHelp(TextWriter output)
    {
        output.WriteLine("""
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

              DESCRIBE ?person WHERE { ?person a <http://xmlns.com/foaf/0.1/Person> };

              SELECT ?name
              WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ?name
              };
            """);
    }

    private static string EscapeJson(string s)
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

    private static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }

    #endregion
}

/// <summary>
/// REPL state for format preferences.
/// </summary>
internal class ReplState
{
    public OutputFormat Format { get; set; } = OutputFormat.Json;
    public RdfFormat RdfFormat { get; set; } = RdfFormat.NTriples;
}
