using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>Bounds for a <see cref="SourceWindowReader"/> — strongly typed, mirroring
/// <see cref="DebugStateClientOptions"/>. Every field is a resource bound: reading arbitrary source files from
/// disk is a new file-I/O surface (ADR-012 Consequences require it accounted under the resource-limit-class
/// discipline), so the window size, the cache, and a single read are all capped up front rather than left
/// unbounded.</summary>
public sealed class SourceWindowOptions
{
    /// <summary>Lines of context shown above AND below the current line — a value of 6 yields up to a 13-line
    /// window (clamped at file edges). Must be ≥ 0.</summary>
    public int ContextLines { get; set; } = 6;

    /// <summary>Maximum number of distinct source files the cache retains; the least-recently-read is evicted
    /// past this. Must be ≥ 1.</summary>
    public int MaxCachedFiles { get; set; } = 32;

    /// <summary>Maximum total bytes across all cached files; the least-recently-read are evicted past this.</summary>
    public long MaxCacheBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>A source file larger than this is refused (<see cref="SourceWindowStatus.FileTooLarge"/>) rather
    /// than read — bounds a single read so one pathological path cannot exhaust memory.</summary>
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024;
}

/// <summary>Resolves an execution location (a <see cref="WireFrame"/>, or a raw file + line) to a renderable
/// <see cref="SourceWindow"/> by reading the source from disk — the Phase-4 "source-on-step" primitive. It is
/// view-agnostic: it returns the model, and a console / TUI / GUI view renders it. Lives in <c>DrHook.Viz</c>
/// (BCL-only, references only <c>DrHook.Wire</c>) because every view shares it; reading from disk is sound here
/// because the transport is a LOCAL Unix-domain socket, so a view is co-located with the target and its source
/// tree (ADR-012 D2). Best-effort by design — a missing/oversized/unreadable file yields a named
/// <see cref="SourceWindowStatus"/>, never an exception. A reader holds one bounded cache for its lifetime
/// (a view constructs one per session and reuses it across snapshots/steps).
///
/// <para>Thread-safe: a TUI view may render on its UI thread while the client's read loop pushes the next
/// snapshot, so the cache is guarded by a lock.</para></summary>
public sealed class SourceWindowReader
{
    private readonly SourceWindowOptions _options;
    private readonly SourceFileCache _cache;

    public SourceWindowReader(SourceWindowOptions? options = null)
    {
        _options = options ?? new SourceWindowOptions();
        if (_options.ContextLines < 0) throw new ArgumentOutOfRangeException(nameof(options), "ContextLines must be >= 0");
        if (_options.MaxCachedFiles < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxCachedFiles must be >= 1");
        _cache = new SourceFileCache(_options.MaxCachedFiles, _options.MaxCacheBytes, _options.MaxFileBytes);
    }

    /// <summary>Render the source window for a call-stack frame — the top frame for "where am I stopped", or any
    /// selected frame. A frame with no resolved location (<c>File</c>/<c>Line</c> null) yields
    /// <see cref="SourceWindow.None"/>.</summary>
    public SourceWindow Read(WireFrame frame) => Read(frame.File, frame.Line);

    /// <summary>Render the source window for an explicit file + 1-based line.</summary>
    public SourceWindow Read(string? filePath, int? line)
    {
        if (filePath is null || line is null || line.Value < 1) return SourceWindow.None;
        int current = line.Value;

        SourceFileCache.Entry entry = _cache.Get(filePath);
        switch (entry.Status)
        {
            case SourceFileCache.LoadStatus.NotFound:
                return new SourceWindow(SourceWindowStatus.FileNotFound, filePath, current, Array.Empty<SourceLine>());
            case SourceFileCache.LoadStatus.TooLarge:
                return new SourceWindow(SourceWindowStatus.FileTooLarge, filePath, current, Array.Empty<SourceLine>());
            case SourceFileCache.LoadStatus.Error:
                return new SourceWindow(SourceWindowStatus.ReadError, filePath, current, Array.Empty<SourceLine>());
        }

        string[] lines = entry.Lines!;
        if (current > lines.Length)
            return new SourceWindow(SourceWindowStatus.LineOutOfRange, filePath, current, Array.Empty<SourceLine>());

        int from = Math.Max(1, current - _options.ContextLines);
        int to = Math.Min(lines.Length, current + _options.ContextLines);
        var window = new List<SourceLine>(to - from + 1);
        for (int n = from; n <= to; n++)
            window.Add(new SourceLine(n, lines[n - 1], n == current));
        return new SourceWindow(SourceWindowStatus.Ok, filePath, current, window);
    }
}

/// <summary>A bounded LRU cache of source files (path → lines), so stepping within one file does not re-read it
/// each stop while the total retained source stays under both a file-count and a byte budget. Loading is
/// fault-contained: a missing / oversized / unreadable path returns a status, never throws. Internal — tested
/// directly via <c>InternalsVisibleTo</c> and exercised through <see cref="SourceWindowReader"/>.</summary>
internal sealed class SourceFileCache
{
    internal enum LoadStatus { Ok, NotFound, TooLarge, Error }

    internal readonly record struct Entry(LoadStatus Status, string[]? Lines);

    private sealed record CacheItem(string Path, string[] Lines, long Bytes);

    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly long _maxFileBytes;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheItem> _lru = new(); // front = most-recently-used
    private long _bytes;

    public SourceFileCache(int maxFiles, long maxBytes, long maxFileBytes)
    {
        _maxFiles = maxFiles;
        _maxBytes = maxBytes;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Current number of cached files (test/observability).</summary>
    public int Count { get { lock (_lock) return _index.Count; } }

    public Entry Get(string path)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(path, out LinkedListNode<CacheItem>? hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit); // touch — most-recently-used
                return new Entry(LoadStatus.Ok, hit.Value.Lines);
            }

            long size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new Entry(LoadStatus.NotFound, null);
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new Entry(LoadStatus.Error, null);
            }

            if (size > _maxFileBytes) return new Entry(LoadStatus.TooLarge, null);

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Entry(LoadStatus.Error, null);
            }

            var node = new LinkedListNode<CacheItem>(new CacheItem(path, lines, size));
            _lru.AddFirst(node);
            _index[path] = node;
            _bytes += size;
            Evict();
            return new Entry(LoadStatus.Ok, lines);
        }
    }

    // Drop least-recently-used entries while over either bound — but never the entry just inserted (front),
    // so a lone file larger than the byte budget is still served (count stays at 1).
    private void Evict()
    {
        while ((_index.Count > _maxFiles || _bytes > _maxBytes) && _lru.Count > 1)
        {
            LinkedListNode<CacheItem> last = _lru.Last!;
            _index.Remove(last.Value.Path);
            _bytes -= last.Value.Bytes;
            _lru.RemoveLast();
        }
    }
}
