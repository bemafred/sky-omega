// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Serving;

namespace SkyOmega.Mercury.Hosting;

/// <summary>
/// mercuryd's options (ADR-065 D1, D2, D4, D5): the store it serves, where it serves it, and whether it takes updates.
/// The store options are read by <see cref="StoreArguments"/>, as every tool reads them.
/// </summary>
public sealed class DaemonArguments
{
    private DaemonArguments()
    {
    }

    /// <summary>
    /// The store and where it is served: <c>--store</c> or <c>-d</c> (one of them is needed), <c>--profile</c>,
    /// <c>--listen</c> and <c>--port</c>.
    /// </summary>
    public StoreArguments Store { get; } = new(
        StoreOptions.Path | StoreOptions.Name | StoreOptions.Profile | StoreOptions.Listen | StoreOptions.Port);

    /// <summary><c>--read-only</c>: the HTTP endpoint takes no updates (D5).</summary>
    public bool ReadOnly { get; private set; }

    /// <summary><c>-h</c> or <c>--help</c>.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary><c>-v</c> or <c>--version</c>.</summary>
    public bool ShowVersion { get; private set; }

    /// <summary>Why the options were refused, as the line or lines that follow <c>Error: </c>; null when they were
    /// not.</summary>
    public string? Error { get; private set; }

    /// <summary>Reads mercuryd's command line. The first option refused stops the reading.</summary>
    public static DaemonArguments Read(string[] args)
    {
        var read = new DaemonArguments();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    read.ShowHelp = true;
                    break;
                case "-v" or "--version":
                    read.ShowVersion = true;
                    break;
                case "--read-only":
                    read.ReadOnly = true;
                    break;
                default:
                    switch (read.Store.Read(args, ref i))
                    {
                        case ArgumentRead.Read:
                            break;
                        case ArgumentRead.NotMine:
                            read.Error = $"Unknown option '{args[i]}'.{Environment.NewLine}Use --help for usage information.";
                            return read;
                        case ArgumentRead.LooksLikeSparql:
                            read.Error = $"{read.Store.Error}{Environment.NewLine}" +
                                         "mercuryd serves a store; query it at its endpoint, or over its attach socket.";
                            return read;
                        default:
                            read.Error = read.Store.Error;
                            return read;
                    }
                    break;
            }
        }

        // One store per process, and a service that made a store nobody named would serve the wrong one (D2).
        if (!read.ShowHelp && !read.ShowVersion && read.Store.ResolvePath() is null)
            read.Error = "mercuryd serves one store: name it with --store <name>, or give its directory with -d <path>." +
                         $"{Environment.NewLine}Use --help for usage information.";
        return read;
    }
}
