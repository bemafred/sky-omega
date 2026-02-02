// Program.cs
// Mercury Turtle CLI - Parse, validate, convert, and load Turtle RDF files

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.TriG;

namespace SkyOmega.Mercury.Cli.Turtle;

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

        // Demo mode - no arguments, run examples
        if (options.InputFile == null && !options.Benchmark)
        {
            await RunDemoExamples();
            return 0;
        }

        try
        {
            if (options.Validate)
            {
                return await ValidateTurtle(options.InputFile!);
            }
            else if (options.Stats)
            {
                return await ShowStatistics(options.InputFile!);
            }
            else if (options.Benchmark)
            {
                return await RunBenchmark(options.InputFile, options.TripleCount);
            }
            else if (options.StorePath != null)
            {
                return await LoadIntoStore(options.InputFile!, options.StorePath);
            }
            else if (options.OutputFile != null || options.OutputFormat != RdfFormat.Unknown)
            {
                return await ConvertFormat(options.InputFile!, options.OutputFile, options.OutputFormat);
            }
            else
            {
                // Default: parse and print triples
                return await ParseAndPrint(options.InputFile!);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
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

                case "-i":
                case "--input":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--input requires a file path";
                        return options;
                    }
                    options.InputFile = args[++i];
                    break;

                case "-o":
                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--output requires a file path";
                        return options;
                    }
                    options.OutputFile = args[++i];
                    break;

                case "--output-format":
                    if (i + 1 >= args.Length)
                    {
                        options.Error = "--output-format requires a format (nt, nq, trig)";
                        return options;
                    }
                    var fmt = args[++i].ToLowerInvariant();
                    options.OutputFormat = fmt switch
                    {
                        "nt" or "ntriples" => RdfFormat.NTriples,
                        "nq" or "nquads" => RdfFormat.NQuads,
                        "trig" => RdfFormat.TriG,
                        "ttl" or "turtle" => RdfFormat.Turtle,
                        _ => RdfFormat.Unknown
                    };
                    if (options.OutputFormat == RdfFormat.Unknown)
                    {
                        options.Error = $"Unknown format: {fmt}. Use nt, nq, trig, or ttl.";
                        return options;
                    }
                    break;

                case "-v":
                case "--validate":
                    options.Validate = true;
                    break;

                case "--stats":
                    options.Stats = true;
                    break;

                case "-b":
                case "--benchmark":
                    options.Benchmark = true;
                    break;

                case "-n":
                case "--count":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var count))
                    {
                        options.Error = "--count requires a number";
                        return options;
                    }
                    options.TripleCount = count;
                    i++;
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

                default:
                    if (arg.StartsWith("-"))
                    {
                        options.Error = $"Unknown option: {arg}";
                        return options;
                    }
                    // Positional argument - treat as input file
                    if (options.InputFile == null)
                    {
                        options.InputFile = arg;
                    }
                    break;
            }
        }

        return options;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            Mercury Turtle CLI - Parse, validate, convert, and load Turtle RDF files

            USAGE:
                mercury-turtle [OPTIONS] [INPUT_FILE]

            OPTIONS:
                -h, --help              Show this help message
                -i, --input <FILE>      Input Turtle file
                -o, --output <FILE>     Output file (format detected from extension)
                --output-format <FMT>   Output format: nt, nq, trig, ttl
                -v, --validate          Validate syntax only (report errors)
                --stats                 Show statistics (triple count, predicates)
                -b, --benchmark         Run performance benchmark
                -n, --count <N>         Number of triples for benchmark (default: 10000)
                -s, --store <PATH>      Load into persistent QuadStore

            EXAMPLES:
                # Validate Turtle syntax
                mercury-turtle --validate input.ttl

                # Convert Turtle to N-Triples
                mercury-turtle --input data.ttl --output data.nt

                # Convert with explicit format
                mercury-turtle data.ttl --output-format nt > data.nt

                # Show statistics
                mercury-turtle --stats data.ttl

                # Load into persistent store
                mercury-turtle --input data.ttl --store ./mydb

                # Run performance benchmark
                mercury-turtle --benchmark --count 100000

                # Run demo examples (no arguments)
                mercury-turtle

            OUTPUT FORMATS:
                nt, ntriples    N-Triples
                nq, nquads      N-Quads
                trig            TriG
                ttl, turtle     Turtle
            """);
    }

    static async Task<int> ValidateTurtle(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        Console.WriteLine($"Validating: {filePath}");

        await using var stream = File.OpenRead(filePath);
        using var parser = new TurtleStreamParser(stream);

        long tripleCount = 0;
        var errors = new List<string>();

        try
        {
            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
            });

            Console.WriteLine($"Valid Turtle: {tripleCount:N0} triples");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            // InvalidDataException message includes "Line X, Column Y: <message>"
            Console.Error.WriteLine($"Syntax error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> ShowStatistics(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        Console.WriteLine($"Analyzing: {filePath}");
        Console.WriteLine();

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

        Console.WriteLine($"Total triples:    {tripleCount:N0}");
        Console.WriteLine($"Unique subjects:  {subjects.Count:N0}");
        Console.WriteLine($"Unique objects:   {objects.Count:N0}");
        Console.WriteLine($"Unique predicates: {predicateCounts.Count:N0}");
        Console.WriteLine();

        Console.WriteLine("Predicate distribution:");
        var sortedPredicates = new List<KeyValuePair<string, long>>(predicateCounts);
        sortedPredicates.Sort((a, b) => b.Value.CompareTo(a.Value));

        var maxToShow = Math.Min(10, sortedPredicates.Count);
        for (int i = 0; i < maxToShow; i++)
        {
            var kv = sortedPredicates[i];
            var pct = (double)kv.Value / tripleCount * 100;
            Console.WriteLine($"  {kv.Value,8:N0} ({pct,5:F1}%) {TruncateUri(kv.Key, 50)}");
        }

        if (sortedPredicates.Count > maxToShow)
        {
            Console.WriteLine($"  ... and {sortedPredicates.Count - maxToShow} more predicates");
        }

        return 0;
    }

    static string TruncateUri(string uri, int maxLen)
    {
        if (uri.Length <= maxLen) return uri;
        return "..." + uri.Substring(uri.Length - maxLen + 3);
    }

    static async Task<int> RunBenchmark(string? inputFile, int tripleCount)
    {
        Console.WriteLine("=== Mercury Turtle Parser Benchmark ===\n");

        string turtle;
        string source;

        if (inputFile != null)
        {
            if (!File.Exists(inputFile))
            {
                Console.Error.WriteLine($"File not found: {inputFile}");
                return 1;
            }
            turtle = await File.ReadAllTextAsync(inputFile);
            source = inputFile;
        }
        else
        {
            turtle = GenerateLargeTurtleDocument(tripleCount);
            source = $"generated ({tripleCount:N0} triples)";
        }

        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Size: {turtle.Length / 1024:N0} KB");
        Console.WriteLine();

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

        // Zero-GC callback - no string allocations in hot path
        await parser.ParseAsync((subject, predicate, obj) =>
        {
            parsedCount++;
        });

        sw.Stop();

        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var memAfter = GC.GetTotalMemory(forceFullCollection: false);

        Console.WriteLine("Results:");
        Console.WriteLine($"  Triples:     {parsedCount:N0}");
        Console.WriteLine($"  Time:        {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  Throughput:  {parsedCount / sw.Elapsed.TotalSeconds:N0} triples/sec");
        Console.WriteLine($"  Throughput:  {turtle.Length / 1024.0 / sw.Elapsed.TotalSeconds:N2} KB/sec");
        Console.WriteLine();
        Console.WriteLine("GC Collections:");
        Console.WriteLine($"  Gen 0:       {gen0After - gen0Before}");
        Console.WriteLine($"  Gen 1:       {gen1After - gen1Before}");
        Console.WriteLine($"  Gen 2:       {gen2After - gen2Before}");
        Console.WriteLine($"  Memory:      {(memAfter - memBefore) / 1024.0:+#,##0.00;-#,##0.00;0} KB");

        if (gen0After - gen0Before == 0 && gen1After - gen1Before == 0 && gen2After - gen2Before == 0)
        {
            Console.WriteLine("\nZero GC collections during parse!");
        }

        return 0;
    }

    static async Task<int> LoadIntoStore(string inputFile, string storePath)
    {
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"File not found: {inputFile}");
            return 1;
        }

        Console.WriteLine($"Loading: {inputFile}");
        Console.WriteLine($"Store: {storePath}");

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
                Console.WriteLine($"  Loaded {count:N0} triples...");
            }
        });

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"Loaded {count:N0} triples in {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Throughput: {count / sw.Elapsed.TotalSeconds:N0} triples/sec");
        Console.WriteLine($"Store ready at: {storePath}");

        return 0;
    }

    static async Task<int> ConvertFormat(string inputFile, string? outputFile, RdfFormat outputFormat)
    {
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"File not found: {inputFile}");
            return 1;
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

        TextWriter output = outputFile != null
            ? new StreamWriter(outputFile, append: false, Encoding.UTF8)
            : Console.Out;

        try
        {
            long count = 0;

            switch (outputFormat)
            {
                case RdfFormat.NTriples:
                    await using (var writer = new NTriplesStreamWriter(output))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteTriple(subject, predicate, obj);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.NQuads:
                    await using (var writer = new NQuadsStreamWriter(output))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.TriG:
                    await using (var writer = new TriGStreamWriter(output))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
                            count++;
                        });
                    }
                    break;

                case RdfFormat.Turtle:
                    await using (var writer = new TurtleStreamWriter(output))
                    {
                        await parser.ParseAsync((subject, predicate, obj) =>
                        {
                            writer.WriteTriple(subject, predicate, obj);
                            count++;
                        });
                    }
                    break;

                default:
                    Console.Error.WriteLine($"Output format not supported: {outputFormat}");
                    return 1;
            }

            if (outputFile != null)
            {
                Console.Error.WriteLine($"Converted {count:N0} triples to {outputFile}");
            }

            return 0;
        }
        finally
        {
            if (outputFile != null)
            {
                await output.DisposeAsync();
            }
        }
    }

    static async Task<int> ParseAndPrint(string inputFile)
    {
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"File not found: {inputFile}");
            return 1;
        }

        await using var stream = File.OpenRead(inputFile);
        using var parser = new TurtleStreamParser(stream);

        long count = 0;

        await parser.ParseAsync((subject, predicate, obj) =>
        {
            count++;
            Console.WriteLine($"{subject} {predicate} {obj} .");
        });

        Console.Error.WriteLine($"Parsed {count:N0} triples");
        return 0;
    }

    static async Task RunDemoExamples()
    {
        Console.WriteLine("=== Mercury Turtle Parser Demo ===\n");
        Console.WriteLine("Run with --help for full usage information.\n");

        // Example 1: Parse from string
        Console.WriteLine("Example 1: Parse Turtle from string\n");

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
            Console.WriteLine($"  {count}. {subject} {predicate} {obj}");
        });

        Console.WriteLine($"\nParsed {count} triples\n");

        // Example 2: Quick benchmark
        Console.WriteLine("Example 2: Quick benchmark (1000 triples)\n");

        var benchTurtle = GenerateLargeTurtleDocument(1000);
        using var benchStream = new MemoryStream(Encoding.UTF8.GetBytes(benchTurtle));
        using var benchParser = new TurtleStreamParser(benchStream);

        var sw = Stopwatch.StartNew();
        long benchCount = 0;

        await benchParser.ParseAsync((s, p, o) => benchCount++);

        sw.Stop();

        Console.WriteLine($"  Parsed {benchCount:N0} triples in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  Throughput: {benchCount / sw.Elapsed.TotalSeconds:N0} triples/sec\n");

        Console.WriteLine("Try: mercury-turtle --benchmark --count 100000");
    }

    static string GenerateLargeTurtleDocument(int tripleCount)
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
}

internal class CliOptions
{
    public string? InputFile { get; set; }
    public string? OutputFile { get; set; }
    public RdfFormat OutputFormat { get; set; } = RdfFormat.Unknown;
    public string? StorePath { get; set; }
    public bool Validate { get; set; }
    public bool Stats { get; set; }
    public bool Benchmark { get; set; }
    public int TripleCount { get; set; } = 10_000;
    public bool ShowHelp { get; set; }
    public string? Error { get; set; }
}
