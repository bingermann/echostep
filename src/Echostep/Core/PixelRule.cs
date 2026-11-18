using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echostep.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RuleMode>))]
public enum RuleMode
{
    /// <summary>Fire when the pixel starts matching the color.</summary>
    [JsonStringEnumMemberName("onMatch")] OnMatch,
    /// <summary>Fire when the pixel stops matching the color.</summary>
    [JsonStringEnumMemberName("onMismatch")] OnMismatch,
    /// <summary>Fire when the pixel changes to a new color (beyond tolerance) and holds it.</summary>
    [JsonStringEnumMemberName("onChange")] OnChange,
}

/// <summary>A background rule: read one pixel on a timer and run actions when it matches, stops matching or changes.</summary>
public sealed class PixelRule
{
    public string Name { get; set; } = "New rule";
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Optional window title; when set, X/Y are relative to its client area.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Window { get; set; }

    public string Color { get; set; } = "#000000";
    public int Tolerance { get; set; } = 12;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MatchMode Match { get; set; } = MatchMode.Channel;

    public RuleMode Mode { get; set; } = RuleMode.OnMatch;
    public int IntervalMs { get; set; } = 50;
    public int HoldForMs { get; set; } = 100;
    public int CooldownMs { get; set; } = 1000;
    public bool Enabled { get; set; } = true;

    /// <summary>Optional .estep macro to run when the rule fires (after <see cref="Actions"/>). Relative paths are beside rules.json.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MacroFile { get; set; }

    public List<MacroAction> Actions { get; set; } = [];

    public PixelRule Clone()
    {
        var r = (PixelRule)MemberwiseClone();
        r.Actions = Core.MacroFile.CloneAll(Actions);
        return r;
    }

    [JsonIgnore] public string PixelText => $"{X}, {Y}{MacroAction.WindowSuffix(Window)}";

    [JsonIgnore] public string ModeText => Mode switch
    {
        RuleMode.OnMismatch => "on mismatch",
        RuleMode.OnChange => "on change",
        _ => "on match",
    };

    /// <summary>Problems that prevent this rule from being watched, or empty.</summary>
    public List<string> Validate()
    {
        var p = new List<string>();
        if (Mode != RuleMode.OnChange && !Rgb.TryParse(Color, out _)) p.Add($"\"{Color}\" is not a color like #2D7DD2.");
        if (Tolerance < 0 || Tolerance > Rgb.MaxTolerance(Match)) p.Add($"Tolerance must be 0–{Rgb.MaxTolerance(Match)}.");
        if (IntervalMs < 5) p.Add("Interval must be at least 5 ms.");
        if (HoldForMs < 0 || CooldownMs < 0) p.Add("Hold and cooldown cannot be negative.");
        var first = MacroValidator.FirstProblem(Actions);
        if (first is not null) p.Add("Actions: " + first);
        if (Actions.Count == 0 && string.IsNullOrWhiteSpace(MacroFile)) p.Add("The rule has no actions.");
        return p;
    }
}

/// <summary>
/// Edge-trigger state of one rule. Fires on the transition into the condition, once the condition
/// has held for HoldForMs, and never twice within CooldownMs.
/// </summary>
public sealed class RuleState
{
    private bool _armed = true;
    private long? _conditionSince;
    private Rgb? _reference;   // onChange: the last stable color
    private Rgb? _candidate;   // onChange: the new color being held

    public Rgb? LastColor { get; private set; }
    public bool LastCondition { get; private set; }
    public long? LastFiredMs { get; private set; }
    public DateTime? LastFiredAt { get; private set; }
    public int FireCount { get; private set; }
    public bool Running { get; set; }

    /// <summary>Set once an unreadable pixel has been logged; cleared when reads work again.</summary>
    public bool UnreadableLogged { get; set; }

    public void Reset()
    {
        _armed = true;
        _conditionSince = null;
        _reference = null;
        _candidate = null;
        LastCondition = false;
        LastColor = null;
    }

    /// <summary>Feeds one reading (null = unreadable). Returns true when the rule should fire now.</summary>
    public bool Update(PixelRule rule, Rgb? reading, long nowMs)
    {
        LastColor = reading;
        bool CooledDown() => LastFiredMs is not { } t || nowMs - t >= rule.CooldownMs;

        if (rule.Mode == RuleMode.OnChange)
        {
            if (reading is not { } c) { LastCondition = false; return false; }
            if (_reference is null) { _reference = c; LastCondition = false; return false; }

            if (Rgb.Matches(c, _reference.Value, rule.Tolerance, rule.Match))
            {
                _candidate = null;
                _conditionSince = null;
                LastCondition = false;
                return false;
            }
            if (_candidate is null || !Rgb.Matches(c, _candidate.Value, rule.Tolerance, rule.Match))
            {
                _candidate = c;          // a different new color restarts the hold
                _conditionSince = nowMs;
            }
            LastCondition = true;
            if (nowMs - _conditionSince!.Value < rule.HoldForMs || !CooledDown()) return false;
            _reference = _candidate;
            _candidate = null;
            _conditionSince = null;
            return Fire(nowMs);
        }

        var target = Rgb.TryParse(rule.Color, out var t) ? t : Rgb.Black;
        var matches = reading is { } col && Rgb.Matches(col, target, rule.Tolerance, rule.Match);
        // An unreadable pixel counts as "condition not met" in both modes, so a locked desktop never fires a rule.
        var condition = reading is not null && (rule.Mode == RuleMode.OnMatch ? matches : !matches);
        LastCondition = condition;
        if (!condition)
        {
            _armed = true;
            _conditionSince = null;
            return false;
        }
        _conditionSince ??= nowMs;
        if (!_armed || nowMs - _conditionSince.Value < rule.HoldForMs || !CooledDown()) return false;
        _armed = false;
        return Fire(nowMs);
    }

    private bool Fire(long nowMs)
    {
        LastFiredMs = nowMs;
        LastFiredAt = DateTime.Now;
        FireCount++;
        return true;
    }
}

/// <summary>Reads and writes rules.json.</summary>
public static class RulesFile
{
    private sealed class Document
    {
        public List<PixelRule>? Rules { get; set; }
    }

    public static List<PixelRule> Load(string path)
    {
        if (!File.Exists(path)) return [];
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static List<PixelRule> Parse(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(json, MacroFile.Json);
            var rules = doc?.Rules ?? [];
            foreach (var r in rules)
            {
                r.Actions ??= [];
                foreach (var a in r.Actions) MacroFile.Normalize(a);
                if (string.IsNullOrWhiteSpace(r.Window)) r.Window = null;
                if (string.IsNullOrWhiteSpace(r.MacroFile)) r.MacroFile = null;
            }
            return rules;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"rules.json is not valid: {ex.Message}", ex);
        }
    }

    public static string Serialize(IReadOnlyList<PixelRule> rules) =>
        JsonSerializer.Serialize(new Document { Rules = rules.ToList() }, MacroFile.Json) + "\n";

    public static void Save(string path, IReadOnlyList<PixelRule> rules)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(rules), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
