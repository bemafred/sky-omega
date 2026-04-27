using BenchmarkDotNet.Running;

namespace SkyOmega.Mercury.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        // Subcommand dispatch — non-BenchmarkDotNet runners live here and are invoked
        // by passing their name as the first argument.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "wdbench":
                    return WdBenchRunner.Run(args.AsSpan(1).ToArray());
            }
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
