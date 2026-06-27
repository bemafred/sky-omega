// ADR-012 Phase 4 — the source-on-step primitive. The reader turns the structured location the Phase-2
// enrichment put on the wire (WireFrame.File/Line) into a view-agnostic SourceWindow by reading the file from
// disk. Pure + deterministic (real temp files, no sockets, no engine). Reading is best-effort, so the failure
// modes (missing / oversized / out-of-range) are pinned as named statuses, not exceptions.

using System;
using System.IO;
using System.Linq;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class SourceWindowReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "drhook-srcwin-" + Guid.NewGuid().ToString("N"));

    public SourceWindowReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteLines(string name, int count)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(n => $"line {n}"));
        return path;
    }

    [Fact]
    public void Read_StoppedMidFile_WindowsAroundLine_MarksCurrent()
    {
        string path = WriteLines("Worker.cs", 10);
        var reader = new SourceWindowReader(new SourceWindowOptions { ContextLines = 2 });

        SourceWindow w = reader.Read(path, 5);

        Assert.Equal(SourceWindowStatus.Ok, w.Status);
        Assert.True(w.HasSource);
        Assert.Equal(5, w.CurrentLine);
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, w.Lines.Select(l => l.Number).ToArray());
        Assert.Equal("line 5", w.Lines.Single(l => l.IsCurrent).Text);
        Assert.Equal(5, w.Lines.Single(l => l.IsCurrent).Number);
        Assert.Single(w.Lines, l => l.IsCurrent); // exactly one current line
    }

    [Fact]
    public void Read_NearStart_ClampsToLineOne()
    {
        string path = WriteLines("Worker.cs", 10);
        var reader = new SourceWindowReader(new SourceWindowOptions { ContextLines = 3 });

        SourceWindow w = reader.Read(path, 1);

        Assert.Equal(SourceWindowStatus.Ok, w.Status);
        Assert.Equal(new[] { 1, 2, 3, 4 }, w.Lines.Select(l => l.Number).ToArray());
        Assert.True(w.Lines[0].IsCurrent);
    }

    [Fact]
    public void Read_NearEnd_ClampsToLastLine()
    {
        string path = WriteLines("Worker.cs", 10);
        var reader = new SourceWindowReader(new SourceWindowOptions { ContextLines = 3 });

        SourceWindow w = reader.Read(path, 10);

        Assert.Equal(SourceWindowStatus.Ok, w.Status);
        Assert.Equal(new[] { 7, 8, 9, 10 }, w.Lines.Select(l => l.Number).ToArray());
        Assert.True(w.Lines[^1].IsCurrent);
    }

    [Fact]
    public void Read_LineBeyondFile_ReturnsLineOutOfRange_NoLines()
    {
        string path = WriteLines("Short.cs", 3);
        var reader = new SourceWindowReader();

        SourceWindow w = reader.Read(path, 99);

        Assert.Equal(SourceWindowStatus.LineOutOfRange, w.Status);
        Assert.False(w.HasSource);
        Assert.Empty(w.Lines);
        Assert.Equal(99, w.CurrentLine); // the drifted line is still reported for the message
    }

    [Fact]
    public void Read_MissingFile_ReturnsFileNotFound()
    {
        var reader = new SourceWindowReader();

        SourceWindow w = reader.Read(Path.Combine(_dir, "does-not-exist.cs"), 4);

        Assert.Equal(SourceWindowStatus.FileNotFound, w.Status);
        Assert.Empty(w.Lines);
    }

    [Fact]
    public void Read_NoResolvedLocation_ReturnsNone()
    {
        var reader = new SourceWindowReader();

        Assert.Equal(SourceWindowStatus.NoLocation, reader.Read(null, 5).Status);      // native / no-PDB frame
        Assert.Equal(SourceWindowStatus.NoLocation, reader.Read("/some/Path.cs", null).Status);
        Assert.Equal(SourceWindowStatus.NoLocation, reader.Read("/some/Path.cs", 0).Status); // 1-based
        Assert.Same(SourceWindow.None, reader.Read(null, null)); // the no-location singleton, not a fresh record
    }

    [Fact]
    public void Read_FileExceedingMaxBytes_RefusedAsFileTooLarge()
    {
        string path = WriteLines("Big.cs", 10);
        var reader = new SourceWindowReader(new SourceWindowOptions { MaxFileBytes = 1 });

        SourceWindow w = reader.Read(path, 5);

        Assert.Equal(SourceWindowStatus.FileTooLarge, w.Status);
        Assert.Empty(w.Lines);
    }

    [Fact]
    public void Read_WireFrame_UsesItsFileAndLine()
    {
        string path = WriteLines("Worker.cs", 10);
        var reader = new SourceWindowReader(new SourceWindowOptions { ContextLines = 1 });

        SourceWindow w = reader.Read(new WireFrame("Acme.Worker.Run @ Worker.cs:4", path, 4));
        Assert.Equal(SourceWindowStatus.Ok, w.Status);
        Assert.Equal(4, w.CurrentLine);
        Assert.Equal(new[] { 3, 4, 5 }, w.Lines.Select(l => l.Number).ToArray());

        // a frame with no resolved location (native/internal/no-PDB) has nothing to show
        SourceWindow none = reader.Read(new WireFrame("[external]", null, null));
        Assert.Equal(SourceWindowStatus.NoLocation, none.Status);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_PastMaxFiles()
    {
        string f1 = WriteLines("A.cs", 3), f2 = WriteLines("B.cs", 3), f3 = WriteLines("C.cs", 3);
        var cache = new SourceFileCache(maxFiles: 2, maxBytes: long.MaxValue, maxFileBytes: long.MaxValue);

        Assert.Equal(SourceFileCache.LoadStatus.Ok, cache.Get(f1).Status); // {f1}
        Assert.Equal(SourceFileCache.LoadStatus.Ok, cache.Get(f2).Status); // {f1, f2}
        Assert.Equal(SourceFileCache.LoadStatus.Ok, cache.Get(f1).Status); // touch f1 -> {f2, f1}
        Assert.Equal(SourceFileCache.LoadStatus.Ok, cache.Get(f3).Status); // insert f3, evict LRU (f2) -> {f1, f3}
        Assert.Equal(2, cache.Count);                                      // the count bound held

        // Prove WHICH was evicted: delete both candidates from disk, then re-read. A cache HIT still returns Ok
        // (lines held in memory); a MISS re-reads from disk and now sees the deletion -> NotFound.
        File.Delete(f1);
        File.Delete(f2);
        Assert.Equal(SourceFileCache.LoadStatus.Ok, cache.Get(f1).Status);       // f1 was touched -> retained
        Assert.Equal(SourceFileCache.LoadStatus.NotFound, cache.Get(f2).Status); // f2 was LRU -> evicted
    }
}
