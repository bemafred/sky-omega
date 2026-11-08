#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property OutputType=Library
//
// Probe 107's startup hook, passed to the target with DOTNET_STARTUP_HOOKS=<this dll>. The runtime calls
// StartupHook.Initialize (no namespace, by contract) before the target's Main. Q2: its mark must come after the
// probe's ResumeRuntime.

internal static class StartupHook
{
    public static void Initialize()
    {
        Console.WriteLine($"hook {DateTime.UtcNow.Ticks}");
        Console.Out.Flush();
    }
}
