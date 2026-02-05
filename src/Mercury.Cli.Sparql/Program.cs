// Program.cs
// Mercury SPARQL CLI - Thin shim that delegates to SparqlTool library

using System;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Tool;

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

        var result = await SparqlTool.RunAsync(options.ToToolOptions(), Console.Out, Console.Error);
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

                case "-l":
                case "--load":
                    if (i + 1 >= args.Length) { options.Error = "--load requires a file path"; return options; }
                    options.LoadFile = args[++i];
                    break;

                case "-q":
                case "--query":
                    if (i + 1 >= args.Length) { options.Error = "--query requires a SPARQL query string"; return options; }
                    options.Query = args[++i];
                    break;

                case "-f":
                case "--query-file":
                    if (i + 1 >= args.Length) { options.Error = "--query-file requires a file path"; return options; }
                    options.QueryFile = args[++i];
                    break;

                case "-s":
                case "--store":
                    if (i + 1 >= args.Length) { options.Error = "--store requires a directory path"; return options; }
                    options.StorePath = args[++i];
                    break;

                case "--format":
                    if (i + 1 >= args.Length) { options.Error = "--format requires a format (json, csv, tsv, xml)"; return options; }
                    var format = args[++i].ToLowerInvariant();
                    options.Format = format switch
                    {
                        "json" => OutputFormat.Json,
                        "csv" => OutputFormat.Csv,
                        "tsv" => OutputFormat.Tsv,
                        "xml" => OutputFormat.Xml,
                        _ => OutputFormat.Unknown
                    };
                    if (options.Format == OutputFormat.Unknown) { options.Error = $"Unknown format: {format}. Use json, csv, tsv, or xml."; return options; }
                    break;

                case "--rdf-format":
                    if (i + 1 >= args.Length) { options.Error = "--rdf-format requires a format (nt, ttl, rdf, nq, trig)"; return options; }
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
                    if (options.RdfOutputFormat == RdfFormat.Unknown) { options.Error = $"Unknown RDF format: {rdfFormat}. Use nt, ttl, rdf, nq, or trig."; return options; }
                    break;

                case "-e":
                case "--explain":
                    if (i + 1 >= args.Length) { options.Error = "--explain requires a SPARQL query string"; return options; }
                    options.Explain = args[++i];
                    break;

                case "-r":
                case "--repl":
                    options.Repl = true;
                    break;

                default:
                    if (arg.StartsWith("-")) { options.Error = $"Unknown option: {arg}"; return options; }
                    if (options.Query == null && options.QueryFile == null) { options.Query = arg; }
                    break;
            }
        }

        return options;
    }

    private static void PrintHelp()
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

    public SparqlToolOptions ToToolOptions() => new()
    {
        LoadFile = LoadFile,
        Query = Query,
        QueryFile = QueryFile,
        StorePath = StorePath,
        Explain = Explain,
        Format = Format,
        RdfOutputFormat = RdfOutputFormat,
        Repl = Repl
    };
}
