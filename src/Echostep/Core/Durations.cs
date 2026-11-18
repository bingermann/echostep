using System.Globalization;

namespace Echostep.Core;

/// <summary>
/// Time amounts shown in a unit (milliseconds, seconds, minutes or hours). Files always store milliseconds;
/// a time without a stored unit is shown in the largest unit that gives a whole number (5000 ms → 5 s).
/// </summary>
public static class Durations
{
    public static readonly string[] UnitNames = ["milliseconds", "seconds", "minutes", "hours"];

    /// <summary>The largest unit in which <paramref name="ms"/> is a whole number.</summary>
    public static WaitUnit BestUnit(long ms)
    {
        if (ms <= 0) return WaitUnit.Milliseconds;
        foreach (var u in new[] { WaitUnit.Hours, WaitUnit.Minutes, WaitUnit.Seconds })
            if (ms % WaitAction.MsPer(u) == 0) return u;
        return WaitUnit.Milliseconds;
    }

    /// <summary>"250 ms", "5 s", "2 min", "1 h", or in a given unit ("1.5 min").</summary>
    public static string Format(long ms, WaitUnit? unit = null)
    {
        var u = unit ?? BestUnit(ms);
        return $"{WaitAction.FormatAmount(ms / (double)WaitAction.MsPer(u))} {WaitAction.UnitName(u)}";
    }

    /// <summary>Milliseconds for an amount typed in a unit ("1.5" or "1,5"), or null if it is not a valid time.</summary>
    public static long? ToMs(string? amount, WaitUnit unit)
    {
        var text = (amount ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        var ms = Math.Round(value * WaitAction.MsPer(unit));
        return ms is >= 0 and <= long.MaxValue / 2 ? (long)ms : null;
    }
}
