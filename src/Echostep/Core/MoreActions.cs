using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Echostep.Core;

// Actions under the More button: Find image, Type text, Set variable, Run, Message / sound.

/// <summary>
/// Things a macro can ask of the computer beyond mouse and keyboard input: the clipboard, starting
/// programs, message boxes and sounds. The Windows implementation lives in the Win layer.
/// </summary>
public interface IHostServices
{
    string? GetClipboard();
    void SetClipboard(string text);
    /// <summary>Opens a program, file, folder or URL; with <paramref name="wait"/>, returns when the program has closed.</summary>
    void Run(string target, string? arguments, bool wait, CancellationToken ct);
    /// <summary>Shows a message on top of everything. For a question, returns true for Yes; otherwise always true.</summary>
    bool ShowMessage(string text, bool question);
    void PlaySound(MessageSound sound);
}

// ===================================================================================================
// Find image
// ===================================================================================================

/// <summary>
/// A small picture stored inside a macro: "width,height,base64", where the base64 is the deflated
/// R, G, B bytes row by row. No image library is needed to read or write it.
/// </summary>
public sealed class ImagePattern
{
    public const int MaxSide = 400;

    public int Width { get; }
    public int Height { get; }
    /// <summary>0x00RRGGBB, row by row.</summary>
    public int[] Pixels { get; }

    public ImagePattern(int width, int height, int[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height) throw new ArgumentException("Bad image size.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Cuts a rectangle out of a capture.</summary>
    public static ImagePattern From(ScreenImage img, int x, int y, int width, int height)
    {
        var px = new int[width * height];
        for (var row = 0; row < height; row++)
            Array.Copy(img.Pixels, (y - img.Y + row) * img.Width + (x - img.X), px, row * width, width);
        return new ImagePattern(width, height, px);
    }

    public string Encode()
    {
        var raw = new byte[Pixels.Length * 3];
        for (var i = 0; i < Pixels.Length; i++)
        {
            raw[i * 3] = (byte)(Pixels[i] >> 16);
            raw[i * 3 + 1] = (byte)(Pixels[i] >> 8);
            raw[i * 3 + 2] = (byte)Pixels[i];
        }
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return $"{Width},{Height},{Convert.ToBase64String(ms.ToArray())}";
    }

    public static bool TryDecode(string? text, out ImagePattern? pattern)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) ||
            w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) return false;
        try
        {
            using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(parts[2])), CompressionMode.Decompress);
            var raw = new byte[w * h * 3];
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) return false;
                read += n;
            }
            var px = new int[w * h];
            for (var i = 0; i < px.Length; i++) px[i] = (raw[i * 3] << 16) | (raw[i * 3 + 1] << 8) | raw[i * 3 + 2];
            pattern = new ImagePattern(w, h, px);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return false;
        }
    }
}

public static class ImageSearch
{
    /// <summary>
    /// Screen position of the top-left corner of the first place (left to right, top to bottom) where the
    /// pattern appears in the capture, every pixel within <paramref name="tolerance"/> on each channel; or null.
    /// </summary>
    public static (int X, int Y)? Find(ScreenImage hay, ImagePattern needle, int tolerance)
    {
        int hw = hay.Width, hh = hay.Height, nw = needle.Width, nh = needle.Height;
        if (nw > hw || nh > hh) return null;
        int[] h = hay.Pixels, n = needle.Pixels;
        // Two quick probes (first and middle pixel) reject almost every position before the full compare.
        var p0 = n[0];
        int midX = nw / 2, midY = nh / 2, pm = n[midY * nw + midX];
        for (var y = 0; y <= hh - nh; y++)
        {
            var rowStart = y * hw;
            for (var x = 0; x <= hw - nw; x++)
            {
                if (!Close(h[rowStart + x], p0, tolerance)) continue;
                if (!Close(h[(y + midY) * hw + x + midX], pm, tolerance)) continue;
                if (Matches(h, hw, x, y, n, nw, nh, tolerance)) return (hay.X + x, hay.Y + y);
            }
        }
        return null;
    }

    private static bool Matches(int[] h, int hw, int x, int y, int[] n, int nw, int nh, int tol)
    {
        for (var row = 0; row < nh; row++)
        {
            int hi = (y + row) * hw + x, ni = row * nw;
            for (var col = 0; col < nw; col++)
                if (!Close(h[hi + col], n[ni + col], tol)) return false;
        }
        return true;
    }

    private static bool Close(int a, int b, int tol)
    {
        if (a == b) return true;
        if (tol <= 0) return false;
        return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol &&
               Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol &&
               Math.Abs((a & 255) - (b & 255)) <= tol;
    }
}

/// <summary>
/// Looks for a picture on the screen (or in a zone). When it is found, it can move to or click it, and
/// stores where it was in the variables foundX and foundY. It can keep looking until a timeout, or check once.
/// </summary>
public sealed class FindImageAction : MacroAction
{
    /// <summary>The picture, as <see cref="ImagePattern.Encode"/> writes it.</summary>
    public string Image { get; set; } = "";

    /// <summary>Zone to search in screen pixels. A width or height of 0 searches the entire screen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Y { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Width { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Height { get; set; }

    /// <summary>How far each color channel may differ (0–255). A little helps with anti-aliased text.</summary>
    public int Tolerance { get; set; } = 24;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ChangeMouseAction Mouse { get; set; } = ChangeMouseAction.None;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ChangeAnchor Anchor { get; set; }

    /// <summary>Look once instead of waiting; OnTimeout is then where to go when it is not there (empty: next row).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Once { get; set; }

    public int TimeoutMs { get; set; } = 10_000;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OnTimeout { get; set; } = Labels.Stop;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    [JsonIgnore] public bool EntireScreen => Width <= 0 || Height <= 0;

    [JsonIgnore] public (int W, int H)? ImageSize => ImagePattern.TryDecode(Image, out var p) ? (p!.Width, p.Height) : null;

    [JsonIgnore] public override string DisplayName => "Find image";

    [JsonIgnore]
    public override string Parameters =>
        (ImageSize is { } s ? $"{s.W} × {s.H} image" : "(no image)") +
        (EntireScreen ? " on screen" : $" in zone {X}, {Y}, {Width} × {Height}") +
        (Mouse == ChangeMouseAction.None ? "" : $", {MouseText(Mouse)}") +
        (Once ? $", check once, else → {Show(OnTimeout)}" : TimeoutText(TimeoutMs, OnTimeout)) + ThenText(Then);

    internal static string MouseText(ChangeMouseAction m) => m switch
    {
        ChangeMouseAction.Move => "move to it",
        ChangeMouseAction.RightClick => "right-click it",
        ChangeMouseAction.MiddleClick => "middle-click it",
        ChangeMouseAction.DoubleClick => "double-click it",
        _ => "click it",
    };

    private static string Show(string? t) => string.IsNullOrWhiteSpace(t) ? "next" : t.Trim();

    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        (Once ? Opt("If not found", OnTimeout)
            : TimeoutMs > 0 ? [("On timeout", OnTimeoutOrStop(OnTimeout))] : Array.Empty<(string, string)>())
        .Concat(Opt("Then", Then));

    public override void MapTargets(Func<string, string> map) { OnTimeout = MapOpt(OnTimeout, map); Then = MapOpt(Then, map); }
}

// ===================================================================================================
// Type text, Set variable, Run, Message / sound
// ===================================================================================================

/// <summary>Types text as if from the keyboard. {name} is replaced by a variable's value.</summary>
public sealed class TypeTextAction : MacroAction
{
    public string Text { get; set; } = "";

    /// <summary>Pause between characters; 0 types it all at once.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CharDelayMs { get; set; }

    [JsonIgnore] public override string DisplayName => "Type text";
    [JsonIgnore] public override string Parameters =>
        "\"" + (Text.Length > 60 ? Text[..57] + "…" : Text).Replace("\r", "").Replace("\n", "⏎") + "\"" +
        (CharDelayMs > 0 ? $", {CharDelayMs} ms per character" : "");
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.KeyPresses;
}

[JsonConverter(typeof(JsonStringEnumConverter<VariableOperation>))]
public enum VariableOperation
{
    [JsonStringEnumMemberName("set")] Set,
    [JsonStringEnumMemberName("add")] Add,
    [JsonStringEnumMemberName("subtract")] Subtract,
    /// <summary>The variable gets the clipboard's text.</summary>
    [JsonStringEnumMemberName("fromClipboard")] FromClipboard,
    /// <summary>The clipboard gets the value (Value, with variables filled in).</summary>
    [JsonStringEnumMemberName("toClipboard")] ToClipboard,
}

/// <summary>Sets, adds to or subtracts from a variable, or moves text between a variable and the clipboard.</summary>
public sealed class SetVariableAction : MacroAction
{
    public string Name { get; set; } = "count";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VariableOperation Operation { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; } = "0";

    [JsonIgnore] public override string DisplayName => "Set variable";
    [JsonIgnore] public override string Parameters => Operation switch
    {
        VariableOperation.Add => $"{Name} + {Value}",
        VariableOperation.Subtract => $"{Name} − {Value}",
        VariableOperation.FromClipboard => $"{Name} = clipboard",
        VariableOperation.ToClipboard => $"clipboard = \"{Value}\"",
        _ => $"{Name} = \"{Value}\"",
    };
}

/// <summary>Opens a program, file, folder or web address.</summary>
public sealed class RunAction : MacroAction
{
    public string Target { get; set; } = "notepad.exe";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    /// <summary>Continue only once the program has closed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Wait { get; set; }

    [JsonIgnore] public override string DisplayName => "Run / open";
    [JsonIgnore] public override string Parameters =>
        Target + (string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Arguments) + (Wait ? ", wait until it closes" : "");
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageSound>))]
public enum MessageSound
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("beep")] Beep,
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageBoxKind>))]
public enum MessageBoxKind
{
    /// <summary>Only the sound.</summary>
    [JsonStringEnumMemberName("none")] None,
    /// <summary>A message with OK; playback waits until it is closed.</summary>
    [JsonStringEnumMemberName("ok")] Ok,
    /// <summary>A Yes/No question: Yes goes to Then, No to Else.</summary>
    [JsonStringEnumMemberName("yesNo")] YesNo,
}

/// <summary>Plays a sound, shows a message, or asks a Yes/No question.</summary>
public sealed class MessageAction : MacroAction
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; } = "Done!";

    public MessageBoxKind Box { get; set; } = MessageBoxKind.Ok;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MessageSound Sound { get; set; } = MessageSound.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Then { get; set; }
    [JsonPropertyName("else")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Else { get; set; }

    [JsonIgnore] public override string DisplayName => Box switch
    {
        MessageBoxKind.None => "Sound",
        MessageBoxKind.YesNo => "Ask",
        _ => "Message",
    };

    [JsonIgnore] public override string Parameters =>
        (Box == MessageBoxKind.None ? "" : $"\"{Text}\"") +
        (Sound == MessageSound.None ? "" : (Box == MessageBoxKind.None ? "" : ", ") + Sound.ToString().ToLowerInvariant() + " sound") +
        (Box == MessageBoxKind.YesNo ? $", yes → {Show(Then)}, no → {Show(Else)}" : ThenText(Then));

    private static string Show(string? t) => string.IsNullOrWhiteSpace(t) ? "next" : t.Trim();

    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        Opt("Then", Then).Concat(Box == MessageBoxKind.YesNo ? Opt("Else", Else) : []);
    public override void MapTargets(Func<string, string> map) { Then = MapOpt(Then, map); Else = MapOpt(Else, map); }
}

// ===================================================================================================
// Variables
// ===================================================================================================

public static partial class Variables
{
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <summary>Names filled in by Echostep itself.</summary>
    public static readonly string[] BuiltIn = ["date", "time", "clipboard", "foundX", "foundY"];

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name.Trim());

    /// <summary>Replaces each {name} whose value <paramref name="lookup"/> knows; others stay as typed.</summary>
    public static string Expand(string? text, Func<string, string?> lookup) =>
        string.IsNullOrEmpty(text) ? "" : Reference().Replace(text, m => lookup(m.Groups[1].Value) ?? m.Value);

    public static bool TryNumber(string? s, out double value) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string FormatNumber(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Compares two values: as numbers when both are numbers, otherwise as text (case-insensitive).</summary>
    public static bool Compare(string? left, VarCompare op, string? right)
    {
        left ??= "";
        right ??= "";
        if (op == VarCompare.Contains) return left.Contains(right, StringComparison.OrdinalIgnoreCase);
        int c = TryNumber(left, out var a) && TryNumber(right, out var b)
            ? a.CompareTo(b)
            : string.Compare(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            VarCompare.NotEqual => c != 0,
            VarCompare.Less => c < 0,
            VarCompare.Greater => c > 0,
            _ => c == 0,
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<VarCompare>))]
public enum VarCompare
{
    [JsonStringEnumMemberName("equals")] Equal,
    [JsonStringEnumMemberName("notEquals")] NotEqual,
    [JsonStringEnumMemberName("less")] Less,
    [JsonStringEnumMemberName("greater")] Greater,
    [JsonStringEnumMemberName("contains")] Contains,
}
