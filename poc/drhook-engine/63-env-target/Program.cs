// DrHook.Engine probe 63 TARGET. Echoes the launch-overridden env var (DRHOOK_PROBE_ENV) and an
// inherited one (HOME) to a file (args[0]), then exits — proving env overrides reach the child and
// inherited vars survive (inherit-plus-override).

using System;
using System.IO;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: EnvTarget <out-file>");
    return 2;
}

string overridden = Environment.GetEnvironmentVariable("DRHOOK_PROBE_ENV") ?? "(unset)";
bool inheritedPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"));
File.WriteAllText(args[0], $"DRHOOK_PROBE_ENV={overridden}\nHOME_PRESENT={inheritedPresent}\n");
return 0;
