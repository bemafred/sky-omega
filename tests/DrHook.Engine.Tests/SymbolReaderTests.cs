// SymbolReader is pure file-based PDB reading, so it is unit-testable WITHOUT a live debuggee:
// the test opens THIS test assembly's own Portable PDB and reads back the symbols of a fixture
// method whose locals and source it controls. Deterministic, CI-safe — the testability bar the
// DrHook.Engine substrate is held to (docs/limits/drhook-testability.md).

using System.Reflection;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class SymbolReaderTests
{
    // Fixture with named locals and a multi-line body so the PDB has both local scopes and
    // sequence points to read back. Kept simple and used so Debug builds preserve the locals.
    private static long ComputeFixture(int seed)
    {
        int doubled = seed * 2;
        long widened = doubled + 1L;
        return widened;
    }

    private static int FixtureToken =>
        typeof(SymbolReaderTests)
            .GetMethod(nameof(ComputeFixture), BindingFlags.NonPublic | BindingFlags.Static)!
            .MetadataToken;

    [Fact]
    public void TryOpen_FindsThisAssemblysPortablePdb()
    {
        using SymbolReader? sym = SymbolReader.TryOpen(typeof(SymbolReaderTests).Assembly.Location);
        Assert.NotNull(sym);
    }

    [Fact]
    public void GetLocalNames_ReadsNamedLocals()
    {
        using SymbolReader? sym = SymbolReader.TryOpen(typeof(SymbolReaderTests).Assembly.Location);
        Assert.NotNull(sym);

        IReadOnlyList<LocalName> locals = sym!.GetLocalNames(FixtureToken);

        Assert.Contains(locals, l => l.Name == "doubled");
        Assert.Contains(locals, l => l.Name == "widened");
    }

    [Fact]
    public void TryGetLine_MapsMethodEntryToSource()
    {
        using SymbolReader? sym = SymbolReader.TryOpen(typeof(SymbolReaderTests).Assembly.Location);
        Assert.NotNull(sym);

        SourceLocation? loc = sym!.TryGetLine(FixtureToken, ilOffset: 0);

        Assert.NotNull(loc);
        Assert.EndsWith("SymbolReaderTests.cs", loc!.Value.File);
        Assert.True(loc.Value.Line > 0, "entry should map to a positive source line");
    }

    [Fact]
    public void TryGetLine_NonMethodToken_ReturnsNull()
    {
        using SymbolReader? sym = SymbolReader.TryOpen(typeof(SymbolReaderTests).Assembly.Location);
        Assert.NotNull(sym);

        // 0x02xxxxxx is a TypeDef token, not an mdMethodDef.
        Assert.Null(sym!.TryGetLine(0x02000001, 0));
    }
}
