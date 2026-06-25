// RuntimeConfig reads a target's runtimeconfig.json to learn its .NET runtime MAJOR WITHOUT launching
// it — the pre-flight check that lets DrHook refuse a runtime-version-mismatched launch/attach cleanly
// (finding 86) instead of failing late inside dbgshim with CORDBG_E_DEBUG_COMPONENT_MISSING (0x80131C3C)
// and leaking a suspended spawn. Pure file read, so unit-testable against hand-written runtimeconfig
// fixtures — no debuggee, deterministic, CI-safe.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class RuntimeConfigTests : IDisposable
{
    private readonly string _dir;

    public RuntimeConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "drhook-runtimeconfig-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MajorOfImage_ReadsTfm_Net10()
    {
        string image = Path.Combine(_dir, "app.dll");
        File.WriteAllText(Path.ChangeExtension(image, ".runtimeconfig.json"),
            """{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" } } }""");
        Assert.Equal(10, RuntimeConfig.MajorOfImage(image));
    }

    [Fact]
    public void MajorOfImage_ReadsPreviewFrameworkVersion_Net11()
    {
        // The net11 SDK-11 file-based-app shape: version carries a -preview suffix; the major must still
        // parse to 11 (this is exactly the target that locked the MCP and made net10 targets fail).
        string image = Path.Combine(_dir, "preview.dll");
        File.WriteAllText(Path.ChangeExtension(image, ".runtimeconfig.json"),
            """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0-preview.5.26302.115" } } }""");
        Assert.Equal(11, RuntimeConfig.MajorOfImage(image));
    }

    [Fact]
    public void MajorOfImage_MissingRuntimeConfig_IsNull()
    {
        // No runtimeconfig beside the image -> undetectable -> null. Callers must NOT block on uncertainty,
        // so this null is load-bearing: an unknown target version is allowed through, not refused.
        Assert.Null(RuntimeConfig.MajorOfImage(Path.Combine(_dir, "nonexistent.dll")));
    }

    [Fact]
    public void MajorOfLaunchTarget_PicksTheDllArg_NotTheDotnetMuxer()
    {
        // `dotnet exec X.dll` — the runtime version is X's, not the muxer's. The detector must read
        // X.runtimeconfig.json, so a net9 target launched via the (net11) muxer is correctly seen as net9.
        string dll = Path.Combine(_dir, "target.dll");
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"),
            """{ "runtimeOptions": { "tfm": "net9.0" } }""");
        Assert.Equal(9, RuntimeConfig.MajorOfLaunchTarget("dotnet", new[] { "exec", dll }));
    }

    [Fact]
    public void MajorOfLaunchTarget_BareApphost_ReadsApphostRuntimeConfig()
    {
        // A single-file / bare apphost launched directly (no .dll arg) -> read <apphost>.runtimeconfig.json.
        string apphost = Path.Combine(_dir, "sfapp");
        File.WriteAllText(apphost + ".runtimeconfig.json",
            """{ "runtimeOptions": { "tfm": "net10.0" } }""");
        Assert.Equal(10, RuntimeConfig.MajorOfLaunchTarget(apphost, Array.Empty<string>()));
    }
}
