using System;
using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-058 D7: the atom store checks its header against the atom records at open. The tests build
/// the on-disk states the elite incident showed by editing a cleanly written store's files: a header
/// that is behind the data, and a chain of records that newer atoms were written over.
/// </summary>
public class AtomStoreRecoveryTests : IDisposable
{
    // atoms.atoms header: dataPosition (0), nextAtomId (8), atomCount (16), totalBytes (24), magic (32).
    private const int DataStart = 1024;

    private readonly string _dir;
    private readonly string _base;

    public AtomStoreRecoveryTests()
    {
        var tempPath = TempPath.Test("atom-recovery");
        tempPath.MarkOwnership();
        _dir = tempPath;
        _base = Path.Combine(_dir, "atoms");
    }

    public void Dispose() => TempPath.SafeCleanup(_dir);

    [Fact]
    public void HeaderBehindTheData_OpenTakesTheAtomsAndReusesNoId()
    {
        WriteAtoms("alpha", "beta", "gamma");
        ResetHeaderToNew(); // elite's header: never saved after creation

        using var store = new HashAtomStore(_base);

        Assert.Equal(3, store.AtomsRecoveredAtOpen);
        Assert.Null(store.WriteRefusal);
        Assert.Equal("alpha", store.GetAtomString(1));
        Assert.Equal("gamma", store.GetAtomString(3));
        Assert.Equal(2, store.Intern("beta"));   // found, not interned again
        Assert.Equal(4, store.Intern("delta"));  // a new ID past every recorded atom
        Assert.Equal("alpha", store.GetAtomString(1));
    }

    [Fact]
    public void HeaderBehindTheData_TheTakenAtomsAreSavedAtOpen()
    {
        WriteAtoms("alpha", "beta");
        ResetHeaderToNew();
        using (var first = new HashAtomStore(_base))
            Assert.Equal(2, first.AtomsRecoveredAtOpen);

        using var second = new HashAtomStore(_base);
        Assert.Equal(0, second.AtomsRecoveredAtOpen);
        Assert.Equal(2, second.AtomCount);
    }

    [Fact]
    public void AChainThatBreaks_RefusesNewAtoms()
    {
        // ID reuse leaves a chain that continues and then breaks: the newer atoms start at the
        // header's position, and the offset entries past them still point into the older layout.
        WriteAtoms("alpha", "beta", "gamma");
        ResetHeaderToNew();
        WriteOffset(3, DataStart + 5); // atom 3 no longer starts where atom 2 ends

        using var store = new HashAtomStore(_base);

        Assert.Equal(2, store.AtomsRecoveredAtOpen);
        Assert.NotNull(store.WriteRefusal);
        Assert.Contains("damaged", store.WriteRefusal);
        Assert.Equal("beta", store.GetAtomString(2));          // reads still work
        Assert.Throws<InvalidOperationException>(() => store.Intern("delta"));
    }

    [Fact]
    public void AStoreThatRefusesAtoms_RefusesEveryWrite()
    {
        var storeDir = Path.Combine(_dir, "quadstore");
        using (var store = new QuadStore(storeDir))
            store.AddCurrent("<urn:s>", "<urn:p>", "\"o\"", "<urn:g>");

        var atoms = Path.Combine(storeDir, "atoms");
        ResetHeaderToNew(atoms);
        WriteOffset(3, DataStart + 5, atoms);

        using var reopened = new QuadStore(storeDir);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            reopened.AddCurrent("<urn:s>", "<urn:p>", "\"o\"", "<urn:g>"));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void ALeftoverOffsetPastTheHeader_IsIgnored()
    {
        // Clear before ADR-058 left offset entries in place. An entry right after the header that
        // does not continue the chain is such a leftover, not an atom of this store.
        WriteAtoms("alpha", "beta");
        WriteOffset(3, DataStart + 5);

        using var store = new HashAtomStore(_base);

        Assert.Equal(0, store.AtomsRecoveredAtOpen);
        Assert.Null(store.WriteRefusal);
        Assert.Equal(3, store.Intern("gamma"));
    }

    [Fact]
    public void Clear_ThenReopen_BringsNoClearedAtomBack()
    {
        using (var store = new HashAtomStore(_base))
        {
            store.Intern("alpha");
            store.Intern("beta");
            store.Clear();
            store.Intern("gamma");
        }

        using var reopened = new HashAtomStore(_base);
        Assert.Equal(0, reopened.AtomsRecoveredAtOpen);
        Assert.Equal(1, reopened.AtomCount);
        Assert.Equal("gamma", reopened.GetAtomString(1));
        Assert.Equal(0, reopened.GetAtomId("alpha"));
    }

    // ---- file surgery ----

    private void WriteAtoms(params string[] values)
    {
        using var store = new HashAtomStore(_base);
        foreach (var v in values)
            store.Intern(v);
    }

    private void ResetHeaderToNew(string? basePath = null)
    {
        using var fs = new FileStream((basePath ?? _base) + ".atoms", FileMode.Open, FileAccess.ReadWrite);
        using var w = new BinaryWriter(fs);
        w.Write((long)DataStart); // dataPosition
        w.Write(0L);              // nextAtomId
        w.Write(0L);              // atomCount
        w.Write(0L);              // totalBytes
    }

    private void WriteOffset(long atomId, long offset, string? basePath = null)
    {
        using var fs = new FileStream((basePath ?? _base) + ".offsets", FileMode.Open, FileAccess.ReadWrite);
        fs.Position = atomId * sizeof(long);
        using var w = new BinaryWriter(fs);
        w.Write(offset);
    }
}
