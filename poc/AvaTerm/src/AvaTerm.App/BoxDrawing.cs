using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AvaTerm.App;

/// <summary>
/// Draws box-drawing and block characters as geometry so that borders (tmux panes, TUI frames)
/// join seamlessly from cell to cell regardless of the font's line height.
/// </summary>
internal static class BoxDrawing
{
    // Arms for U+2500..U+254F as left/right/up/down digits: 0 none, 1 light, 2 heavy.
    static readonly string[] Arms = (
        "1100 2200 0011 0022 1100 2200 0011 0022 1100 2200 0011 0022 0101 0201 0102 0202 " +
        "1001 2001 1002 2002 0110 0210 0120 0220 1010 2010 1020 2020 0111 0211 0121 0112 " +
        "0122 0221 0212 0222 1011 2011 1021 1012 1022 2021 2012 2022 1101 2101 1201 2201 " +
        "1102 2102 1202 2202 1110 2110 1210 2210 1120 2120 1220 2220 1111 2111 1211 2211 " +
        "1121 1112 1122 2121 1221 2112 1212 2221 2212 2122 1222 2222 1100 2200 0011 0022").Split(' ');

    static string? ArmsFor(int rune) => rune switch
    {
        >= 0x2500 and <= 0x254F => Arms[rune - 0x2500],
        0x256D => "0101", 0x256E => "1001", 0x256F => "1010", 0x2570 => "0110",   // rounded corners
        0x2574 => "1000", 0x2575 => "0010", 0x2576 => "0100", 0x2577 => "0001",
        0x2578 => "2000", 0x2579 => "0020", 0x257A => "0200", 0x257B => "0002",
        0x257C => "1200", 0x257D => "0012", 0x257E => "2100", 0x257F => "0021",
        _ => null,
    };

    public static bool TryDraw(DrawingContext ctx, int rune, Rect cell, IBrush brush, Color color, double scale)
    {
        if (rune < 0x2500 || rune > 0x259F) return false;
        double Snap(double v) => Math.Round(v * scale) / scale;

        if (rune >= 0x2580)
        {
            double x = cell.X, y = cell.Y, w = cell.Width, h = cell.Height;
            double midX = Snap(x + w / 2), midY = Snap(y + h / 2);
            switch (rune)
            {
                case 0x2580: ctx.FillRectangle(brush, new Rect(x, y, w, midY - y)); return true;
                case >= 0x2581 and <= 0x2588:
                {
                    double top = Snap(y + h * (8 - (rune - 0x2580)) / 8);
                    ctx.FillRectangle(brush, new Rect(x, top, w, y + h - top));
                    return true;
                }
                case >= 0x2589 and <= 0x258F:
                    ctx.FillRectangle(brush, new Rect(x, y, Snap(w * (0x2590 - rune) / 8), h));
                    return true;
                case 0x2590: ctx.FillRectangle(brush, new Rect(midX, y, x + w - midX, h)); return true;
                case >= 0x2591 and <= 0x2593:
                    ctx.FillRectangle(new ImmutableSolidColorBrush(color, 0.25 * (rune - 0x2590)), cell);
                    return true;
                case 0x2594: ctx.FillRectangle(brush, new Rect(x, y, w, Snap(h / 8))); return true;
                case 0x2595:
                {
                    double left = Snap(x + w * 7 / 8);
                    ctx.FillRectangle(brush, new Rect(left, y, x + w - left, h));
                    return true;
                }
            }
            // Quadrants: bit 1 upper-left, 2 upper-right, 4 lower-left, 8 lower-right.
            int mask = rune switch
            {
                0x2596 => 4, 0x2597 => 8, 0x2598 => 1, 0x2599 => 13, 0x259A => 9,
                0x259B => 7, 0x259C => 11, 0x259D => 2, 0x259E => 6, 0x259F => 14,
                _ => 0,
            };
            if (mask == 0) return false;
            if ((mask & 1) != 0) ctx.FillRectangle(brush, new Rect(x, y, midX - x, midY - y));
            if ((mask & 2) != 0) ctx.FillRectangle(brush, new Rect(midX, y, x + w - midX, midY - y));
            if ((mask & 4) != 0) ctx.FillRectangle(brush, new Rect(x, midY, midX - x, y + h - midY));
            if ((mask & 8) != 0) ctx.FillRectangle(brush, new Rect(midX, midY, x + w - midX, y + h - midY));
            return true;
        }

        string? arms = ArmsFor(rune);
        if (arms == null) return false;   // double lines and diagonals stay with the font

        double light = Math.Max(1, Math.Round(cell.Width / 8 * scale)) / scale;
        double Thickness(int weight) => weight == 2 ? light * 2 : weight == 1 ? light : 0;
        int l = arms[0] - '0', r = arms[1] - '0', u = arms[2] - '0', d = arms[3] - '0';

        // Strokes are centred on the pixel grid; each arm runs to the far edge of the crossing stroke.
        double StrokeX(double t) => Snap(cell.X + (cell.Width - t) / 2);
        double StrokeY(double t) => Snap(cell.Y + (cell.Height - t) / 2);
        double joinW = Math.Max(Thickness(u), Thickness(d));
        double joinH = Math.Max(Thickness(l), Thickness(r));
        if (joinW == 0) joinW = light;
        if (joinH == 0) joinH = light;
        double jx = StrokeX(joinW), jy = StrokeY(joinH);

        if (l > 0) ctx.FillRectangle(brush, new Rect(cell.X, StrokeY(Thickness(l)), jx + joinW - cell.X, Thickness(l)));
        if (r > 0) ctx.FillRectangle(brush, new Rect(jx, StrokeY(Thickness(r)), cell.Right - jx, Thickness(r)));
        if (u > 0) ctx.FillRectangle(brush, new Rect(StrokeX(Thickness(u)), cell.Y, Thickness(u), jy + joinH - cell.Y));
        if (d > 0) ctx.FillRectangle(brush, new Rect(StrokeX(Thickness(d)), jy, Thickness(d), cell.Bottom - jy));
        return true;
    }
}
