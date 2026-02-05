// TurtleTool.cs
// Turtle tool library - testable logic extracted from CLI

using System.Diagnostics;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.TriG;

namespace SkyOmega.Mercury.Turtle.Tool;

/// <summary>
/// Turtle tool - provides testable implementations of CLI functionality.
/// </summary>
public static class TurtleTool
{
    /// <summary>
    /// Runs the Turtle tool with the specified options.
    /// </summary>
    /// <param name="options">Tool options.</param>
    /// <param name="output">Standard output writer.</param>
    /// <param name="error">Standard error writer.</param>
    /// <returns>Tool result with exit code.</returns>
    public static async Task<ToolResult> RunAsync(TurtleToolOptions options, TextWriter output, TextWriter error)
    {
        // Demo mode - no arguments, run examples
        if (options.InputFile == null && !options.Benchmark)
        {
            return await DemoAsync(output, error);
        }

        try
        {
            if (options.Validate)
            {
                return await ValidateAsync(options.InputFile!, output, error);
            }
            else if (options.Stats)
            {
                return await StatisticsAsync(options.InputFile!, output, error);
            }
            else if (options.Benchmark)
            {
                return await BenchmarkAsync(options.InputFile, options.TripleCount, output, error);
            }
            else if (options.StorePath != null)
            {
                return await LoadAsync(options.InputFile!, options.StorePath, output, error);
            }
            else if (options.OutputFile != null || options.OutputFormat != RdfFormat.Unknown)
            {
                return await ConvertAsync(options.InputFile!, options.OutputFile, options.OutputFormat, output, error);
            }
            else
            {
                // Default: parse and print triples
                return await ParseAsync(options.InputFile!, output, error);
            }
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}");
            return ToolResult.FromException(ex);
        }
    }

    /// <summary>
    /// Validates Turtle syntax.
    /// </summary>
    public static async Task<ToolResult> ValidateAsync(string filePath, TextWriter output, TextWriter error)
    {
        if (!File.Exists(filePath))
        {
            await error.WriteLineAsync($"File not found: {filePath}");
            return ToolResult.Fail($"File not found: {filePath}");
        }

        await output.WriteLineAsync($"Validating: {filePath}");

        await using var stream = File.OpenRead(filePath);
        using var parser = new TurtleStreamParser(stream);

        long tripleCount = 0;

        try
        {
            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
            });

            await output.WriteLineAsync($"Valid Turtle: {tripleCount:N0} triples");
            return ToolResult.Ok();
        }
        catch (InvalidDataException ex)
        {
            await error.WriteLineAsync($"Syntax error: {ex.Message}");
            return ToolResult.Fail($"Syntax error: {ex.Message}");
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}");
            return ToolResult.FromException(ex);
        }
    }

    /// <summary>
    /// Shows statistics about a Turtle file.
    /// </summary>
    public static async Task<ToolResult> StatisticsAsync(string filePath, TextWriter output, TextWriter error)
    {
        if (!File.Exists(filePath))
        {
            await error.WriteLineAsync($"File not found: {filePath}");
            return ToolResult.Fail($"File not found: {filePath}");
        }

        await output.WriteLineAsync($"Analyzing: {filePath}");
        await output.WriteLineAsync();

        await using var stream = File.OpenRead(filePath);
        using var parser = new TurtleStreamParser(stream);

        long tripleCount = 0;
        var predicateCounts = new Dictionary<string, long>();
        var subjects = new HashSet<string>();
        var objects = new HashSet<string>();

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            tripleCount++;

            var predStr = predicate.ToString();
            predicateCounts.TryGetValue(predStr, out var count);
            predicateCounts[predStr] = count + 1;

            subjects.Add(subject.ToString());
            objects.Add(obj.ToString());
        });

        await output.WriteLineAsync($"Total triples:    {tripleCount:N0}");
        await output.WriteLineAsync($"Unique subjects:  {subjects.Count:N0}");
        await output.WriteLineAsync($"Unique objects:   {objects.Count:N0}");
        await output.WriteLineAsync($"Unique predicates: {predicateCounts.Count:N0}");
        await output.WriteLineAsync();

        await output.WriteLineAsync("Predicate distribution:");
        var sortedPredicates = new List<KeyValuePair<string, long>>(predicateCounts);
        sortedPredicates.Sort((a, b) => b.Value.CompareTo(a.Value));

        var maxToShow = Math.Min(10, sortedPredicates.Count);
        for (int i = 0; i < maxToShow; i++)
        {
            var kv = sortedPredicates[i];
            var pct = (double)kv.Value / tripleCount * 100;
            await output.WriteLineAsync($"  {kv.Value,8:N0} ({pct,5:F1}%) {TruncateUri(kv.Key, 50)}");
        }

        if (sortedPredicates.Count > maxToShow)
        {
            await output.WriteLineAsync($"  ... and {sortedPredicates.Count - maxToShow} more predicates");
        }

        return ToolResult.Ok();
    }

    /// <summary>
    /// Runs a performance benchmark.
    /// </summary>
    public static async Task<ToolResult> BenchmarkAsync(string? inputFile, int tripleCount, TextWriter output, TextWriter error)
    {
        await output.WriteLineAsync("=== Mercury Turtle Parser Benchmark ===\n");

        string turtle;
        string source;

        if (inputFile != null)
        {
            if (!File.Exists(inputFile))
            {
                await error.WriteLineAsync($"File not found: {inputFile}");
                return ToolResult.Fail($"File not found: {inputFile}");
            }
            turtle = await File.ReadAllTextAsync(inputFile);
            source = inputFile;
        }
        else
        {
            turtle = GenerateLargeTurtleDocument(tripleCount);
            source = $"generated ({tripleCount:N0} triples)";
        }

        await output.WriteLineAsync($"Source: {source}");
        await output.WriteLineAsync($"Size: {turtle.Length / 1024:N0} KB");
        await output.WriteLineAsync();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream, bufferSize: 16384);

        // Force GC and get baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memBefore = GC.GetTotalMemory(forceFullCollection: false);

        var sw = Stopwatch.StartNew();
        long parsedCount = 0;

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            parsedCount++;
        });

        sw.Stop();

        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var memAfter = GC.GetTotalMemory(forceFullCollection: false);

        await output.WriteLineAsync("Results:");
        await output.WriteLineAsync($"  Triples:     {parsedCount:N0}");
        await output.WriteLineAsync($"  Time:        {sw.ElapsedMilliseconds:N0} ms");
        await output.WriteLineAsync($"  Throughput:  {parsedCount / sw.Elapsed.TotalSeconds:N0} triples/sec");
        await output.WriteLineAsync($"  Throughput:  {turtle.Length / 1024.0 / sw.Elapsed.TotalSeconds:N2} KB/sec");
        await output.WriteLineAsync();
        await output.WriteLineAsync("GC Collections:");
        await output.WriteLineAsync($"  Gen 0:       {gen0After - gen0Before}");
        await output.WriteLineAsync($"  Gen 1:       {gen1After - gen1Before}");
        await output.WriteLineAsync($"  Gen 2:       {gen2After - gen2Before}");
        await output.WriteLineAsync($"  Memory:      {(memAfter - memBefore) / 1024.0:+#,##0.00;-#,##0.00;0} KB");

        if (gen0After - gen0Before == 0 && gen1After - gen1Before == 0 && gen2After - gen2Before == 0)
        {
            await output.WriteLineAsync("\nZero GC collections during parse!");
        }

        return ToolResult.Ok();
    }

    /// <summary>
    /// Loads Turtle file into a QuadStore.
    /// </summary>
    public static async Task<ToolResult> LoadAsync(string inputFile, string storePath, TextWriter output, TextWriter error)
    {
        if (!File.Exists(inputFile))
        {
            await error.WriteLineAsync($"File not found: {inputFile}");
            return ToolResult.Fail($"File not found: {inputFile}");
        }

        await output.WriteLineAsync($"Loading: {inputFile}");
        await output.WriteLineAsync($"Store: {storePath}");

        using var store = new QuadStore(storePath);
        await using var stream = File.OpenRead(inputFile);
        using var parser = new TurtleStreamParser(stream);

        var sw = Stopwatch.StartNew();
        long count = 0;

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            store.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
            count++;

            if (count % 100_000 == 0)
            {
                output.WriteLine($"  Loaded {count:N0} triples...");
            }
        });

        sw.Stop();

        await output.WriteLineAsync();
        await output.WriteLineAsync($"Loaded {count:N0} triples in {sw.ElapsedMilliseconds:N0} ms");
        await output.WriteLineAsync($"Throughput: {count / sw.Elapsed.TotalSeconds:N0} triples/sec");
        await output.WriteLineAsync($"Store ready at: {storePath}");

        return ToolResult.Ok();
    }

    /// <summary>
    /// Converts Turtle to another RDF format.
    /// </summary>
    public static async Task<ToolResult> ConvertAsync(string inputFile, string? outputFile, RdfFormat outputFormat, TextWriter output, TextWriter error)
    {
        if (!File.Exists(inputFile))
        {
            await error.WriteLineAsync($"File not found: {inputFile}");
            return ToolResult.Fail($"File not found: {inputFile}");
        }

        // Determine output format from file extension if not specified
        if (outputFormat == RdfFormat.Unknown && outputFile != null)
        {
            var ext = Path.GetExtension(outputFile);
            outputFormat = RdfFormatNegotiator.FromExtension(ext.AsSpan());
        }

        // Default to N-Triples
        if (outputFormat == RdfFormat.Unknown)
        {
            outputFormat = RdfFormat.NTriples;
        }

        await using var inputStream = File.OpenRead(inputFile);
        using var parser = new TurtleStreamParser(inputStream);

        TextWriter outputWriter = outputFile != null
            ? new StreamWriter(outputFile, append: false, Encoding.UTF8)
            : output;

        try
        {
            long count = 0;

            switch (outputFormat)
            {
                case RdfFormat.NTriples:
                    await using (var writer = new NTriplesStreamWriter(outputWriter))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteTriple(subject, predicate, obj);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.NQuads:
                    await using (var writer = new NQuadsStreamWriter(outputWriter))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.TriG:
                    await using (var writer = new TriGStreamWriter(outputWriter))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.Turtle:
                    await using (var writer = new TurtleStreamWriter(outputWriter))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteTriple(subject, predicate, obj);
                            count++;
                        });
                    }
                    break;

                default:
                    await error.WriteLineAsync($"Output format not supported: {outputFormat}");
                    return ToolResult.Fail($"Output format not supported: {outputFormat}");
            }

            if (outputFile != null)
            {
                await error.WriteLineAsync($"Converted {count:N0} triples to {outputFile}");
            }

            return ToolResult.Ok();
        }
        finally
        {
            if (outputFile != null)
            {
                await outputWriter.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Parses and prints triples.
    /// </summary>
    public static async Task<ToolResult> ParseAsync(string inputFile, TextWriter output, TextWriter error)
    {
        if (!File.Exists(inputFile))
        {
            await error.WriteLineAsync($"File not found: {inputFile}");
            return ToolResult.Fail($"File not found: {inputFile}");
        }

        await using var stream = File.OpenRead(inputFile);
        using var parser = new TurtleStreamParser(stream);

        long count = 0;

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            count++;
            output.WriteLine($"{subject} {predicate} {obj} .");
        });

        await error.WriteLineAsync($"Parsed {count:N0} triples");
        return ToolResult.Ok();
    }

    /// <summary>
    /// Runs demo examples.
    /// </summary>
    public static async Task<ToolResult> DemoAsync(TextWriter output, TextWriter error)
    {
        await output.WriteLineAsync("=== Mercury Turtle Parser Demo ===\n");
        await output.WriteLineAsync("Run with --help for full usage information.\n");

        // Example 1: Parse from string
        await output.WriteLineAsync("Example 1: Parse Turtle from string\n");

        const string turtle = """
                              @prefix foaf: <http://xmlns.com/foaf/0.1/> .
                              @prefix ex: <http://example.org/> .

                              ex:alice foaf:name "Alice" ;
                                       foaf:knows ex:bob ;
                                       foaf:age 30 .

                              ex:bob foaf:name "Bob" ;
                                     foaf:knows ex:alice .
                              """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);

        var count = 0;

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            count++;
            output.WriteLine($"  {count}. {subject} {predicate} {obj}");
        });

        await output.WriteLineAsync($"\nParsed {count} triples\n");

        // Example 2: Quick benchmark
        await output.WriteLineAsync("Example 2: Quick benchmark (1000 triples)\n");

        var benchTurtle = GenerateLargeTurtleDocument(1000);
        using var benchStream = new MemoryStream(Encoding.UTF8.GetBytes(benchTurtle));
        using var benchParser = new TurtleStreamParser(benchStream);

        var sw = Stopwatch.StartNew();
        long benchCount = 0;

        await benchParser.ParseAsync((s, p, o) => benchCount++);

        sw.Stop();

        await output.WriteLineAsync($"  Parsed {benchCount:N0} triples in {sw.ElapsedMilliseconds} ms");
        await output.WriteLineAsync($"  Throughput: {benchCount / sw.Elapsed.TotalSeconds:N0} triples/sec\n");

        await output.WriteLineAsync("Try: mercury-turtle --benchmark --count 100000");

        return ToolResult.Ok();
    }

    #region Private Helpers

    private static string TruncateUri(string uri, int maxLen)
    {
        if (uri.Length <= maxLen) return uri;
        return "..." + uri.Substring(uri.Length - maxLen + 3);
    }

    private static string GenerateLargeTurtleDocument(int tripleCount)
    {
        var sb = new StringBuilder();

        sb.AppendLine("@prefix : <http://example.org/> .");
        sb.AppendLine("@prefix foaf: <http://xmlns.com/foaf/0.1/> .");
        sb.AppendLine();

        for (int i = 0; i < tripleCount; i++)
        {
            sb.AppendLine($":subject{i} foaf:name \"Person {i}\" .");
        }

        return sb.ToString();
    }

    #endregion
}
