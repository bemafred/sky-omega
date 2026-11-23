// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;

namespace SkyOmega.Mercury.Serving;

/// <summary>The options, shared by the Mercury tools, that say which store a tool holds and where it serves it.</summary>
[Flags]
public enum StoreOptions
{
    /// <summary>None of them.</summary>
    None = 0,

    /// <summary><c>-d</c> or <c>--data</c> with the store's directory, or the directory given with no option.</summary>
    Path = 1,

    /// <summary><c>--store</c> with a store's name: the store of that name under the stores' directory.</summary>
    Name = 2,

    /// <summary><c>--profile</c> with a profile, which applies when the store is made (ADR-029).</summary>
    Profile = 4,

    /// <summary><c>-p</c> or <c>--port</c> with the HTTP endpoint's port.</summary>
    Port = 8,

    /// <summary><c>--listen</c> with a host the HTTP endpoint listens on, as often as there are hosts (ADR-065 D4).</summary>
    Listen = 16,

    /// <summary><c>--service-connect-timeout</c> with the seconds a SERVICE call waits to connect to its endpoint, for
    /// the process (ADR-066 D5).</summary>
    ServiceConnectTimeout = 32,
}

/// <summary>What <see cref="StoreArguments.Read"/> did with an argument.</summary>
public enum ArgumentRead
{
    /// <summary>It read the argument, and the value that went with it.</summary>
    Read,

    /// <summary>The argument is an option it does not take; the tool reads it, or refuses it.</summary>
    NotMine,

    /// <summary>It refused the argument, and <see cref="StoreArguments.Error"/> says why.</summary>
    Refused,

    /// <summary>The argument was given where the store's directory goes, and it looks like a SPARQL query.
    /// <see cref="StoreArguments.Error"/> says so, and the tool adds where a query goes.</summary>
    LooksLikeSparql,
}

/// <summary>
/// Reads the store options a tool takes (ADR-065 D11), each the same way in every tool. A tool reads its own options
/// first and hands the rest to <see cref="Read"/>.
/// </summary>
/// <remarks>
/// A value that is missing is ignored, as is a port that is not a number; a profile that is not one is refused, and so
/// is a connect timeout that is not a whole number of seconds, 1 or more.
/// </remarks>
public sealed class StoreArguments(StoreOptions takes)
{
    private readonly List<string> _hosts = [];

    /// <summary>The store's directory, from <c>-d</c>, <c>--data</c> or the argument given with no option.</summary>
    public string? Path { get; private set; }

    /// <summary>The store's name, from <c>--store</c>.</summary>
    public string? Name { get; private set; }

    /// <summary>The profile for a store that is made, from <c>--profile</c>.</summary>
    public StoreProfile? Profile { get; private set; }

    /// <summary>The HTTP endpoint's port, from <c>-p</c> or <c>--port</c>.</summary>
    public int? Port { get; private set; }

    /// <summary>
    /// The hosts the HTTP endpoint listens on, from <c>--listen</c>, in the order given; <c>localhost</c> when none is
    /// given. Each is a name, an IP address, or <c>+</c> or <c>*</c> for every network (ADR-065 D4).
    /// </summary>
    public IReadOnlyList<string> Hosts => _hosts.Count > 0 ? _hosts : ["localhost"];

    /// <summary>
    /// How long a SERVICE call waits to connect to its endpoint, from <c>--service-connect-timeout</c>; null when it is
    /// not given, and the process keeps <see cref="SparqlEngine.ServiceConnectTimeout"/>'s 5 seconds (ADR-066 D5).
    /// </summary>
    public TimeSpan? ServiceConnectTimeout { get; private set; }

    /// <summary>Why the last argument refused was refused, as the line or lines that follow <c>Error: </c>.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Reads <c>args[i]</c> when it is one of the options this tool takes, and moves <paramref name="i"/> past the
    /// value read with it.
    /// </summary>
    public ArgumentRead Read(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "-d" or "--data" when Takes(StoreOptions.Path):
                if (i + 1 < args.Length)
                    Path = args[++i];
                return ArgumentRead.Read;
            case "--store" when Takes(StoreOptions.Name):
                if (i + 1 < args.Length)
                    Name = args[++i];
                return ArgumentRead.Read;
            case "-p" or "--port" when Takes(StoreOptions.Port):
                if (i + 1 < args.Length && int.TryParse(args[++i], out var port))
                    Port = port;
                return ArgumentRead.Read;
            case "--listen" when Takes(StoreOptions.Listen):
                if (i + 1 < args.Length)
                    _hosts.Add(args[++i]);
                return ArgumentRead.Read;
            case "--profile" when Takes(StoreOptions.Profile):
                if (i + 1 < args.Length && Enum.TryParse<StoreProfile>(args[++i], ignoreCase: true, out var profile))
                {
                    Profile = profile;
                    return ArgumentRead.Read;
                }
                Error = $"--profile requires one of: {string.Join(", ", Enum.GetNames<StoreProfile>())}.";
                return ArgumentRead.Refused;
            case "--service-connect-timeout" when Takes(StoreOptions.ServiceConnectTimeout):
                if (i + 1 < args.Length &&
                    int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds > 0)
                {
                    ServiceConnectTimeout = TimeSpan.FromSeconds(seconds);
                    return ArgumentRead.Read;
                }
                Error = "--service-connect-timeout requires a whole number of seconds, 1 or more.";
                return ArgumentRead.Refused;
        }

        var arg = args[i];
        if (arg.StartsWith('-') || !Takes(StoreOptions.Path))
            return ArgumentRead.NotMine;
        if (Path != null)
        {
            Error = $"Unexpected argument '{arg}'. Store path already set to '{Path}'.{Environment.NewLine}" +
                    "Use --help for usage information.";
            return ArgumentRead.Refused;
        }
        if (LooksLikeSparql(arg))
        {
            Error = $"'{arg}' looks like a SPARQL query, not a store path.";
            return ArgumentRead.LooksLikeSparql;
        }
        Path = arg;
        return ArgumentRead.Read;
    }

    /// <summary>The store's directory: the one given, else the store named, else the store <paramref name="defaultName"/>,
    /// under the stores' directory (<see cref="MercuryPaths.Store"/>).</summary>
    public string ResolvePath(string defaultName) => ResolvePath() ?? MercuryPaths.Store(defaultName);

    /// <summary>The store's directory: the one given, else the store named, under the stores' directory; null when
    /// neither was given.</summary>
    public string? ResolvePath() => Path ?? (Name is null ? null : MercuryPaths.Store(Name));

    /// <summary>Whether an argument has the words or the characters of a SPARQL query, which never appear in a path.</summary>
    public static bool LooksLikeSparql(string arg)
    {
        var upper = arg.ToUpperInvariant();
        return upper.Contains("SELECT ") || upper.Contains("CONSTRUCT ") ||
               upper.Contains("DESCRIBE ") || upper.Contains("ASK ") ||
               upper.Contains("INSERT ") || upper.Contains("DELETE ") ||
               upper.Contains("WHERE") || arg.Contains('{') || arg.Contains('}') ||
               arg.Contains("?s ") || arg.Contains("?p ") || arg.Contains("?o ");
    }

    private bool Takes(StoreOptions option) => (takes & option) != 0;
}
