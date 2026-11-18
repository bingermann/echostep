namespace Echostep.Core;

/// <summary>
/// What a row's Value column shows while it is counting during playback: a timer counting down
/// (Wait, timeouts, Move duration, Repeat for a length of time) or a count going down (Repeat ×N,
/// Key press ×N). The Wait until time row is not counted down.
/// </summary>
public sealed record LiveState(long? EndMs = null, int? Count = null);

public static class LiveText
{
    /// <summary>
    /// The Value column's normal text. Wait for pixel shows only its coordinates (the column paints
    /// the color it is looking for beside them); every other action shows its parameters.
    /// </summary>
    public static string ValueText(MacroAction a) => a switch
    {
        WaitPixelAction p => $"{p.X}, {p.Y}" + (string.IsNullOrWhiteSpace(p.Window) ? "" : $" in \"{p.Window}\""),
        _ => a.Parameters,
    };

    /// <summary>
    /// Time left in the action's own unit: "350 ms" for millisecond times, otherwise "12 s", "4:05" or "1:02:03".
    /// </summary>
    public static string Countdown(long msLeft, WaitUnit unit)
    {
        msLeft = Math.Max(0, msLeft);
        if (unit == WaitUnit.Milliseconds) return $"{msLeft} ms";
        var seconds = (msLeft + 999) / 1000;
        if (seconds < 60) return $"{seconds} s";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>The Value text for an action in a live state, or null if the state does not apply to it.</summary>
    public static string? Describe(MacroAction a, LiveState s, long nowMs)
    {
        var left = s.EndMs is { } end ? end - nowMs : (long?)null;
        string Timeout(int timeoutMs, string? onTimeout) =>
            a.Parameters.Replace(MacroAction.TimeoutText(timeoutMs, onTimeout),
                $", timeout {Countdown(left!.Value, Durations.BestUnit(timeoutMs))} → {MacroAction.OnTimeoutOrStop(onTimeout)}");

        switch (a)
        {
            case WaitAction w when left is not null:
                return Countdown(left.Value, w.Unit);
            case MoveAction m when left is not null && m.TravelMs > 0:
                return a.Parameters.Replace($" in {Durations.Format(m.TravelMs)}", $" in {Countdown(left.Value, Durations.BestUnit(m.TravelMs))}");
            case KeyAction k when s.Count is { } n:
                return k.Modifiers.ToPrefix() + k.Key + $" ×{n}";
            case WaitPixelAction p when left is not null && p.TimeoutMs > 0:
                // The list shows this row as coordinates plus a swatch of the color; the countdown goes on the swatch.
                return Countdown(left.Value, Durations.BestUnit(p.TimeoutMs));
            case WaitPixelChangeAction p when left is not null && p.TimeoutMs > 0:
                return Timeout(p.TimeoutMs, p.OnTimeout);
            case WaitScreenChangeAction p when left is not null && p.TimeoutMs > 0:
                return Timeout(p.TimeoutMs, p.OnTimeout);
            case WindowFocusAction p when left is not null && p.TimeoutMs > 0:
                return Timeout(p.TimeoutMs, p.OnTimeout);
            case FindImageAction p when left is not null && p.TimeoutMs > 0 && !p.Once:
                return Timeout(p.TimeoutMs, p.OnTimeout);
            case RepeatAction r:
                switch (r.Mode)
                {
                    case RepeatMode.Count when s.Count is { } n:
                        return a.Parameters.Replace($", {r.Count} times", $", {n} {(n == 1 ? "time" : "times")}");
                    case RepeatMode.Seconds when left is not null:
                        var total = (r.Seconds ?? 60) * 1000L;
                        return a.Parameters.Replace($", for {Durations.Format(total)}", $", for {Countdown(left.Value, Durations.BestUnit(total))}");
                    case RepeatMode.Infinite when s.Count is { } run:
                        return a.Parameters.Replace(", forever", $", forever (run {run})");
                }
                return null;
            default:
                return null;
        }
    }
}
