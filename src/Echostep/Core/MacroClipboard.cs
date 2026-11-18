namespace Echostep.Core;

/// <summary>
/// Copy and paste of actions. The clipboard holds ordinary .estep text, so actions can be pasted
/// between Echostep windows, into a text editor, or from a .estep file opened in one.
/// </summary>
public static class MacroClipboard
{
    public static string ToText(IEnumerable<MacroAction> actions) => MacroFile.Serialize(MacroFile.CloneAll(actions));

    /// <summary>Actions in clipboard text, or null if the text is not Echostep actions.</summary>
    public static List<MacroAction>? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{')) return null;
        try
        {
            var actions = MacroFile.Parse(text);
            return actions.Count > 0 ? actions : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes pasted actions safe to insert into <paramref name="existing"/>: a label that is already in use
    /// gets a new unique name (retry → retry2), and jumps inside the pasted block that pointed at the old
    /// name follow the new one, so a copied loop stays self-contained.
    /// </summary>
    public static void PrepareForPaste(IReadOnlyList<MacroAction> existing, List<MacroAction> pasted)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in existing)
            if (!string.IsNullOrWhiteSpace(a.Label)) used.Add(a.Label.Trim());

        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in pasted)
        {
            var label = a.Label?.Trim();
            if (string.IsNullOrEmpty(label)) continue;
            if (Labels.IsReserved(label)) { a.Label = null; continue; }
            if (used.Contains(label))
            {
                var stem = label.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (stem.Length == 0) stem = label;
                var n = 2;
                string candidate;
                do candidate = stem + n++; while (used.Contains(candidate));
                renamed.TryAdd(label, candidate);
                a.Label = candidate;
                label = candidate;
            }
            used.Add(label);
        }
        if (renamed.Count == 0) return;

        string Map(string t) => renamed.TryGetValue(t.Trim(), out var n) ? n : t;
        foreach (var a in pasted) a.MapTargets(Map);
    }
}
