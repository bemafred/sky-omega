using SkyOmega.Mercury.Tests;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║           Sky Omega Mercury - Tests & Examples               ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

var cliArgs = Environment.GetCommandLineArgs();
var command = cliArgs.Length > 1 ? cliArgs[1].ToLowerInvariant() : "all";

switch (command)
{
    case "tests":
        ParserTests.RunAllTests();
        break;

    case "storage":
        FileStorageExamples.RunAll();
        break;

    case "temporal":
        SkyOmegaExamples.RunAll();
        break;

    case "bench":
        FileStorageExamples.DemoTBScaleCapability();
        SkyOmegaExamples.BenchmarkTemporalStorage();
        break;

    case "demo":
        SkyOmegaExamples.DemoSkyOmegaCapabilities();
        FileStorageExamples.DemoTBScaleCapability();
        break;

    case "all":
    default:
        ParserTests.RunAllTests();
        Console.WriteLine();
        FileStorageExamples.RunAll();
        Console.WriteLine();
        SkyOmegaExamples.RunAll();
        break;
}

Console.WriteLine("Done.");
