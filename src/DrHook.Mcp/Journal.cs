using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SkyOmega.DrHook.Mcp;

/// <summary>drhook-mcp's journal (ADR-016 6.9): <c>journal.ndjson</c> in DrHook's directory, one JSON object per line,
/// append-only. Written and flushed to disk before each act it records: a worker's discard, the kill of an owned target
/// after a discard, and every <c>drhook_kill</c>, so the record exists even if drhook-mcp dies in the act.
/// <list type="bullet">
/// <item>What it holds: identities (PIDs, start times, the program's path), reasons, states and stop locations. Never
/// values, arguments or environment (principle 11's redaction lies in what it records).</item>
/// <item>Its size: bounded as mercury.log is, at <see cref="Bound"/>, then one older file, <c>journal.ndjson.1</c>.</item>
/// <item>Several drhook-mcp processes may share it (one per client session), so each write holds
/// <c>journal.ndjson.lock</c> exclusively, waiting up to <see cref="LockWait"/>.</item>
/// <item>The operator consolidates it into Mercury; each discard's anomaly carries its entry. drhook-mcp never waits on
/// Mercury.</item>
/// </list></summary>
public sealed class Journal(string path)
{
    /// <summary>The journal's size before it is moved to <c>journal.ndjson.1</c>: 4 MB, as mercury.log.</summary>
    public const long Bound = 4 * 1024 * 1024;

    /// <summary>How long a write waits for another drhook-mcp's write to finish.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    // One line per entry, escaped only where JSON requires it, as DrHook's results are (OperatorJson): the operator reads
    // it, and the default encoder writes a time's + as \u002B (seen in the first live entry, 2026-11-21).
    private static readonly System.Text.Json.JsonSerializerOptions OneLine = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The journal's file.</summary>
    public string Path { get; } = path;

    /// <summary>Write one entry, stamped with the time of writing (<c>at</c>), and flush it to disk. Returns the line
    /// written, or, when it could not be written, why not; the act it records goes ahead either way.</summary>
    public string Write(string entry, JsonObject facts)
    {
        var record = new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["entry"] = entry,
            ["drhookMcpPid"] = Environment.ProcessId,
        };
        foreach ((string key, JsonNode? value) in facts) record[key] = value?.DeepClone();
        string line = record.ToJsonString(OneLine);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using FileStream held = HoldTheLock();
            if (File.Exists(Path) && new FileInfo(Path).Length >= Bound) File.Move(Path, Path + ".1", overwrite: true);
            using var journal = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            journal.Write(Encoding.UTF8.GetBytes(line + "\n"));
            journal.Flush(flushToDisk: true);
            return line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"drhook-mcp: the journal entry was not written to {Path}: {ex.GetType().Name}: {ex.Message}: {line}");
            return $"not written ({ex.GetType().Name}: {ex.Message}): {line}";
        }
    }

    // journal.ndjson.lock beside the journal, held exclusively for one write (an flock on Unix, a sharing lock on Windows).
    private FileStream HoldTheLock()
    {
        string lockPath = Path + ".lock";
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited.Elapsed < LockWait) { Thread.Sleep(10); }
        }
    }
}
