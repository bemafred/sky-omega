#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
// What one store operation, and each file operation under it, costs on this machine, in the temporary directory it is
// given (ADR-067 Q5 and Q6; the README beside this file). Each operation runs once untimed, then n times timed; the
// median and the 90th percentile are printed in milliseconds, as a Markdown table. The argument names the condition.
//
//   dotnet run tools/probes/runner-flush-cost/what-a-store-operation-costs.cs -- "as the runner is set up"
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var condition = args.Length > 0 ? args[0] : "no condition named";
var root = Directory.CreateTempSubdirectory("mops");
var drive = new DriveInfo(root.FullName);
var results = new List<(string Name, List<double> Ms)>();
var wall = Stopwatch.StartNew();
void Timed(string name, int n, Action<int> op)
{
    op(-1);
    var ms = new List<double>(n);
    for (int i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); op(i); ms.Add(sw.Elapsed.TotalMilliseconds); }
    results.Add((name, ms));
}
string Dir(string kind, int i) => Path.Combine(root.FullName, $"{kind}{(i < 0 ? "w" : i.ToString())}");

// The file system alone. The log opens its file with FileOptions.WriteThrough (WriteAheadLog.cs) and flushes it to disk
// at each commit: FlushFileBuffers on Windows, fsync on Linux and macOS.
Timed("file: create, write 4 KB, flush to disk, close", 50, i =>
{
    using var f = new FileStream(Path.Combine(root.FullName, $"f{i}"), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    f.Write(new byte[4096]); f.Flush(flushToDisk: true);
});
Timed("file: create and delete an empty file", 200, i => { var p = Path.Combine(root.FullName, $"e{i}"); File.WriteAllBytes(p, []); File.Delete(p); });
using (var log = new FileStream(Path.Combine(root.FullName, "log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
    Timed("file: append 100 B, flush to disk (a log's commit)", 200, _ => { log.Write(new byte[100]); log.Flush(flushToDisk: true); });

// A file mapped as BTreeFile maps an index (BTreeFile.cs, the constructor): opened for random access, with or without
// FileOptions.WriteThrough, made sparse and sized to a test store's index (64 MB, StorageOptions.ForTesting), and
// mapped whole. FlushPage flushes the whole view: FlushViewOfFile on Windows, msync(MS_SYNC) on Linux and macOS.
// "The next page" writes into a page not written before, as an insert that takes a new page does; "the same page"
// writes into one written before.
void Mapped(string name, bool writeThrough, bool nextPage, bool fileToDisk)
{
    using var file = new FileStream(Path.Combine(root.FullName, $"map{results.Count}"), FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.None, 4096, FileOptions.RandomAccess | (writeThrough ? FileOptions.WriteThrough : FileOptions.None));
    SparseFile.SetLength(file, 64L << 20);
    using var mapping = MemoryMappedFile.CreateFromFile(file, null, file.Length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
    using var view = mapping.CreateViewAccessor(0, file.Length, MemoryMappedFileAccess.ReadWrite);
    Timed(name, 100, i =>
    {
        view.Write(nextPage ? (i + 1L) * BTreeFile.PageSize : 0, (long)i);
        view.Flush();
        if (fileToDisk) file.Flush(flushToDisk: true);
    });
}
Mapped("mapped, WriteThrough (an index): the next page, flush the view", writeThrough: true, nextPage: true, fileToDisk: false);
Mapped("mapped, WriteThrough (an index): the same page, flush the view", writeThrough: true, nextPage: false, fileToDisk: false);
Mapped("mapped, no WriteThrough: the next page, flush the view", writeThrough: false, nextPage: true, fileToDisk: false);
Mapped("mapped, no WriteThrough: the same page, flush the view", writeThrough: false, nextPage: false, fileToDisk: false);
Mapped("mapped, no WriteThrough: the next page, flush the view, then the file to disk", writeThrough: false, nextPage: true, fileToDisk: true);

// A test store's life, with the options the tests use
Timed("store: create and close (ForTesting)", 15, i => new QuadStore(Dir("c", i), null, null, StorageOptions.ForTesting).Dispose());
using (var store = new QuadStore(Dir("w", 0), null, null, StorageOptions.ForTesting))
{
    Timed("store: one insert (AddCurrent)", 100, i => store.AddCurrent($"<urn:s{i}>", "<urn:p>", $"\"{i}\""));
    Timed("store: a batch of 100 inserts", 10, i =>
    {
        store.BeginBatch();
        for (int k = 0; k < 100; k++) store.AddCurrentBatched($"<urn:b{i}-{k}>", "<urn:p>", $"\"{k}\"");
        store.CommitBatch();
    });
    Timed("store: SPARQL INSERT DATA of one quad", 50, i => SparqlEngine.Update(store, $"INSERT DATA {{ <urn:u{i}> <urn:p> \"x\" }}"));
    Timed("store: SPARQL SELECT of one subject", 200, i => SparqlEngine.Query(store, $"SELECT ?o WHERE {{ <urn:s{Math.Max(i, 0)}> ?p ?o }}"));
    Timed("store: checkpoint", 10, _ => store.Checkpoint());
}
for (int i = -1; i < 10; i++)
    using (var s = new QuadStore(Dir("r", i), null, null, StorageOptions.ForTesting))
        for (int k = 0; k < 10; k++) s.AddCurrent($"<urn:s{k}>", "<urn:p>", "\"x\"");
Timed("store: reopen and close (10 writes in its log)", 10, i => new QuadStore(Dir("r", i), null, null, StorageOptions.ForTesting).Dispose());
Timed("store: delete a closed store's directory", 15, i => Directory.Delete(Dir("c", i), true));
using (var pool = new QuadStorePool(storageOptions: StorageOptions.ForTesting, maxConcurrent: 2, purpose: "mops"))
{
    var first = pool.Rent(); first.AddCurrent("<urn:s>", "<urn:p>", "\"x\""); pool.Return(first);
    Timed("pool: rent a used store (it is cleared), insert, return", 20, _ => { var st = pool.Rent(); st.AddCurrent("<urn:s>", "<urn:p>", "\"x\""); pool.Return(st); });
}

Console.WriteLine($"### {condition}");
Console.WriteLine();
Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} cores, " +
                  $".NET {Environment.Version}. Temporary directory {Path.GetTempPath()} ({drive.DriveFormat}, " +
                  $"{drive.AvailableFreeSpace / (1L << 30)} GB free). All operations: {wall.Elapsed.TotalSeconds:F0} s.");
Console.WriteLine();
Console.WriteLine("| Operation | Median ms | p90 ms | n |");
Console.WriteLine("|---|--:|--:|--:|");
foreach (var (name, ms) in results)
{
    var sorted = ms.Order().ToList();
    double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
    Console.WriteLine($"| {name} | {At(0.5):F2} | {At(0.9):F2} | {sorted.Count} |");
}
Console.WriteLine();
try { root.Delete(true); } catch (Exception ex) { Console.WriteLine($"Not deleted: {root.FullName}: {ex.Message}"); }
