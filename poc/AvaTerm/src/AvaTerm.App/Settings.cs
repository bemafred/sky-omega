using System.Text.Json;
using AvaTerm.Core;

namespace AvaTerm.App;

/// <summary>
/// User settings, read once at startup from settings.json in the per-user config directory
/// (%APPDATA%\AvaTerm, ~/.config/AvaTerm, ~/Library/Application Support/AvaTerm).
/// Every property is optional in the file.
/// </summary>
public sealed class Settings
{
    public string FontFamily { get; set; } =
        "Cascadia Mono, Cascadia Code, JetBrains Mono, Menlo, SF Mono, Consolas, DejaVu Sans Mono, Ubuntu Mono, Liberation Mono, monospace";
    public double FontSize { get; set; } = 14;
    public int ScrollbackLines { get; set; } = 10_000;
    /// <summary>Program started in each tab; null uses the login shell (pwsh/powershell/cmd on Windows).</summary>
    public string? Shell { get; set; }
    public string[] ShellArgs { get; set; } = [];
    public bool CopyOnSelect { get; set; } = true;
    /// <summary>A plain click on the line being edited moves the shell cursor there.</summary>
    public bool ClickToMoveCursor { get; set; } = true;
    /// <summary>macOS: send ESC-prefixed keys for Option instead of letting it compose characters.</summary>
    public bool MacOptionIsMeta { get; set; }
    public bool CursorBlink { get; set; } = true;
    public int WheelLines { get; set; } = 3;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaTerm", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), options) ?? new Settings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must not keep the terminal from starting.
        }
        return new Settings();
    }
}

public sealed class ColorTheme
{
    public uint Foreground = 0xD4D7DD;
    public uint Background = 0x14171C;
    public uint Cursor = 0xE8E8E8;
    public uint Selection = 0x2D4F7C;
    public uint Chrome = 0x0E1014;
    public readonly uint[] Palette = new uint[256];

    public ColorTheme()
    {
        uint[] ansi =
        [
            0x1D2026, 0xE06C75, 0x98C379, 0xE5C07B, 0x3A7BD5, 0xC678DD, 0x56B6C2, 0xC8CCD4,
            0x5C6370, 0xF0838C, 0xA9D68C, 0xF0D08F, 0x7CC0FF, 0xD691EA, 0x6CCBD8, 0xFFFFFF,
        ];
        ansi.CopyTo(Palette, 0);
        for (int i = 0; i < 216; i++)
        {
            uint Level(int c) => c == 0 ? 0u : (uint)(55 + 40 * c);
            Palette[16 + i] = Level(i / 36) << 16 | Level(i / 6 % 6) << 8 | Level(i % 6);
        }
        for (int i = 0; i < 24; i++)
        {
            uint v = (uint)(8 + 10 * i);
            Palette[232 + i] = v << 16 | v << 8 | v;
        }
    }

    public uint Resolve(uint color, bool foreground)
    {
        if (TermColor.IsIndexed(color)) return Palette[TermColor.Index(color)];
        if (TermColor.IsRgb(color)) return TermColor.RgbValue(color);
        return foreground ? Foreground : Background;
    }

    public static uint Blend(uint a, uint b, double t)
    {
        static uint Mix(uint x, uint y, double t) => (uint)Math.Round(x + (y - (double)x) * t);
        return Mix(a >> 16 & 0xFF, b >> 16 & 0xFF, t) << 16
             | Mix(a >> 8 & 0xFF, b >> 8 & 0xFF, t) << 8
             | Mix(a & 0xFF, b & 0xFF, t);
    }
}
