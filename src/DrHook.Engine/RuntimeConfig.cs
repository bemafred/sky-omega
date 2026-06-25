// Read a .NET target's runtime MAJOR version from its runtimeconfig.json — without launching it.
// A debugger process can host the debug components (mscordbi/DAC) of only ONE runtime version
// (finding 86); knowing a target's version up front lets the substrate refuse a mismatched
// launch/attach CLEANLY instead of failing late inside dbgshim with CORDBG_E_DEBUG_COMPONENT_MISSING
// (0x80131C3C) and leaking a suspended spawn. Pure file read — no debuggee, deterministically testable.

using System.Text.Json;

namespace SkyOmega.DrHook.Engine;

public static class RuntimeConfig
{
    /// <summary>The .NET runtime MAJOR a launch target will use, from the <c>runtimeconfig.json</c> beside
    /// its primary assembly — the <c>.dll</c> argument for <c>dotnet exec X.dll</c>, else
    /// <paramref name="program"/> (an apphost). Null when it can't be determined (no runtimeconfig,
    /// unparseable) — callers treat null as "don't block".</summary>
    public static int? MajorOfLaunchTarget(string program, IReadOnlyList<string> args)
    {
        string primary = program;
        foreach (string a in args)
            if (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { primary = a; break; }
        return MajorOfImage(primary);
    }

    /// <summary>The .NET runtime MAJOR from the <c>runtimeconfig.json</c> beside <paramref name="imagePath"/>
    /// (an apphost or a managed <c>.dll</c>); null if absent or unparseable.</summary>
    public static int? MajorOfImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        string config = Path.ChangeExtension(imagePath, ".runtimeconfig.json");
        if (!File.Exists(config)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out JsonElement ro)) return null;
            // Prefer the explicit "tfm" (e.g. "net10.0"); fall back to framework.version ("10.0.0",
            // "11.0.0-preview.5..."). frameworks[] (multiple) takes the first — same major in practice.
            if (ro.TryGetProperty("tfm", out JsonElement tfm) && tfm.GetString() is { } t && MajorOfTfm(t) is { } mt)
                return mt;
            if (ro.TryGetProperty("framework", out JsonElement fw) && fw.TryGetProperty("version", out JsonElement v) && v.GetString() is { } ver)
                return MajorOfVersion(ver);
            if (ro.TryGetProperty("frameworks", out JsonElement fws) && fws.ValueKind == JsonValueKind.Array && fws.GetArrayLength() > 0
                && fws[0].TryGetProperty("version", out JsonElement v0) && v0.GetString() is { } ver0)
                return MajorOfVersion(ver0);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? MajorOfTfm(string tfm)      // "net10.0" -> 10
    {
        ReadOnlySpan<char> s = tfm.AsSpan();
        if (s.StartsWith("net")) s = s[3..];
        int dot = s.IndexOf('.');
        if (dot > 0) s = s[..dot];
        return int.TryParse(s, out int m) ? m : null;
    }

    private static int? MajorOfVersion(string version)  // "10.0.0" / "11.0.0-preview.5..." -> 10 / 11
    {
        int dot = version.IndexOf('.');
        ReadOnlySpan<char> s = dot > 0 ? version.AsSpan(0, dot) : version.AsSpan();
        return int.TryParse(s, out int m) ? m : null;
    }
}
