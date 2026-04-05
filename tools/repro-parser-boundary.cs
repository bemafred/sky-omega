#!/usr/bin/env -S dotnet run
#:project ../src/Mercury/Mercury.csproj

// Minimal repro: Turtle parser with tiny buffer hitting blank node boundary.
// If buffer sliding works correctly, this should parse at ANY buffer size.

using System.Text;
using SkyOmega.Mercury.Rdf.Turtle;

var turtle = """
    @prefix ex: <http://example.org/> .
    ex:A rdfs:subClassOf [ a ex:Restriction ;
                           ex:prop ex:value
                         ] .
    ex:B ex:name "Bob" .
    """;

// Test with decreasing buffer sizes
foreach (var bufSize in new[] { 8192, 1024, 256, 128, 64, 32 })
{
    var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
    int count = 0;

    try
    {
        using var parser = new TurtleStreamParser(stream, bufferSize: bufSize);
        await parser.ParseAsync((s, p, o) =>
        {
            count++;
            Console.WriteLine($"  [{bufSize,5}] {s} {p} {o}");
        });
        Console.WriteLine($"  [{bufSize,5}] OK: {count} triples");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{bufSize,5}] FAIL at {count} triples: {ex.Message}");
    }

    Console.WriteLine();
}
