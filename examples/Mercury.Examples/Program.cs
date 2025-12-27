namespace SkyOmega.Mercury.Examples;

internal static class Program
{
    public static void Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        switch (command)
        {
            case "storage":
                StorageExamples.RunAll();
                break;

            case "temporal":
                TemporalExamples.RunAll();
                break;

            case "demo":
                StorageExamples.DemoCapabilities();
                TemporalExamples.DemoCapabilities();
                break;

            case "all":
            default:
                StorageExamples.RunAll();
                Console.WriteLine();
                TemporalExamples.RunAll();
                break;
        }
    }
}
