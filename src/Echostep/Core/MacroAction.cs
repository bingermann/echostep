using System.Globalization;
using System.Text.Json.Serialization;

namespace Echostep.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
public enum MouseButton
{
    [JsonStringEnumMemberName("left")] Left,
    [JsonStringEnumMemberName("right")] Right,
    [JsonStringEnumMemberName("middle")] Middle,
    [JsonStringEnumMemberName("x1")] X1,
    [JsonStringEnumMemberName("x2")] X2,
}

/// <summary>What a Click action does with the button.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClickKind>))]
public enum ClickKind
{
    [JsonStringEnumMemberName("click")] Click,
    [JsonStringEnumMemberName("double")] Double,
    [JsonStringEnumMemberName("down")] Down,
    [JsonStringEnumMemberName("up")] Up,
}

/// <summary>What a Keypress action does with the key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyOption>))]
public enum KeyOption
{
    [JsonStringEnumMemberName("press")] Press,
    [JsonStringEnumMemberName("down")] Down,
    [JsonStringEnumMemberName("up")] Up,
}

/// <summary>Action types the playback filter can skip.</summary>
[Flags]
public enum PlaybackFilter
{
    None = 0,
    MouseMoves = 1,
    MouseClicks = 2,
    KeyPresses = 4,
    WaitTimes = 8,
    FocusChanges = 16,
    All = 31,
}

/// <summary>
/// One row of a macro. Each subclass is one action type; the JSON "type" field selects the subclass.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ClickAction), "click")]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(WheelAction), "wheel")]
[JsonDerivedType(typeof(KeyAction), "key")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(WaitUntilAction), "waitUntil")]
[JsonDerivedType(typeof(WaitPixelAction), "waitPixel")]
[JsonDerivedType(typeof(WaitPixelChangeAction), "waitPixelChange")]
[JsonDerivedType(typeof(WaitScreenChangeAction), "waitScreenChange")]
[JsonDerivedType(typeof(IfPixelAction), "ifPixel")]
[JsonDerivedType(typeof(IfAction), "if")]
[JsonDerivedType(typeof(RepeatAction), "repeat")]
[JsonDerivedType(typeof(GotoAction), "goto")]
[JsonDerivedType(typeof(WindowFocusAction), "windowFocus")]
[JsonDerivedType(typeof(FindImageAction), "findImage")]
[JsonDerivedType(typeof(TypeTextAction), "typeText")]
[JsonDerivedType(typeof(SetVariableAction), "setVariable")]
[JsonDerivedType(typeof(RunAction), "run")]
[JsonDerivedType(typeof(MessageAction), "message")]
public abstract class MacroAction
{
    /// <summary>Optional custom label (unique, case-insensitive, not start/end/stop/next or a number).</summary>
    [JsonPropertyOrder(-1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Free note shown in the Comment column.</summary>
    [JsonPropertyOrder(90)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }

    /// <summary>Disabled rows are skipped during playback.</summary>
    [JsonPropertyOrder(91)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }

    /// <summary>Playback pauses before a row with a breakpoint.</summary>
    [JsonPropertyOrder(92)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Breakpoint { get; set; }

    /// <summary>Row background color (#RRGGBB), visual only.</summary>
    [JsonPropertyOrder(93)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RowColor { get; set; }

    /// <summary>Group id. Neighbouring rows with the same id form one group; groups are visual only.</summary>
    [JsonPropertyOrder(94)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; set; }

    /// <summary>The group's color (#RRGGBB), stored on each member.</summary>
    [JsonPropertyOrder(95)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupColor { get; set; }

    /// <summary>Name shown in the Action column.</summary>
    [JsonIgnore] public abstract string DisplayName { get; }

    /// <summary>Text shown in the Value column.</summary>
    [JsonIgnore] public abstract string Parameters { get; }

    /// <summary>Which playback-filter checkbox covers this action (None = never filtered).</summary>
    [JsonIgnore] public virtual PlaybackFilter Category => PlaybackFilter.None;

    /// <summary>Time this action is known to take, for the macro duration in the status bar.</summary>
    [JsonIgnore] public virtual long DurationMs0 => 0;

    /// <summary>Label references this action makes (field name, target), for validation and highlighting.</summary>
    public virtual IEnumerable<(string Field, string Target)> GetTargets() => [];

    /// <summary>Rewrites every label reference (used when pasted labels are renamed).</summary>
    public virtual void MapTargets(Func<string, string> map) { }

    public MacroAction Clone() => (MacroAction)MemberwiseClone();

    public override string ToString() => $"{DisplayName} {Parameters}";

    internal static string WindowSuffix(string? window) =>
        string.IsNullOrWhiteSpace(window) ? "" : $" in \"{window}\"";

    internal static string TimeoutText(int timeoutMs, string? onTimeout) =>
        timeoutMs <= 0 ? ", no timeout" : $", timeout {Durations.Format(timeoutMs)} → {OnTimeoutOrStop(onTimeout)}";

    internal static string OnTimeoutOrStop(string? t) => string.IsNullOrWhiteSpace(t) ? Labels.Stop : t.Trim();

    internal static string ThenText(string? then) => string.IsNullOrWhiteSpace(then) ? "" : $", then → {then.Trim()}";

    internal static string? MapOpt(string? t, Func<string, string> map) => string.IsNullOrWhiteSpace(t) ? t : map(t);

    internal static IEnumerable<(string, string)> Opt(string field, string? target) =>
        string.IsNullOrWhiteSpace(target) ? [] : [(field, target.Trim())];
}

/// <summary>An action with an x, y point that may be relative to a window's client area.</summary>
public abstract class PointAction : MacroAction
{
    [JsonPropertyOrder(0)] public int X { get; set; }
    [JsonPropertyOrder(1)] public int Y { get; set; }

    /// <summary>Optional window title; when set, X/Y are relative to its client area.</summary>
    [JsonPropertyOrder(20)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Window { get; set; }
}

public sealed class ClickAction : PointAction
{
    [JsonPropertyOrder(2)] public MouseButton Button { get; set; } = MouseButton.Left;

    /// <summary>click (default), double, down or up.</summary>
    [JsonPropertyOrder(3), JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ClickKind Kind { get; set; }

    /// <summary>X/Y are an offset from wherever the mouse is when the action runs.</summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Relative { get; set; }

    /// <summary>Adds a small random offset (±2 px) each time.</summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Wiggle { get; set; }

    [JsonIgnore] public override string DisplayName => Kind switch
    {
        ClickKind.Double => "Double-click",
        ClickKind.Down => "Mouse button down",
        ClickKind.Up => "Mouse button up",
        _ => "Click",
    };

    [JsonIgnore] public override string Parameters =>
        $"{(Relative ? "+" : "")}{X}, {Y}{(Relative ? " from mouse" : "")}" +
        $"{(Button == MouseButton.Left ? "" : " " + Button.ToString().ToLowerInvariant())}" +
        $"{(Wiggle ? " ~wiggle" : "")}{WindowSuffix(Window)}";

    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.MouseClicks;
}

public sealed class MoveAction : PointAction
{
    /// <summary>Optional start point; without it the move starts wherever the mouse is.</summary>
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromX { get; set; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromY { get; set; }

    /// <summary>How long the pointer takes to travel; 0 jumps straight there.</summary>
    [JsonPropertyOrder(4), JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TravelMs { get; set; }

    /// <summary>X/Y are a distance from wherever the mouse is.</summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Relative { get; set; }

    [JsonIgnore] public override string DisplayName => "Move";
    [JsonIgnore] public override string Parameters =>
        (FromX is { } fx && FromY is { } fy && !Relative ? $"{fx}, {fy} → " : "") +
        $"{(Relative ? "+" : "")}{X}, {Y}{(Relative ? " from mouse" : "")}" +
        $"{(TravelMs > 0 ? $" in {Durations.Format(TravelMs)}" : "")}{WindowSuffix(Window)}";
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.MouseMoves;
    [JsonIgnore] public override long DurationMs0 => TravelMs;
}

public sealed class WheelAction : MacroAction
{
    /// <summary>Scroll sideways instead of up/down.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Horizontal { get; set; }

    /// <summary>Wheel amount; 120 is one notch. Positive scrolls up (or right).</summary>
    public int Value { get; set; } = 120;

    [JsonIgnore] public override string DisplayName => "Wheel";
    [JsonIgnore] public override string Parameters => $"{Value} {(Horizontal ? "horizontal" : "vertical")}";
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.MouseClicks;
}

public sealed class KeyAction : MacroAction
{
    public string Key { get; set; } = "Enter";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Ctrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Alt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Shift { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Win { get; set; }

    /// <summary>press (default: press and release), down or up.</summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public KeyOption Option { get; set; }

    /// <summary>How many times to press (written only when more than 1).</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CountOrNull { get; set; }

    [JsonIgnore]
    public int Count
    {
        get => Math.Max(1, CountOrNull ?? 1);
        set => CountOrNull = value > 1 ? value : null;
    }

    [JsonIgnore]
    public Modifiers Modifiers
    {
        get => (Ctrl ? Modifiers.Ctrl : 0) | (Alt ? Modifiers.Alt : 0) | (Shift ? Modifiers.Shift : 0) | (Win ? Modifiers.Win : 0);
        set { Ctrl = value.HasFlag(Modifiers.Ctrl); Alt = value.HasFlag(Modifiers.Alt); Shift = value.HasFlag(Modifiers.Shift); Win = value.HasFlag(Modifiers.Win); }
    }

    [JsonIgnore] public override string DisplayName => Option switch
    {
        KeyOption.Down => "Key down",
        KeyOption.Up => "Key up",
        _ => "Keypress",
    };
    [JsonIgnore] public override string Parameters => Modifiers.ToPrefix() + Key + (Count > 1 ? $" ×{Count}" : "");
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.KeyPresses;
}

/// <summary>The unit a wait is shown and edited in. The file always stores milliseconds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WaitUnit>))]
public enum WaitUnit
{
    [JsonStringEnumMemberName("ms")] Milliseconds,
    [JsonStringEnumMemberName("s")] Seconds,
    [JsonStringEnumMemberName("min")] Minutes,
    [JsonStringEnumMemberName("h")] Hours,
}

public sealed class WaitAction : MacroAction
{
    /// <summary>Length of the pause in milliseconds (the value that is played).</summary>
    public int Ms { get; set; } = 500;

    /// <summary>Unit the wait is shown in: ms (default), s, min or h.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WaitUnit Unit { get; set; } = WaitUnit.Milliseconds;

    [JsonIgnore] public override string DisplayName => "Wait";
    [JsonIgnore] public override string Parameters => $"{FormatAmount(Amount)} {UnitName(Unit)}";
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.WaitTimes;
    [JsonIgnore] public override long DurationMs0 => Ms;

    /// <summary>The wait expressed in <see cref="Unit"/> (for example 1.5 for 90000 ms in minutes).</summary>
    [JsonIgnore] public double Amount => Ms / (double)MsPer(Unit);

    public static int MsPer(WaitUnit unit) => unit switch
    {
        WaitUnit.Seconds => 1000,
        WaitUnit.Minutes => 60_000,
        WaitUnit.Hours => 3_600_000,
        _ => 1,
    };

    public static string UnitName(WaitUnit unit) => unit switch
    {
        WaitUnit.Seconds => "s",
        WaitUnit.Minutes => "min",
        WaitUnit.Hours => "h",
        _ => "ms",
    };

    public static string FormatAmount(double amount) => amount.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Milliseconds for an amount typed in a unit ("1.5" or "1,5"), or null if it is not a valid wait.</summary>
    public static int? ToMs(string? amount, WaitUnit unit)
    {
        var text = (amount ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        var ms = Math.Round(value * MsPer(unit));
        return ms is >= 0 and <= int.MaxValue ? (int)ms : null;
    }
}

/// <summary>
/// Waits until the next time the local clock shows a time of day. If that time has already passed
/// today, it waits until that time tomorrow.
/// </summary>
public sealed class WaitUntilAction : MacroAction
{
    /// <summary>Local time of day, 24-hour: "HH:mm" or "HH:mm:ss".</summary>
    public string Time { get; set; } = "09:00";

    [JsonIgnore] public override string DisplayName => "Wait until time";
    [JsonIgnore] public override string Parameters => TryParseTime(Time, out var t) ? Format(t) : Time;

    private static readonly string[] Formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string Format(TimeOnly t) => t.Second == 0 ? t.ToString("HH\\:mm") : t.ToString("HH\\:mm\\:ss");

    /// <summary>The next moment after <paramref name="now"/> when the clock shows <paramref name="time"/>.</summary>
    public static DateTime NextOccurrence(DateTime now, TimeOnly time)
    {
        var target = now.Date + time.ToTimeSpan();
        return target > now ? target : target.AddDays(1);
    }
}

/// <summary>Shared fields of the actions that read one pixel.</summary>
public abstract class PixelActionBase : PointAction
{
    [JsonPropertyOrder(4)] public int Tolerance { get; set; } = 12;

    [JsonPropertyOrder(21)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MatchMode Match { get; set; } = MatchMode.Channel;

    internal string TolText => Match == MatchMode.Distance ? $"≈{Tolerance}" : $"±{Tolerance}";
}

public sealed class WaitPixelAction : PixelActionBase
{
    [JsonPropertyOrder(3)] public string Color { get; set; } = "#000000";
    [JsonPropertyOrder(5)] public int TimeoutMs { get; set; } = 5000;
    /// <summary>Label to jump to on timeout, or "stop".</summary>
    [JsonPropertyOrder(6)] public string OnTimeout { get; set; } = Labels.Stop;

    /// <summary>Where to go when the color shows up; empty continues with the next row.</summary>
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    [JsonIgnore] public override string DisplayName => "Wait for pixel";
    [JsonIgnore] public override string Parameters =>
        $"{X}, {Y} = {Color} {TolText}{TimeoutText(TimeoutMs, OnTimeout)}{ThenText(Then)}{WindowSuffix(Window)}";
    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        (TimeoutMs > 0 ? [("On timeout", OnTimeoutOrStop(OnTimeout))] : Array.Empty<(string, string)>()).Concat(Opt("Then", Then));
    public override void MapTargets(Func<string, string> map) { OnTimeout = map(OnTimeout); Then = MapOpt(Then, map); }
}

/// <summary>Waits until the pixel differs from the color it had when the step started.</summary>
public sealed class WaitPixelChangeAction : PixelActionBase
{
    public WaitPixelChangeAction() => Tolerance = 0;

    [JsonPropertyOrder(5)] public int TimeoutMs { get; set; } = 5000;
    [JsonPropertyOrder(6)] public string OnTimeout { get; set; } = Labels.Stop;

    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    [JsonIgnore] public override string DisplayName => "Wait for pixel change";
    [JsonIgnore] public override string Parameters =>
        $"{X}, {Y} changes by more than {TolText}{TimeoutText(TimeoutMs, OnTimeout)}{ThenText(Then)}{WindowSuffix(Window)}";
    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        (TimeoutMs > 0 ? [("On timeout", OnTimeoutOrStop(OnTimeout))] : Array.Empty<(string, string)>()).Concat(Opt("Then", Then));
    public override void MapTargets(Func<string, string> map) { OnTimeout = map(OnTimeout); Then = MapOpt(Then, map); }
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeMouseAction>))]
public enum ChangeMouseAction
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("move")] Move,
    [JsonStringEnumMemberName("click")] LeftClick,
    [JsonStringEnumMemberName("rightClick")] RightClick,
    [JsonStringEnumMemberName("middleClick")] MiddleClick,
    [JsonStringEnumMemberName("doubleClick")] DoubleClick,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeAnchor>))]
public enum ChangeAnchor
{
    [JsonStringEnumMemberName("center")] Center,
    [JsonStringEnumMemberName("topLeft")] TopLeft,
    [JsonStringEnumMemberName("topRight")] TopRight,
    [JsonStringEnumMemberName("bottomLeft")] BottomLeft,
    [JsonStringEnumMemberName("bottomRight")] BottomRight,
}

/// <summary>
/// Waits until something changes inside a zone or on the entire screen. The area is captured when the
/// action starts and every later frame is compared with it.
/// </summary>
public sealed class WaitScreenChangeAction : MacroAction
{
    /// <summary>Zone in screen pixels. A width or height of 0 means the entire screen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Y { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Width { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Height { get; set; }

    /// <summary>A pixel counts as changed when any color channel differs by more than this.</summary>
    public int Tolerance { get; set; } = 8;

    /// <summary>Optional mouse action on the changed area.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChangeMouseAction Mouse { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChangeAnchor Anchor { get; set; }

    public int TimeoutMs { get; set; } = 30_000;
    public string OnTimeout { get; set; } = Labels.End;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    [JsonIgnore] public bool EntireScreen => Width <= 0 || Height <= 0;

    [JsonIgnore] public override string DisplayName => "Wait for screen changes";
    [JsonIgnore] public override string Parameters =>
        (EntireScreen ? "entire screen" : $"zone {X}, {Y}, {Width} × {Height}") +
        (Mouse == ChangeMouseAction.None ? "" : $", {Mouse} at {Anchor}") +
        $"{TimeoutText(TimeoutMs, OnTimeout)}{ThenText(Then)}";
    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        (TimeoutMs > 0 ? [("On timeout", OnTimeoutOrStop(OnTimeout))] : Array.Empty<(string, string)>()).Concat(Opt("Then", Then));
    public override void MapTargets(Func<string, string> map) { OnTimeout = map(OnTimeout); Then = MapOpt(Then, map); }
}

/// <summary>Checks the pixel once and jumps to Then or Else; an empty target continues with the next row.</summary>
public sealed class IfPixelAction : PixelActionBase
{
    [JsonPropertyOrder(3)] public string Color { get; set; } = "#000000";

    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    [JsonPropertyOrder(8)]
    [JsonPropertyName("else")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Else { get; set; }

    [JsonIgnore] public override string DisplayName => "If pixel";
    [JsonIgnore] public override string Parameters =>
        $"{X}, {Y} = {Color} {TolText} → {Show(Then)}, else → {Show(Else)}{WindowSuffix(Window)}";
    private static string Show(string? t) => string.IsNullOrWhiteSpace(t) ? "next" : t.Trim();

    public override IEnumerable<(string Field, string Target)> GetTargets() => Opt("Then", Then).Concat(Opt("Else", Else));
    public override void MapTargets(Func<string, string> map) { Then = MapOpt(Then, map); Else = MapOpt(Else, map); }
}

[JsonConverter(typeof(JsonStringEnumConverter<RepeatMode>))]
public enum RepeatMode
{
    /// <summary>The block runs Count times in total.</summary>
    [JsonStringEnumMemberName("count")] Count,
    /// <summary>Keep jumping back until Seconds have passed since the Repeat was first reached.</summary>
    [JsonStringEnumMemberName("seconds")] Seconds,
    /// <summary>Keep jumping back until a time of day.</summary>
    [JsonStringEnumMemberName("until")] Until,
    /// <summary>Jump back forever (until Stop).</summary>
    [JsonStringEnumMemberName("infinite")] Infinite,
}

public sealed class RepeatAction : MacroAction
{
    public string Target { get; set; } = Labels.Start;
    public int Count { get; set; } = 2;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RepeatMode Mode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seconds { get; set; }

    /// <summary>Time of day for <see cref="RepeatMode.Until"/> ("HH:mm" or "HH:mm:ss").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Until { get; set; }

    /// <summary>Where to go once the repeating is finished; empty continues with the next row.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? After { get; set; }

    [JsonIgnore] public override string DisplayName => "Repeat";
    [JsonIgnore] public override string Parameters => Mode switch
    {
        RepeatMode.Seconds => $"→ {Target}, for {Durations.Format((Seconds ?? 60) * 1000L)}",
        RepeatMode.Until => $"→ {Target}, until {Until}",
        RepeatMode.Infinite => $"→ {Target}, forever",
        _ => $"→ {Target}, {Count} times",
    } + ThenText(After);
    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        new[] { ("Target", Target?.Trim() ?? "") }.Concat(Opt("After finishing", After));
    public override void MapTargets(Func<string, string> map) { Target = map(Target); After = MapOpt(After, map); }
}

public sealed class GotoAction : MacroAction
{
    public string Target { get; set; } = Labels.Start;
    [JsonIgnore] public override string DisplayName => "Goto";
    [JsonIgnore] public override string Parameters => Target;
    public override IEnumerable<(string Field, string Target)> GetTargets() => [("Target", Target?.Trim() ?? "")];
    public override void MapTargets(Func<string, string> map) => Target = map(Target);
}

[JsonConverter(typeof(JsonStringEnumConverter<TitleMatch>))]
public enum TitleMatch
{
    [JsonStringEnumMemberName("exact")] Exact,
    [JsonStringEnumMemberName("begins")] Begins,
    [JsonStringEnumMemberName("ends")] Ends,
    [JsonStringEnumMemberName("contains")] Contains,
    [JsonStringEnumMemberName("any")] Any,
}

/// <summary>
/// Waits for a running program's window, brings it to the front, and can move or resize it.
/// It never starts the program.
/// </summary>
public sealed class WindowFocusAction : MacroAction
{
    /// <summary>Program file name, e.g. notepad.exe (empty = any program).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TitleMatch TitleMatch { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>When both are set, the window is moved here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Left { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Top { get; set; }

    /// <summary>When both are set, the window is resized to this.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Width { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Height { get; set; }

    public int TimeoutMs { get; set; } = 30_000;
    public string OnTimeout { get; set; } = Labels.End;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    public bool TitleMatches(string actual)
    {
        var t = Title ?? "";
        return TitleMatch switch
        {
            TitleMatch.Any => true,
            TitleMatch.Begins => actual.StartsWith(t, StringComparison.OrdinalIgnoreCase),
            TitleMatch.Ends => actual.EndsWith(t, StringComparison.OrdinalIgnoreCase),
            TitleMatch.Contains => actual.Contains(t, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(actual, t, StringComparison.OrdinalIgnoreCase),
        };
    }

    public bool ProgramMatches(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(Program)) return true;
        static string Stem(string s) => s.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Trim()[..^4] : s.Trim();
        return exeName is not null && string.Equals(Stem(Program), Stem(exeName), StringComparison.OrdinalIgnoreCase);
    }

    [JsonIgnore] public string Describe =>
        (string.IsNullOrWhiteSpace(Program) ? "" : Program!.Trim() + " ") +
        (TitleMatch == TitleMatch.Any ? "(any title)" : $"\"{Title}\"" + (TitleMatch == TitleMatch.Exact ? "" : $" ({TitleMatch.ToString().ToLowerInvariant()})"));

    [JsonIgnore] public override string DisplayName => "Window focus";
    [JsonIgnore] public override string Parameters =>
        Describe + (Left is not null && Top is not null ? $", move to {Left}, {Top}" : "") +
        (Width is not null && Height is not null ? $", size {Width} × {Height}" : "") +
        $"{TimeoutText(TimeoutMs, OnTimeout)}{ThenText(Then)}";
    [JsonIgnore] public override PlaybackFilter Category => PlaybackFilter.FocusChanges;
    public override IEnumerable<(string Field, string Target)> GetTargets() =>
        (TimeoutMs > 0 ? [("On timeout", OnTimeoutOrStop(OnTimeout))] : Array.Empty<(string, string)>()).Concat(Opt("Then", Then));
    public override void MapTargets(Func<string, string> map) { OnTimeout = map(OnTimeout); Then = MapOpt(Then, map); }
}
