#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
// Question (ADR-064 D10, Martin 2026-11-16: "It should stop, yes, it is read only by design."): which ways can a
// Reference store that already holds a load take another one?
// Hypotheses, read in QuadStore on 2026-11-17:
// - a Reference store with a hash atom store, opened again in bulk mode, is not refused: the single-bulk-load check
//   applies to a sorted atom store only (ADR-034 D7);
// - the same store opened without bulk mode takes a batch too: BeginBatch on Reference writes into GSPO directly
//   when the store is not in bulk mode, and GPOS is not written, so the two indexes no longer agree;
// - a Reference store with a sorted atom store, opened again in bulk mode, is refused.
// Construction: three Reference stores, each loaded once in bulk mode with two triples and closed. Then a second
// load into each, in the mode the hypothesis names, and what each index answers.
using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

var root = Path.Combine(Path.GetTempPath(), "mercury-probe-reference-load-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const string P = "<urn:probe:p>";

string Load(string name, AtomStoreImplementation atoms)
{
    var dir = Path.Combine(root, name);
    Directory.CreateDirectory(dir);
    (StoreSchema.ForProfile(StoreProfile.Reference) with { AtomStore = atoms }).WriteTo(dir);
    using var store = new QuadStore(dir, null, null, new StorageOptions { BulkMode = true, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });
    store.BeginBatch();
    store.AddCurrentBatched("<urn:probe:s1>", P, "\"one\"");
    store.AddCurrentBatched("<urn:probe:s2>", P, "\"two\"");
    store.CommitBatch();
    store.FlushToDisk();
    store.RebuildSecondaryIndexes();
    return dir;
}

void Show(string dir, string when)
{
    using var store = new QuadStore(dir);
    int bySubject = 0, byPredicate = 0;
    var gspo = store.QueryCurrent("<urn:probe:s3>", default, default);
    while (gspo.MoveNext()) bySubject++;
    gspo.Dispose();
    var gpos = store.QueryCurrent(default, P, default);
    while (gpos.MoveNext()) byPredicate++;
    gpos.Dispose();
    Console.WriteLine($"  {when}: quad count {store.GetStatistics().QuadCount}; s3 by subject (GSPO) {bySubject}; by predicate (GPOS) {byPredicate}");
}

void SecondLoad(string dir, bool bulkMode)
{
    try
    {
        using var store = new QuadStore(dir, null, null, new StorageOptions { BulkMode = bulkMode });
        store.BeginBatch();
        store.AddCurrentBatched("<urn:probe:s3>", P, "\"three\"");
        store.CommitBatch();
        store.FlushToDisk();
        Console.WriteLine($"  second load, bulk mode {bulkMode}: taken");
    }
    catch (Exception e)
    {
        Console.WriteLine($"  second load, bulk mode {bulkMode}: {e.GetType().Name}: {e.Message.Split('.')[0]}");
    }
}

try
{
    foreach (var (name, atoms, bulkMode) in new[]
    {
        ("hash-bulk", AtomStoreImplementation.Hash, true),
        ("hash-session", AtomStoreImplementation.Hash, false),
        ("sorted-bulk", AtomStoreImplementation.Sorted, true),
        ("sorted-session", AtomStoreImplementation.Sorted, false),
    })
    {
        Console.WriteLine($"{name}: a Reference store with a {atoms} atom store, loaded once");
        var dir = Load(name, atoms);
        Show(dir, "after the first load");
        SecondLoad(dir, bulkMode);
        Show(dir, "after the second load");
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
