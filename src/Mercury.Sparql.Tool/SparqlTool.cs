// SparqlTool.cs
// SPARQL tool library - testable logic extracted from CLI

using System.Text;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Results;
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

    /// <summary>
    /// A SELECT result in <paramref name="format"/>, through the one writer of each SPARQL results format, which the
    /// HTTP endpoint uses too.
    /// </summary>
    private static ToolResult WriteSelectResult(QueryResult result, OutputFormat format, TextWriter output)
    {
        var variables = result.Variables ?? [];
        var rows = result.Rows ?? [];

        switch (format)
        {
            case OutputFormat.Xml:
                using (var xml = new SparqlXmlResultWriter(output))
                {
                    xml.WriteHead(variables);
                    foreach (var row in rows)
                        xml.WriteResult(row);
                    xml.WriteEnd();
                }
                break;

            case OutputFormat.Csv:
            case OutputFormat.Tsv:
                using (var delimited = new SparqlCsvResultWriter(output, useTsv: format == OutputFormat.Tsv))
                {
                    delimited.WriteHead(variables);
                    foreach (var row in rows)
                        delimited.WriteResult(row);
                    delimited.WriteEnd();
                }
                break;

            default:
                using (var json = new SparqlJsonResultWriter(output))
                {
                    json.WriteHead(variables);
                    foreach (var row in rows)
                        json.WriteResult(row);
                    json.WriteEnd();
                }
                break;
        }

        return ToolResult.Ok();
    }

    private static ToolResult WriteAskResult(QueryResult result, TextWriter output)
    {
        output.WriteLine(result.AskResult == true ? "true" : "false");
        return ToolResult.Ok();
    }

    /// <summary>
    /// A CONSTRUCT or DESCRIBE result in <paramref name="rdfFormat"/>, any of the six, its triples in the default
    /// graph. N-Quads and TriG used to fall back to N-Triples.
    /// </summary>
    private static ToolResult WriteTripleResult(QueryResult result, RdfFormat rdfFormat, TextWriter output)
    {
        using (var document = new RdfDocumentWriter(output, rdfFormat))
        {
            foreach (var (s, p, o) in result.Triples ?? [])
                document.Write(s, p, o, ReadOnlySpan<char>.Empty);
        }
        return ToolResult.Ok();
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
                    await output.WriteLineAsync("Available: nt, nq, ttl, trig, rdf, jsonld");
                }
                else
                {
                    // A format is named as its file extension is
                    var named = RdfEngine.DetermineFormatFromExtension(arg.AsSpan());
                    if (named == RdfFormat.Unknown)
                    {
                        await error.WriteLineAsync($"Unknown RDF format: {arg}. Use nt, nq, ttl, trig, rdf or jsonld.");
                    }
                    else
                    {
                        state.RdfFormat = named;
                        await output.WriteLineAsync($"RDF output format set to: {state.RdfFormat.ToString().ToLowerInvariant()}");
                    }
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
              .rdf-format [fmt], .rf Get/set CONSTRUCT output format (nt, nq, ttl, trig, rdf, jsonld)
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
