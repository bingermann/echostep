using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Echostep.Core;

/// <summary>Which parameter Search &amp; replace looks at.</summary>
public enum FindField
{
    All, WindowTitle, Application, Label, TargetLabel, Comment, DelayTimeout, X, Y, EndX, EndY, Value, KeyCount, MoveDuration,
}

/// <summary>Editing operations that work on any action type: edit-all, search &amp; replace, combine, duration.</summary>
public static class MacroEditing
{
    public static readonly (FindField Field, string Name)[] FieldNames =
    [
        (FindField.All, "All"), (FindField.WindowTitle, "Window title"), (FindField.Application, "Application"),
        (FindField.Label, "Label"), (FindField.TargetLabel, "Target label"), (FindField.Comment, "Comment"),
        (FindField.DelayTimeout, "Delay / Timeout"), (FindField.X, "X"), (FindField.Y, "Y"), (FindField.EndX, "End X"),
        (FindField.EndY, "End Y"), (FindField.Value, "Value"), (FindField.KeyCount, "Key press count"),
        (FindField.MoveDuration, "Mouse movement duration"),
    ];

    private static readonly JsonSerializerOptions Compact = new(MacroFile.Json) { WriteIndented = false };

    private static JsonObject ToNode(MacroAction a) => (JsonObject)JsonSerializer.SerializeToNode(a, Compact)!;

    private static MacroAction FromNode(JsonObject node)
    {
        var a = node.Deserialize<MacroAction>(Compact)!;
        MacroFile.Normalize(a);
        return a;
    }

    // ---- Edit all selected ------------------------------------------------------------------------

    /// <summary>
    /// Copies onto <paramref name="target"/> every field that differs between <paramref name="before"/> and
    /// <paramref name="after"/> (the same action before and after its dialog). The label is never copied.
    /// </summary>
    public static MacroAction ApplyChanges(MacroAction before, MacroAction after, MacroAction target)
    {
        if (target.GetType() != after.GetType()) return target;
        JsonObject b = ToNode(before), a = ToNode(after), t = ToNode(target);
        var names = b.Select(p => p.Key).Union(a.Select(p => p.Key)).Where(n => n is not ("type" or "label")).ToList();
        foreach (var name in names)
        {
            var oldText = b[name]?.ToJsonString();
            var newText = a[name]?.ToJsonString();
            if (oldText == newText) continue;
            if (a[name] is null) t.Remove(name);
            else t[name] = a[name]!.DeepClone();
        }
        return FromNode(t);
    }

    // ---- Search & replace -------------------------------------------------------------------------

    private static IEnumerable<string> Properties(FindField field, bool isMove) => field switch
    {
        FindField.WindowTitle => ["window", "title"],
        FindField.Application => ["program"],
        FindField.Label => ["label"],
        FindField.TargetLabel => ["target", "onTimeout", "then", "else", "after"],
        FindField.Comment => ["comment"],
        FindField.DelayTimeout => ["ms", "timeoutMs", "seconds"],
        FindField.X => isMove ? ["fromX"] : ["x"],
        FindField.Y => isMove ? ["fromY"] : ["y"],
        FindField.EndX => isMove ? ["x"] : [],
        FindField.EndY => isMove ? ["y"] : [],
        FindField.Value => ["value", "color", "key", "time", "time2", "until"],
        FindField.KeyCount => ["count"],
        FindField.MoveDuration => ["durationMs"],
        _ => ["window", "title", "program", "label", "target", "onTimeout", "then", "else", "after", "comment", "ms",
            "timeoutMs", "seconds", "x", "y", "fromX", "fromY", "value", "color", "key", "time", "until", "count", "durationMs"],
    };

    private static string? AsText(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue<string>(out var s) ? s
        : v.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture)
        : null;

    /// <summary>True if any searched parameter of the action contains the text (case-insensitive).</summary>
    public static bool Matches(MacroAction action, FindField field, string find)
    {
        if (string.IsNullOrEmpty(find)) return false;
        var node = ToNode(action);
        return Properties(field, action is MoveAction)
            .Any(p => AsText(node[p])?.Contains(find, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Replaces the text in the searched parameters. Returns the changed copy and how many parameters changed,
    /// or null if nothing matched (or the result would not be valid, e.g. letters in a number).
    /// </summary>
    public static MacroAction? Replace(MacroAction action, FindField field, string find, string replace, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(find)) return null;
        var node = ToNode(action);
        foreach (var p in Properties(field, action is MoveAction))
        {
            var text = AsText(node[p]);
            if (text is null || !text.Contains(find, StringComparison.OrdinalIgnoreCase)) continue;
            var changed = text.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            if (node[p]!.GetValueKind() == JsonValueKind.Number)
            {
                if (!int.TryParse(changed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                node[p] = number;
            }
            else node[p] = changed;
            count++;
        }
        if (count == 0) return null;
        try
        {
            return FromNode(node);
        }
        catch (JsonException)
        {
            count = 0;
            return null;
        }
    }

    // ---- Combine Down + Up ------------------------------------------------------------------------

    /// <summary>
    /// Merges button Down+Up pairs into Clicks and key Down+Up pairs into Keypresses, among the given rows.
    /// A single Wait between the two halves is dropped. Returns how many pairs were merged.
    /// </summary>
    public static int CombineDownUp(List<MacroAction> actions, IEnumerable<int> rows)
    {
        var selected = rows.ToHashSet();
        var merged = 0;
        for (var i = 0; i < actions.Count; i++)
        {
            if (!selected.Contains(i)) continue;
            var upAt = i + 1 < actions.Count && IsUpFor(actions[i], actions[i + 1]) ? i + 1
                : i + 2 < actions.Count && actions[i + 1] is WaitAction && IsUpFor(actions[i], actions[i + 2]) ? i + 2
                : -1;
            if (upAt < 0 || !selected.Contains(upAt)) continue;
            var combined = actions[i].Clone();
            if (combined is ClickAction c) c.Kind = ClickKind.Click;
            else if (combined is KeyAction k) k.Option = KeyOption.Press;
            actions[i] = combined;
            var removed = upAt - i;
            actions.RemoveRange(i + 1, removed);
            selected = selected.Select(r => r > i ? r - removed : r).ToHashSet();
            merged++;
        }
        return merged;
    }

    private static bool IsUpFor(MacroAction down, MacroAction up) => (down, up) switch
    {
        (ClickAction { Kind: ClickKind.Down } d, ClickAction { Kind: ClickKind.Up } u) => d.Button == u.Button,
        (KeyAction { Option: KeyOption.Down } d, KeyAction { Option: KeyOption.Up } u) =>
            string.Equals(d.Key, u.Key, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    // ---- Groups -----------------------------------------------------------------------------------

    /// <summary>First and last row of the group that contains <paramref name="row"/>, or null if it is not in a group.</summary>
    public static (int First, int Last)? GroupRange(IReadOnlyList<MacroAction> actions, int row)
    {
        if (row < 0 || row >= actions.Count || actions[row].Group is not { } id) return null;
        int first = row, last = row;
        while (first > 0 && actions[first - 1].Group == id) first--;
        while (last < actions.Count - 1 && actions[last + 1].Group == id) last++;
        return (first, last);
    }

    /// <summary>
    /// The group that rows inserted at <paramref name="at"/> join: only when they land inside a group, between two of its
    /// rows. Rows added just before its first row or just after its last row stay outside it (use Add to group for that).
    /// </summary>
    public static (string Id, string? Color)? GroupForInsert(IReadOnlyList<MacroAction> actions, int at) =>
        at > 0 && at < actions.Count && actions[at - 1].Group is { } id && actions[at].Group == id
            ? (id, actions[at - 1].GroupColor)
            : null;

    /// <summary>The group right above row <paramref name="first"/> (above: true) or right below row <paramref name="last"/>.</summary>
    public static (string Id, string? Color)? AdjacentGroup(IReadOnlyList<MacroAction> actions, int first, int last, bool above)
    {
        var i = above ? first - 1 : last + 1;
        return i >= 0 && i < actions.Count && actions[i].Group is { } id ? (id, actions[i].GroupColor) : null;
    }

    /// <summary>
    /// The one existing group among rows <paramref name="first"/>..<paramref name="last"/>, if exactly one appears there:
    /// grouping such a selection grows that group instead of starting a new one.
    /// </summary>
    public static (string Id, string? Color)? SingleGroupIn(IReadOnlyList<MacroAction> actions, int first, int last)
    {
        (string Id, string? Color)? found = null;
        for (var i = Math.Max(0, first); i <= last && i < actions.Count; i++)
        {
            if (actions[i].Group is not { } id) continue;
            if (found is null) found = (id, actions[i].GroupColor);
            else if (found.Value.Id != id) return null;
        }
        return found;
    }

    /// <summary>Puts rows into a group (replacing any group they had).</summary>
    public static void JoinGroup(IEnumerable<MacroAction> rows, (string Id, string? Color) group)
    {
        foreach (var a in rows)
        {
            a.Group = group.Id;
            a.GroupColor = group.Color;
        }
    }

    /// <summary>
    /// Takes the given rows out of their groups and leaves the rest grouped. When that splits a group in two,
    /// the lower part gets its own id (same color), so the two parts collapse and recolor separately.
    /// </summary>
    public static void RemoveFromGroup(IReadOnlyList<MacroAction> actions, IEnumerable<int> rows)
    {
        foreach (var i in rows)
            if (i >= 0 && i < actions.Count)
            {
                actions[i].Group = null;
                actions[i].GroupColor = null;
            }
        var seen = new HashSet<string>();
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i].Group is not { } id || (i > 0 && actions[i - 1].Group == id)) continue; // not the start of a run
            if (seen.Add(id)) continue; // first run of this id keeps it
            var fresh = NewGroupId(actions);
            for (var j = i; j < actions.Count && actions[j].Group == id; j++) actions[j].Group = fresh;
            seen.Add(fresh);
        }
    }

    public static string NewGroupId(IReadOnlyList<MacroAction> actions)
    {
        var used = actions.Select(a => a.Group).Where(g => g is not null).ToHashSet();
        for (var n = 1; ; n++)
            if (!used.Contains("g" + n)) return "g" + n;
    }

    /// <summary>Total of the fixed waits and move times, shown as the macro duration.</summary>
    public static TimeSpan Duration(IEnumerable<MacroAction> actions) =>
        TimeSpan.FromMilliseconds(actions.Where(a => !a.Disabled).Sum(a => a.DurationMs0));
}
