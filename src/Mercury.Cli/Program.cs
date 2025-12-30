// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Repl;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Utilities;

// Parse command line arguments
string? storePath = null;
bool inMemory = false;
bool showHelp = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h":
        case "--help":
            showHelp = true;
            break;
        case "-m":
        case "--memory":
            inMemory = true;
            break;
        case "-d":
        case "--data":
            if (i + 1 < args.Length)
                storePath = args[++i];
            break;
        default:
            if (!args[i].StartsWith('-') && storePath == null)
                storePath = args[i];
            break;
    }
}

if (showHelp)
{
    Console.WriteLine("""
        Mercury SPARQL CLI

        Usage: mercury [options] [store-path]

        Options:
          -h, --help      Show this help message
          -m, --memory    Use in-memory store (no persistence)
          -d, --data      Path to data directory

        Examples:
          mercury                     # In-memory store
          mercury ./mydata            # Persistent store at ./mydata
          mercury -m                  # Explicit in-memory mode
          mercury -d /path/to/store   # Persistent store at specified path

        Inside the REPL:
          :help           Show REPL commands
          :quit           Exit the REPL
        """);
    return 0;
}

// Create or open store
string actualPath;
bool usingTempStore = false;

if (inMemory || storePath == null)
{
    // Use temp directory for in-memory mode
    actualPath = TempPath.Cli("session");
    usingTempStore = true;

    if (!inMemory && storePath == null)
    {
        Console.WriteLine("(Using temporary store. Use -d <path> for persistence.)");
        Console.WriteLine();
    }
}
else
{
    actualPath = storePath;
}

var store = new QuadStore(actualPath);

// Run REPL
try
{
    using (store)
    using (var repl = new InteractiveRepl(store))
    {
        repl.Run();
    }
}
finally
{
    // Clean up temp store
    if (usingTempStore && Directory.Exists(actualPath))
    {
        try
        {
            Directory.Delete(actualPath, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

return 0;
