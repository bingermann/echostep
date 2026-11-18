using System.Text.Json.Serialization;

namespace Echostep.Core;

[JsonConverter(typeof(JsonStringEnumConverter<IfCondition>))]
public enum IfCondition
{
    /// <summary>The time of day on this PC's clock.</summary>
    [JsonStringEnumMemberName("time")] Time,
    /// <summary>The day of the week.</summary>
    [JsonStringEnumMemberName("day")] Day,
    /// <summary>Whether a window is open or in front.</summary>
    [JsonStringEnumMemberName("window")] Window,
    /// <summary>A variable compared with a value.</summary>
    [JsonStringEnumMemberName("variable")] Variable,
}

[JsonConverter(typeof(JsonStringEnumConverter<TimeCompare>))]
public enum TimeCompare
{
    [JsonStringEnumMemberName("before")] Before,
    [JsonStringEnumMemberName("after")] After,
    /// <summary>From Time up to (not including) Time2; wraps past midnight when Time2 is earlier.</summary>
    [JsonStringEnumMemberName("between")] Between,
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowCondition>))]
public enum WindowCondition
{
    /// <summary>A window with a matching title exists (minimized counts).</summary>
    [JsonStringEnumMemberName("open")] Open,
    /// <summary>The window in front has a matching title.</summary>
    [JsonStringEnumMemberName("front")] Front,
}

/// <summary>
/// Checks a condition once (the time of day, the day of the week, or a window) and goes to Then if it is
/// true, otherwise to Else. An empty target continues with the next row.
/// </summary>
public sealed class IfAction : MacroAction
{
    public static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public IfCondition Condition { get; set; } = IfCondition.Time;

    // ---- Time of day --------------------------------------------------------------------------
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TimeCompare? Compare { get; set; }
    /// <summary>"HH:mm" or "HH:mm:ss", 24-hour.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Time { get; set; }
    /// <summary>End of the range for "between".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Time2 { get; set; }

    // ---- Day of the week ----------------------------------------------------------------------
    /// <summary>Days that count as true, e.g. ["mon","tue","wed","thu","fri"].</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Days { get; set; }

    // ---- Window -------------------------------------------------------------------------------
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Title { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TitleMatch? TitleMatch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WindowCondition? State { get; set; }

    // ---- Variable -----------------------------------------------------------------------------
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Variable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public VarCompare? VarCompare { get; set; }
    /// <summary>The value to compare with; {name} is replaced by a variable's value.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Value { get; set; }

    // ---- Branches -----------------------------------------------------------------------------
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Then { get; set; }
    [JsonPropertyName("else")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Else { get; set; }

    [JsonIgnore] public override string DisplayName => "If";

    [JsonIgnore] public override string Parameters => $"{ConditionText} → {Show(Then)}, else → {Show(Else)}";

    private static string Show(string? t) => string.IsNullOrWhiteSpace(t) ? "next" : t.Trim();

    /// <summary>"time is after 17:00", "day is Sat, Sun", "window \"Chrome\" (contains) is in front"…</summary>
    [JsonIgnore]
    public string ConditionText => Condition switch
    {
        IfCondition.Day => "day is " + (Days is { Count: > 0 } d ? DaysText(d) : "(no days)"),
        IfCondition.Variable => $"{Variable} {CompareText(VarCompare ?? Core.VarCompare.Equal)} \"{Value}\"",
        IfCondition.Window => WindowText + ((State ?? WindowCondition.Open) == WindowCondition.Front ? " is in front" : " is open"),
        _ => (Compare ?? TimeCompare.After) switch
        {
            TimeCompare.Before => $"time is before {Time}",
            TimeCompare.Between => $"time is between {Time} and {Time2}",
            _ => $"time is after {Time}",
        },
    };

    private string WindowText
    {
        get
        {
            var m = TitleMatch ?? Core.TitleMatch.Contains;
            if (m == Core.TitleMatch.Any) return "any window";
            return $"window \"{Title}\"" + (m == Core.TitleMatch.Exact ? "" : $" ({m.ToString().ToLowerInvariant()})");
        }
    }

    public static string DaysText(IEnumerable<string> days)
    {
        var set = days.Select(d => d.Trim().ToLowerInvariant()).ToHashSet();
        if (DayKeys.Take(5).All(set.Contains) && set.Count == 5) return "weekdays";
        if (set.SetEquals(["sat", "sun"])) return "weekend";
        if (set.Count == 7) return "any day";
        return string.Join(", ", DayKeys.Where(set.Contains).Select(d => char.ToUpperInvariant(d[0]) + d[1..]));
    }

    public static string CompareText(VarCompare c) => c switch
    {
        Core.VarCompare.NotEqual => "≠",
        Core.VarCompare.Less => "<",
        Core.VarCompare.Greater => ">",
        Core.VarCompare.Contains => "contains",
        _ => "=",
    };

    public static string DayKey(DayOfWeek d) => DayKeys[((int)d + 6) % 7];

    public static bool TitleMatches(TitleMatch mode, string pattern, string actual) => mode switch
    {
        Core.TitleMatch.Any => actual.Length > 0,
        Core.TitleMatch.Begins => actual.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
        Core.TitleMatch.Ends => actual.EndsWith(pattern, StringComparison.OrdinalIgnoreCase),
        Core.TitleMatch.Contains => actual.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(actual, pattern, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Whether the condition is true now.</summary>
    /// <param name="lookup">Variable values (and built-ins); null when no variables are available.</param>
    public bool Evaluate(DateTime now, IScreenReader screen, Func<string, string?>? lookup = null)
    {
        switch (Condition)
        {
            case IfCondition.Variable:
            {
                lookup ??= _ => null;
                var name = Variable?.Trim() ?? "";
                return Variables.Compare(lookup(name) ?? "", VarCompare ?? Core.VarCompare.Equal, Variables.Expand(Value, lookup));
            }

            case IfCondition.Day:
                return Days is not null && Days.Any(d => string.Equals(d.Trim(), DayKey(now.DayOfWeek), StringComparison.OrdinalIgnoreCase));

            case IfCondition.Window:
            {
                var mode = TitleMatch ?? Core.TitleMatch.Contains;
                var pattern = Title?.Trim() ?? "";
                if ((State ?? WindowCondition.Open) == WindowCondition.Front)
                    return screen.ForegroundTitle() is { } t && TitleMatches(mode, pattern, t);
                return screen.WindowTitles().Any(t => TitleMatches(mode, pattern, t));
            }

            default:
            {
                if (!WaitUntilAction.TryParseTime(Time, out var a)) throw new PlaybackException($"\"{Time}\" is not a time of day");
                var t = TimeOnly.FromDateTime(now);
                switch (Compare ?? TimeCompare.After)
                {
                    case TimeCompare.Before: return t < a;
                    case TimeCompare.Between:
                        if (!WaitUntilAction.TryParseTime(Time2, out var b)) throw new PlaybackException($"\"{Time2}\" is not a time of day");
                        return a <= b ? t >= a && t < b : t >= a || t < b; // a range like 22:00–06:00 wraps past midnight
                    default: return t >= a;
                }
            }
        }
    }

    /// <summary>Problems that stop the macro from playing, or empty.</summary>
    public IEnumerable<string> Problems()
    {
        switch (Condition)
        {
            case IfCondition.Time:
                if (!WaitUntilAction.TryParseTime(Time, out _)) yield return $"If: \"{Time}\" is not a time like 14:30 or 14:30:00.";
                if (Compare == TimeCompare.Between && !WaitUntilAction.TryParseTime(Time2, out _))
                    yield return $"If: \"{Time2}\" is not a time like 14:30 or 14:30:00.";
                break;
            case IfCondition.Day:
                if (Days is not { Count: > 0 }) yield return "If: tick at least one day.";
                else if (Days.FirstOrDefault(d => !DayKeys.Contains(d.Trim().ToLowerInvariant())) is { } bad)
                    yield return $"If: \"{bad}\" is not a day (use mon, tue, wed, thu, fri, sat, sun).";
                break;
            case IfCondition.Variable:
                if (!Variables.IsValidName(Variable)) yield return $"If: \"{Variable}\" is not a variable name (letters, digits and _, not starting with a digit).";
                break;
            case IfCondition.Window:
                if ((TitleMatch ?? Core.TitleMatch.Contains) != Core.TitleMatch.Any && string.IsNullOrWhiteSpace(Title))
                    yield return "If: enter the window title to look for.";
                break;
        }
    }

    public override IEnumerable<(string Field, string Target)> GetTargets() => Opt("Then", Then).Concat(Opt("Else", Else));
    public override void MapTargets(Func<string, string> map) { Then = MapOpt(Then, map); Else = MapOpt(Else, map); }
}
