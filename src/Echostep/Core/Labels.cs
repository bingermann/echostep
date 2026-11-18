using System.Globalization;

namespace Echostep.Core;

/// <summary>
/// Label rules and lookup. Built-in targets: <c>start</c> (first row), <c>end</c> (past the last row),
/// <c>stop</c> (abort playback), and row numbers 1…n as shown in the # column.
/// Custom labels are unique and case-insensitive.
/// </summary>
public sealed class Labels
{
    public const string Start = "start";
    public const string End = "end";
    public const string Stop = "stop";
    /// <summary>The row after the one that jumps (the default for "go to" fields).</summary>
    public const string Next = "next";

    /// <summary>Resolve() result meaning "stop playing".</summary>
    public const int StopIndex = -1;

    private readonly Dictionary<string, int> _custom = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _count;

    private Labels(IReadOnlyList<MacroAction> actions)
    {
        _count = actions.Count;
        for (var i = 0; i < actions.Count; i++)
        {
            var l = actions[i].Label?.Trim();
            if (!string.IsNullOrEmpty(l) && !IsReserved(l)) _custom.TryAdd(l, i);
        }
    }

    /// <summary>Snapshot of the labels in a list. Row numbers are taken from the list as it is now.</summary>
    public static Labels Build(IReadOnlyList<MacroAction> actions) => new(actions);

    /// <summary>Row index for a target, <see cref="StopIndex"/> for stop, Count for end, or null if missing.</summary>
    /// <param name="fromRow">The row making the jump, used by <c>next</c>.</param>
    public int? Resolve(string? target, int fromRow = -1)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var t = target.Trim();
        if (t.Equals(Next, StringComparison.OrdinalIgnoreCase)) return Math.Min(fromRow + 1, _count);
        if (t.Equals(Start, StringComparison.OrdinalIgnoreCase)) return 0;
        if (t.Equals(End, StringComparison.OrdinalIgnoreCase)) return _count;
        if (t.Equals(Stop, StringComparison.OrdinalIgnoreCase)) return StopIndex;
        if (IsRowNumber(t, out var n)) return n >= 1 && n <= _count ? n - 1 : null;
        return _custom.TryGetValue(t, out var i) ? i : null;
    }

    public static bool IsRowNumber(string text, out int n) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);

    public static bool IsReserved(string label)
    {
        var t = label.Trim();
        return t.Equals(Start, StringComparison.OrdinalIgnoreCase)
            || t.Equals(End, StringComparison.OrdinalIgnoreCase)
            || t.Equals(Stop, StringComparison.OrdinalIgnoreCase)
            || t.Equals(Next, StringComparison.OrdinalIgnoreCase)
            || IsRowNumber(t, out _);
    }

    /// <summary>Checks a custom label for the action at <paramref name="selfIndex"/> (-1 for a new action).</summary>
    public static string? CheckCustomLabel(string? label, IReadOnlyList<MacroAction> actions, int selfIndex)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var t = label.Trim();
        if (IsReserved(t)) return $"\"{t}\" is reserved: start, end, stop, next and plain numbers cannot be custom labels.";
        for (var i = 0; i < actions.Count; i++)
        {
            if (i == selfIndex) continue;
            if (string.Equals(actions[i].Label?.Trim(), t, StringComparison.OrdinalIgnoreCase))
                return $"Label \"{t}\" is already used on row {i + 1}.";
        }
        return null;
    }
}

/// <summary>Finds problems that stop a macro from playing: bad labels, missing jump targets, bad parameters.</summary>
public static class MacroValidator
{
    /// <summary>Problems per row index (0-based). Empty when the macro can play.</summary>
    public static Dictionary<int, List<string>> Validate(IReadOnlyList<MacroAction> actions)
    {
        var problems = new Dictionary<int, List<string>>();
        void Add(int row, string msg)
        {
            if (!problems.TryGetValue(row, out var l)) problems[row] = l = [];
            l.Add(msg);
        }

        var labels = Labels.Build(actions);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var label = a.Label?.Trim();
            if (!string.IsNullOrEmpty(label))
            {
                if (Labels.IsReserved(label)) Add(i, $"Label \"{label}\" is reserved.");
                else if (seen.TryGetValue(label, out var first)) Add(i, $"Label \"{label}\" is already used on row {first + 1}.");
                else seen[label] = i;
            }

            foreach (var (field, target) in a.GetTargets())
            {
                if (string.IsNullOrWhiteSpace(target)) Add(i, $"{a.DisplayName}: {field} is empty.");
                else if (labels.Resolve(target, i) is null) Add(i, $"{a.DisplayName}: {field} \"{target}\" does not exist.");
            }

            switch (a)
            {
                case KeyAction k when !KeyNames.TryGetVk(k.Key, out _):
                    Add(i, $"Keypress: unknown key \"{k.Key}\".");
                    break;
                case WaitAction w when w.Ms < 0:
                    Add(i, "Wait: milliseconds cannot be negative.");
                    break;
                case WaitUntilAction wu when !WaitUntilAction.TryParseTime(wu.Time, out _):
                    Add(i, $"Wait until time: \"{wu.Time}\" is not a time like 14:30 or 14:30:00.");
                    break;
                case WaitPixelAction wp when !Rgb.TryParse(wp.Color, out _):
                    Add(i, $"Wait for pixel: \"{wp.Color}\" is not a color like #2D7DD2.");
                    break;
                case IfAction cond:
                    foreach (var problem in cond.Problems()) Add(i, problem);
                    break;
                case IfPixelAction ip when !Rgb.TryParse(ip.Color, out _):
                    Add(i, $"If pixel: \"{ip.Color}\" is not a color like #2D7DD2.");
                    break;
                case RepeatAction { Mode: RepeatMode.Count } r when r.Count < 1:
                    Add(i, "Repeat: count must be at least 1.");
                    break;
                case RepeatAction { Mode: RepeatMode.Until } r when !WaitUntilAction.TryParseTime(r.Until, out _):
                    Add(i, $"Repeat: \"{r.Until}\" is not a time like 14:30.");
                    break;
                case WindowFocusAction wf when wf.TitleMatch != TitleMatch.Any && string.IsNullOrWhiteSpace(wf.Title) && string.IsNullOrWhiteSpace(wf.Program):
                    Add(i, "Window focus: enter a program or a window title.");
                    break;
                case MoveAction mv when mv.TravelMs < 0:
                    Add(i, "Move: duration cannot be negative.");
                    break;
                case FindImageAction fi when !ImagePattern.TryDecode(fi.Image, out _):
                    Add(i, "Find image: capture the image to look for.");
                    break;
                case FindImageAction fi when fi.Tolerance is < 0 or > 255:
                    Add(i, "Find image: tolerance must be 0–255.");
                    break;
                case SetVariableAction sv when !Variables.IsValidName(sv.Name) && sv.Operation != VariableOperation.ToClipboard:
                    Add(i, $"Set variable: \"{sv.Name}\" is not a variable name (letters, digits and _, not starting with a digit).");
                    break;
                case RunAction run when string.IsNullOrWhiteSpace(run.Target):
                    Add(i, "Run / open: enter a program, file, folder or web address.");
                    break;
                case TypeTextAction tt when tt.CharDelayMs < 0:
                    Add(i, "Type text: the delay cannot be negative.");
                    break;
            }

            if (a is PixelActionBase p && (p.Tolerance < 0 || p.Tolerance > Rgb.MaxTolerance(p.Match)))
                Add(i, $"{a.DisplayName}: tolerance must be 0–{Rgb.MaxTolerance(p.Match)}.");
        }
        return problems;
    }

    /// <summary>One-line description of the first problem, naming the row, or null if the macro is valid.</summary>
    public static string? FirstProblem(IReadOnlyList<MacroAction> actions)
    {
        var p = Validate(actions);
        if (p.Count == 0) return null;
        var row = p.Keys.Min();
        return $"Row {row + 1}: {p[row][0]}";
    }
}
