// Program.cs
// Mercury Turtle CLI - Thin shim that delegates to TurtleTool library

using System;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Turtle.Tool;

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

        var result = await TurtleTool.RunAsync(options.ToToolOptions(), Console.Out, Console.Error);
        return result.ExitCode;
    }

    private static CliOptions ParseArgs(string[] args)
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
                    if (i + 1 >= args.Length) { options.Error = "--input requires a file path"; return options; }
                    options.InputFile = args[++i];
                    break;

                case "-o":
                case "--output":
                    if (i + 1 >= args.Length) { options.Error = "--output requires a file path"; return options; }
                    options.OutputFile = args[++i];
                    break;

                case "--output-format":
                    if (i + 1 >= args.Length) { options.Error = "--output-format requires a format (nt, nq, trig)"; return options; }
                    var fmt = args[++i].ToLowerInvariant();
                    options.OutputFormat = fmt switch
                    {
                        "nt" or "ntriples" => RdfFormat.NTriples,
                        "nq" or "nquads" => RdfFormat.NQuads,
                        "trig" => RdfFormat.TriG,
                        "ttl" or "turtle" => RdfFormat.Turtle,
                        _ => RdfFormat.Unknown
                    };
                    if (options.OutputFormat == RdfFormat.Unknown) { options.Error = $"Unknown format: {fmt}. Use nt, nq, trig, or ttl."; return options; }
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
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var count)) { options.Error = "--count requires a number"; return options; }
                    options.TripleCount = count;
                    i++;
                    break;

                case "-s":
                case "--store":
                    if (i + 1 >= args.Length) { options.Error = "--store requires a directory path"; return options; }
                    options.StorePath = args[++i];
                    break;

                default:
                    if (arg.StartsWith("-")) { options.Error = $"Unknown option: {arg}"; return options; }
                    if (options.InputFile == null) { options.InputFile = arg; }
                    break;
            }
        }

        return options;
    }

    private static void PrintHelp()
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

    public TurtleToolOptions ToToolOptions() => new()
    {
        InputFile = InputFile,
        OutputFile = OutputFile,
        OutputFormat = OutputFormat,
        StorePath = StorePath,
        Validate = Validate,
        Stats = Stats,
        Benchmark = Benchmark,
        TripleCount = TripleCount
    };
}
