// MethodMetadata is pure file-based PE-metadata reading, so it is unit-testable WITHOUT a live
// debuggee: the test reads THIS test assembly's own metadata for fixture methods whose signatures
// it controls. Deterministic, CI-safe — the same testability bar as SymbolReaderTests. Guards the
// argument-name fidelity fix: a STATIC method's argument 0 is its first declared parameter (NOT
// "this"), and an instance method's argument 0 IS "this" (the EngineSteppingSession.cs positional
// "this"/argN placeholder mislabelled static-method arguments — every top-level-program local
// function and static helper hit it).

using System.Reflection;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class MethodMetadataTests
{
    // Static fixture: no receiver, so argument 0 is the first declared parameter (seed), not "this".
    private static long StaticFixture(int seed, long factor) => seed * factor;

    // Instance fixture: argument 0 is "this", then the declared parameters. The readonly initialized
    // field keeps it a genuine instance method (can't be made static) without tripping CS0649.
    private readonly int _bias = 10;
    private int InstanceFixture(int delta, int times) => _bias + delta * times;

    private static int TokenOf(string name, BindingFlags flags) =>
        typeof(MethodMetadataTests).GetMethod(name, flags)!.MetadataToken;

    [Fact]
    public void ArgumentNames_StaticMethod_HasNoThis_AndRealParamNames()
    {
        string module = typeof(MethodMetadataTests).Assembly.Location;
        IReadOnlyList<string> names = MethodMetadata.ArgumentNames(
            module, TokenOf(nameof(StaticFixture), BindingFlags.NonPublic | BindingFlags.Static));

        Assert.Equal(new[] { "seed", "factor" }, names);
        Assert.DoesNotContain("this", names);
    }

    [Fact]
    public void ArgumentNames_InstanceMethod_LeadsWithThis_ThenRealParamNames()
    {
        string module = typeof(MethodMetadataTests).Assembly.Location;
        IReadOnlyList<string> names = MethodMetadata.ArgumentNames(
            module, TokenOf(nameof(InstanceFixture), BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.Equal(new[] { "this", "delta", "times" }, names);
    }

    [Fact]
    public void ArgumentNames_NonMethodToken_ReturnsEmpty()
    {
        string module = typeof(MethodMetadataTests).Assembly.Location;
        // 0x02xxxxxx is a TypeDef token, not an mdMethodDef.
        Assert.Empty(MethodMetadata.ArgumentNames(module, 0x02000001));
    }

    [Fact]
    public void ArgumentNames_MissingModule_ReturnsEmpty()
    {
        Assert.Empty(MethodMetadata.ArgumentNames("/no/such/module.dll", 0x06000001));
    }
}
