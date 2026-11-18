using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echostep.Core;

/// <summary>Reads and writes .estep macro files (UTF-8 JSON with a format version). Older .omr and .orec files open too.</summary>
public static class MacroFile
{
    public const string FormatName = "Echostep";
    public const string Extension = ".estep";

    /// <summary>Format names and extensions written before the app was renamed; still accepted when reading.</summary>
    public static readonly string[] LegacyFormatNames = ["OpenMR", "OpenRecorder"];
    public static readonly string[] LegacyExtensions = [".omr", ".orec"];

    /// <summary>Highest file version this build can read.</summary>
    public const int CurrentVersion = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Hand-edited files may put "label" (or anything) before "type".
        AllowOutOfOrderMetadataProperties = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJson = new(Json) { WriteIndented = false };

    private sealed class Document
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Screen { get; set; }
        public List<MacroAction>? Actions { get; set; }
    }

    public static List<MacroAction> Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Loads a macro together with the screen layout it was recorded on (null if none was stored).</summary>
    public static (List<MacroAction> Actions, string? Screen) LoadWithScreen(string path)
    {
        var actions = Parse(File.ReadAllText(path, Encoding.UTF8), out var screen);
        return (actions, screen);
    }

    public static List<MacroAction> Parse(string json) => Parse(json, out _);

    public static List<MacroAction> Parse(string json, out string? screen)
    {
        screen = null;
        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a valid Echostep macro: {ex.Message}", ex);
        }
        if (doc is null) throw new InvalidDataException("The file is empty.");
        if (doc.Format is not null && !string.Equals(doc.Format, FormatName, StringComparison.OrdinalIgnoreCase)
            && !LegacyFormatNames.Contains(doc.Format, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"This is a \"{doc.Format}\" file, not an Echostep macro.");
        if (doc.Version > CurrentVersion)
            throw new InvalidDataException(
                $"This macro was saved by a newer Echostep (file version {doc.Version}; this build reads up to {CurrentVersion}).");
        var actions = doc.Actions ?? [];
        foreach (var a in actions) Normalize(a);
        screen = string.IsNullOrWhiteSpace(doc.Screen) ? null : doc.Screen;
        return actions;
    }

    public static void Save(string path, IReadOnlyList<MacroAction> actions, string? screen = null)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(actions, screen), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Serializes with one action per line, which keeps diffs in git readable.
    /// Files that only use v1 features are written as version 1 so v1 builds can still open them.
    /// </summary>
    public static string Serialize(IReadOnlyList<MacroAction> actions, string? screen = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"format\": \"{FormatName}\",\n");
        sb.Append($"  \"version\": {RequiredVersion(actions)},\n");
        if (!string.IsNullOrWhiteSpace(screen)) sb.Append($"  \"screen\": {JsonSerializer.Serialize(screen, CompactJson)},\n");
        sb.Append("  \"actions\": [");
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (string.IsNullOrWhiteSpace(a.Label)) a.Label = null;
            sb.Append(i == 0 ? "\n    " : ",\n    ");
            sb.Append(JsonSerializer.Serialize(a, CompactJson));
        }
        sb.Append(actions.Count == 0 ? "]\n" : "\n  ]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>1 if only v1 features are used, otherwise 2.</summary>
    public static int RequiredVersion(IEnumerable<MacroAction> actions) => actions.All(IsV1) ? 1 : 2;

    private static bool IsV1(MacroAction a)
    {
        if (a.Comment is not null || a.Disabled || a.Breakpoint || a.RowColor is not null || a.Group is not null) return false;
        if (a.GetTargets().Any(t => t.Target.Equals(Labels.Next, StringComparison.OrdinalIgnoreCase))) return false;
        if (a is PointAction p && !string.IsNullOrWhiteSpace(p.Window)) return false;
        if (a is PixelActionBase px && px.Match != MatchMode.Channel) return false;
        return a switch
        {
            ClickAction c => c.Button is MouseButton.Left or MouseButton.Right && c.Kind == ClickKind.Click && !c.Relative && !c.Wiggle,
            MoveAction m => m.FromX is null && m.FromY is null && m.TravelMs == 0 && !m.Relative,
            KeyAction k => k.Option == KeyOption.Press && k.Count == 1,
            WaitAction => true,
            WaitPixelAction w => w.Then is null,
            RepeatAction r => r.Mode == RepeatMode.Count && r.After is null,
            GotoAction => true,
            _ => false,
        };
    }

    /// <summary>Tidies values that hand-edited files may leave odd.</summary>
    internal static void Normalize(MacroAction a)
    {
        if (string.IsNullOrWhiteSpace(a.Label)) a.Label = null;
        else a.Label = a.Label.Trim();
        if (a is PointAction p && string.IsNullOrWhiteSpace(p.Window)) p.Window = null;
        if (a is WaitUntilAction wu && WaitUntilAction.TryParseTime(wu.Time, out var tod)) wu.Time = WaitUntilAction.Format(tod);
        if (a is KeyAction k) k.Key = KeyNames.Normalize(k.Key ?? "");
        if (string.IsNullOrWhiteSpace(a.Comment)) a.Comment = null;
        if (string.IsNullOrWhiteSpace(a.RowColor)) a.RowColor = null;
        if (string.IsNullOrWhiteSpace(a.Group)) { a.Group = null; a.GroupColor = null; }
        if (a is WaitPixelAction w && string.IsNullOrWhiteSpace(w.OnTimeout)) w.OnTimeout = Labels.Stop;
        if (a is WaitPixelAction w2 && string.IsNullOrWhiteSpace(w2.Then)) w2.Then = null;
        if (a is WaitPixelChangeAction wc2 && string.IsNullOrWhiteSpace(wc2.Then)) wc2.Then = null;
        if (a is WaitScreenChangeAction ws) { if (string.IsNullOrWhiteSpace(ws.OnTimeout)) ws.OnTimeout = Labels.End; if (string.IsNullOrWhiteSpace(ws.Then)) ws.Then = null; }
        if (a is WindowFocusAction wf) { if (string.IsNullOrWhiteSpace(wf.OnTimeout)) wf.OnTimeout = Labels.End; if (string.IsNullOrWhiteSpace(wf.Then)) wf.Then = null; }
        if (a is RepeatAction rp && string.IsNullOrWhiteSpace(rp.After)) rp.After = null;
        if (a is WaitPixelChangeAction wc && string.IsNullOrWhiteSpace(wc.OnTimeout)) wc.OnTimeout = Labels.Stop;
        if (a is IfAction cond)
        {
            if (WaitUntilAction.TryParseTime(cond.Time, out var t1)) cond.Time = WaitUntilAction.Format(t1);
            if (WaitUntilAction.TryParseTime(cond.Time2, out var t2)) cond.Time2 = WaitUntilAction.Format(t2);
            if (string.IsNullOrWhiteSpace(cond.Then)) cond.Then = null;
            if (string.IsNullOrWhiteSpace(cond.Else)) cond.Else = null;
            cond.Days = cond.Days?.Select(d => d.Trim().ToLowerInvariant()).Distinct()
                .OrderBy(d => Array.IndexOf(IfAction.DayKeys, d)).ToList();
        }
        if (a is IfPixelAction ip)
        {
            if (string.IsNullOrWhiteSpace(ip.Then)) ip.Then = null;
            if (string.IsNullOrWhiteSpace(ip.Else)) ip.Else = null;
        }
    }

    public static List<MacroAction> CloneAll(IEnumerable<MacroAction> actions) => actions.Select(a => a.Clone()).ToList();
}
