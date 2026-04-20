#!/usr/bin/env -S dotnet

// ADR-028 rehash-on-grow proof of concept.
//
// Validates the rehash-swap concurrency model under the ADR-020 single-writer
// contract — not a production implementation.
//
// Model:
//   - Hash table maps hash(string) -> atomId
//   - Atoms are stored in an append-only array (mimics Mercury's mmap'd atom file:
//     atomIds are stable across rehashes, storage backing doesn't resize).
//   - Writer serializes on _writerLock (ADR-020 single-writer).
//   - Readers use Volatile.Read on _current to snapshot the live bucket array.
//   - Rehash allocates new bucket array, re-inserts via stored (hash, atomId),
//     swaps via Volatile.Write(_current = newTable). Old bucket array is
//     eligible for GC once no reader holds a reference.
//
// Questions the spike answers:
//   1. Does rehash preserve all prior (string -> atomId) mappings? (Test 1)
//   2. Do concurrent readers see consistent snapshots across many rehashes
//      without lost lookups or torn reads? (Test 2)
//
// What the spike does NOT validate:
//   - File-based crash safety (production rehash must be durable — atomic rename).
//   - Per-bucket hash storage (production stores hashes so rehash doesn't
//     recompute; spike recomputes for simplicity).
//   - Integration with AtomStore's actual read/write paths.

using System.Diagnostics;

int exitCode = 0;

// ----- Test 1: Correctness across many rehashes -----
Console.WriteLine("Test 1 — 10 M inserts with forced rehashes from 4 K starting buckets");
{
    var table = new AtomHashTable(initialCapacity: 1 << 12);
    var strings = new string[10_000_000];
    var insertSw = Stopwatch.StartNew();
    for (int i = 0; i < strings.Length; i++)
    {
        var s = $"http://wikidata.org/entity/Q{i}";
        strings[i] = s;
        var id = table.GetOrAdd(s);
        if (id != i) { Console.WriteLine($"  FAIL: expected id {i}, got {id}"); return 1; }
    }
    insertSw.Stop();
    Console.WriteLine($"  Insert:    {insertSw.Elapsed.TotalSeconds,6:F2} s  ({strings.Length / insertSw.Elapsed.TotalSeconds / 1000:F0} K/sec)");
    Console.WriteLine($"  Buckets:   {table.Capacity:N0}  (started at 4,096)");
    Console.WriteLine($"  Rehashes:  {table.RehashCount}");

    var verifySw = Stopwatch.StartNew();
    for (int i = 0; i < strings.Length; i++)
    {
        var found = table.Find(strings[i]);
        if (found != i) { Console.WriteLine($"  FAIL: lookup at {i} returned {found}"); return 1; }
    }
    verifySw.Stop();
    Console.WriteLine($"  Verify:    {verifySw.Elapsed.TotalSeconds,6:F2} s  ({strings.Length / verifySw.Elapsed.TotalSeconds / 1000:F0} K/sec)");

    // Miss path: strings we never inserted should return -1
    for (int i = 0; i < 1000; i++)
    {
        var missing = $"http://wikidata.org/missing/Q{i}";
        if (table.Find(missing) != -1) { Console.WriteLine($"  FAIL: missing string returned a match"); return 1; }
    }
    Console.WriteLine("  Test 1 PASS\n");
}

// ----- Test 2: Concurrent readers + live writer with rehashes -----
Console.WriteLine("Test 2 — 8 concurrent readers while writer inserts 5 M + rehashes");
{
    var table = new AtomHashTable(initialCapacity: 1 << 10); // 1024 — forces many rehashes
    const int writerTarget = 5_000_000;
    const int preCount = 10_000;
    var writtenStrings = new string?[writerTarget];
    long readerOps = 0;
    long readerErrors = 0;
    long readerMisses = 0;
    bool writerDone = false;

    for (int i = 0; i < preCount; i++)
    {
        var s = $"http://wikidata.org/entity/Q{i}";
        writtenStrings[i] = s;
        table.GetOrAdd(s);
    }

    var readers = new Task[8];
    for (int r = 0; r < readers.Length; r++)
    {
        int readerId = r;
        readers[r] = Task.Run(() =>
        {
            var rng = new Random(readerId * 1000 + 17);
            while (!Volatile.Read(ref writerDone))
            {
                int i = rng.Next(0, writerTarget);
                var s = Volatile.Read(ref writtenStrings[i]);
                if (s == null)
                {
                    Interlocked.Increment(ref readerMisses);
                    continue;
                }
                var id = table.Find(s);
                if (id != i)
                {
                    Interlocked.Increment(ref readerErrors);
                }
                Interlocked.Increment(ref readerOps);
            }
        });
    }

    var writeSw = Stopwatch.StartNew();
    for (int i = preCount; i < writerTarget; i++)
    {
        var s = $"http://wikidata.org/entity/Q{i}";
        var id = table.GetOrAdd(s);
        if (id != i)
        {
            Volatile.Write(ref writerDone, true);
            Console.WriteLine($"  FAIL: writer got id {id} at i={i}");
            return 1;
        }
        Volatile.Write(ref writtenStrings[i], s);
    }
    writeSw.Stop();
    Volatile.Write(ref writerDone, true);
    Task.WaitAll(readers);

    Console.WriteLine($"  Writer:    {writerTarget:N0} inserts in {writeSw.Elapsed.TotalSeconds:F2} s  ({writerTarget / writeSw.Elapsed.TotalSeconds / 1000:F0} K/sec)");
    Console.WriteLine($"  Buckets:   {table.Capacity:N0}");
    Console.WriteLine($"  Rehashes:  {table.RehashCount}");
    Console.WriteLine($"  Readers:   {readerOps:N0} successful ops, {readerMisses:N0} pre-publish misses (expected), {readerErrors:N0} errors");

    if (readerErrors != 0)
    {
        Console.WriteLine("  Test 2 FAIL — concurrent read saw inconsistent state during rehash");
        exitCode = 1;
    }
    else
    {
        Console.WriteLine("  Test 2 PASS\n");
    }
}

if (exitCode == 0)
    Console.WriteLine("ADR-028 rehash-on-grow spike: ALL TESTS PASSED");
else
    Console.WriteLine("ADR-028 rehash-on-grow spike: FAILURES DETECTED");

return exitCode;

// ============================================================================

sealed class AtomHashTable
{
    private const int MaxAtoms = 10_000_000;

    sealed class Table
    {
        public readonly long[] Buckets;
        public readonly long Mask;
        public int Count;
        public Table(int capacity)
        {
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be power of 2");
            Buckets = new long[capacity];
            Array.Fill(Buckets, -1L);
            Mask = capacity - 1;
        }
    }

    private readonly string?[] _atoms = new string?[MaxAtoms];
    private int _atomCount;
    private Table _current;
    private readonly object _writerLock = new();
    private int _rehashCount;

    public AtomHashTable(int initialCapacity)
    {
        _current = new Table(initialCapacity);
    }

    public long GetOrAdd(string s)
    {
        lock (_writerLock)
        {
            var existing = FindInternal(_current, s);
            if (existing >= 0) return existing;

            if ((long)(_current.Count + 1) * 4 > (long)_current.Buckets.Length * 3)
                Rehash();

            var atomId = (long)_atomCount;
            _atoms[atomId] = s;
            Volatile.Write(ref _atomCount, _atomCount + 1);

            InsertInto(_current, s.GetHashCode(), atomId);
            _current.Count++;
            return atomId;
        }
    }

    public long Find(string s)
    {
        var table = Volatile.Read(ref _current);
        return FindInternal(table, s);
    }

    private long FindInternal(Table table, string s)
    {
        var hash = s.GetHashCode();
        var idx = hash & table.Mask;
        for (long probe = 0; probe < table.Buckets.Length; probe++)
        {
            var bucket = (idx + probe) & table.Mask;
            var id = Volatile.Read(ref table.Buckets[bucket]);
            if (id == -1) return -1;
            var stored = Volatile.Read(ref _atoms[id]);
            if (stored == s) return id;
        }
        return -1;
    }

    private void Rehash()
    {
        var newTable = new Table(_current.Buckets.Length * 2);
        for (int i = 0; i < _current.Buckets.Length; i++)
        {
            var id = _current.Buckets[i];
            if (id == -1) continue;
            var s = _atoms[id]!;
            InsertInto(newTable, s.GetHashCode(), id);
        }
        newTable.Count = _current.Count;
        Volatile.Write(ref _current, newTable);
        _rehashCount++;
    }

    private static void InsertInto(Table t, int hash, long id)
    {
        var idx = hash & t.Mask;
        for (long probe = 0; probe < t.Buckets.Length; probe++)
        {
            var bucket = (idx + probe) & t.Mask;
            if (t.Buckets[bucket] == -1)
            {
                Volatile.Write(ref t.Buckets[bucket], id);
                return;
            }
        }
        throw new InvalidOperationException("Hash table full — should have rehashed");
    }

    public int Count => _current.Count;
    public int Capacity => _current.Buckets.Length;
    public int RehashCount => _rehashCount;
}
