// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Serving;

/// <summary>
/// One store, held by this process: the pool that holds it, and the log Mercury owns beside it (ADR-061 M7).
/// Disposing it closes the store and writes the stop to the log.
/// </summary>
/// <remarks>
/// Each Mercury tool that holds a store opens it here (ADR-065 D11). What a tool says when the store is in use, or
/// around the open and the close, stays the tool's.
/// </remarks>
public sealed class StoreSession : IDisposable
{
    private readonly string _tool;
    private readonly UnhandledExceptionEventHandler? _logUnhandled;
    private bool _disposed;

    private StoreSession(QuadStorePool pool, string path, FileLogger? log, string tool)
    {
        Pool = pool;
        Path = path;
        Log = log;
        _tool = tool;
        if (log is not null)
        {
            _logUnhandled = (_, unhandled) => log.Log(LogLevel.Critical,
                $"{tool} ends with an exception that nothing handled: {unhandled.ExceptionObject}");
            AppDomain.CurrentDomain.UnhandledException += _logUnhandled;
        }
        // A pool can start with no store: a new one, a temporary one, or one made by a version before pools had
        // their metadata. Pool.Active needs one.
        pool.EnsureActive("primary");
    }

    /// <summary>The pool that holds the store.</summary>
    public QuadStorePool Pool { get; }

    /// <summary>The store's directory, as a full path.</summary>
    public string Path { get; }

    /// <summary>The store's log, <c>mercury.log</c> in its directory; null for a temporary store, which has none.</summary>
    public FileLogger? Log { get; }

    /// <summary>What to say, once the store is open, when its log cannot take its lines; null when it can.</summary>
    public string? LogWarning => Log is { LinesNotWritten: > 0 }
        ? $"Warning: the log at {Log.Path} cannot be written. This run leaves no log."
        : null;

    /// <summary>The log file of the store at <paramref name="storePath"/>.</summary>
    public static string LogPathFor(string storePath) =>
        System.IO.Path.Combine(System.IO.Path.GetFullPath(storePath), "mercury.log");

    /// <summary>
    /// Opens the store at <paramref name="storePath"/>, made there when there is none, with its log.
    /// </summary>
    /// <param name="storePath">The store's directory.</param>
    /// <param name="tool">The tool's command, which the log's lines name.</param>
    /// <param name="version">The tool's version, for the log's first line.</param>
    /// <param name="storageOptions">The store's options. Its profile applies when the store is made; a store that
    /// exists keeps its own (ADR-029). Default: <see cref="StorageOptions.Default"/>.</param>
    /// <exception cref="Abstractions.StoreInUseException">Another process holds the store. Nothing is written to the
    /// log then, so the log of the process that holds the store is left alone.</exception>
    public static StoreSession Open(string storePath, string tool, string version, StorageOptions? storageOptions = null)
    {
        var path = System.IO.Path.GetFullPath(storePath);
        // The log writes nothing until the pool holds the store and reports the open.
        var log = new FileLogger(LogPathFor(path), firstLine: $"{tool} {version} starts, process {Environment.ProcessId}");
        QuadStorePool pool;
        try
        {
            pool = new QuadStorePool(path, new QuadStorePoolOptions
            {
                StorageOptions = storageOptions ?? StorageOptions.Default,
                Logger = log,
            });
        }
        catch
        {
            log.Dispose();
            throw;
        }
        return new StoreSession(pool, path, log, tool);
    }

    /// <summary>Opens a temporary store, deleted when it is closed. It has no log.</summary>
    /// <param name="purpose">Names the temporary directory.</param>
    /// <param name="tool">The tool's command.</param>
    public static StoreSession OpenTemporary(string purpose, string tool)
    {
        var pool = QuadStorePool.CreateTemp(purpose);
        return new StoreSession(pool, pool.BasePath!, log: null, tool);
    }

    /// <summary>The SPARQL endpoint over this store at <c>http://{host}:{port}/</c> for each host, not yet started.</summary>
    public SparqlHttpServer CreateHttpServer(IReadOnlyList<string> hosts, int port, bool enableUpdates) =>
        new(() => Pool.Active, hosts.Select(host => MercuryPorts.GetBaseUrl(host, port)),
            new SparqlHttpServerOptions { EnableUpdates = enableUpdates });

    /// <summary>Closes the store, then writes the stop to its log.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Pool.Dispose();
        if (Log is not null)
        {
            Log.Log(LogLevel.Info, $"{_tool} stops, process {Environment.ProcessId}");
            Log.Dispose();
        }
        if (_logUnhandled is not null)
            AppDomain.CurrentDomain.UnhandledException -= _logUnhandled;
    }
}
