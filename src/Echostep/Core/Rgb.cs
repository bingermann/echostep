using System.Globalization;
using System.Text.Json.Serialization;

namespace Echostep.Core;

/// <summary>How a read color is compared with a target color.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchMode>))]
public enum MatchMode
{
    /// <summary>Each of R, G and B may differ by at most the tolerance (0–255).</summary>
    [JsonStringEnumMemberName("channel")] Channel,
    /// <summary>Euclidean RGB distance must be at most the tolerance (0–442). Better for gradients.</summary>
    [JsonStringEnumMemberName("distance")] Distance,
}

/// <summary>An RGB color, independent of WinForms/System.Drawing.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>Win32 COLORREF (0x00BBGGRR): red is the low byte.</summary>
    public static Rgb FromColorRef(uint colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    public int ToArgb() => unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>Parses "#RRGGBB", "RRGGBB", "#RGB" or "0xRRGGBB".</summary>
    public static bool TryParse(string? text, out Rgb rgb)
    {
        rgb = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Rgb Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not a color like #2D7DD2.");

    public static int MaxChannelDiff(Rgb a, Rgb b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    public static double Distance(Rgb a, Rgb b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static bool Matches(Rgb actual, Rgb target, int tolerance, MatchMode mode) => mode switch
    {
        MatchMode.Distance => Distance(actual, target) <= tolerance,
        _ => MaxChannelDiff(actual, target) <= tolerance,
    };

    /// <summary>Largest sensible tolerance for a mode (255 per channel, 442 ≈ √(3·255²) for distance).</summary>
    public static int MaxTolerance(MatchMode mode) => mode == MatchMode.Distance ? 442 : 255;
}
