namespace Echostep.Core;

public enum RecEventKind
{
    MouseMove, MouseDown, MouseUp, KeyDown, KeyUp,
    /// <summary>A key that was swallowed (an Echostep hotkey); held modifiers are not recorded as lone presses.</summary>
    Suppressed,
    MouseWheel,
    /// <summary>Another program's window came to the front.</summary>
    FocusChange,
}

/// <summary>One raw input event captured by the hooks.</summary>
public readonly record struct RecEvent(RecEventKind Kind, long TimeMs, int X = 0, int Y = 0,
    MouseButton Button = MouseButton.Left, int Vk = 0, int Delta = 0, bool Horizontal = false,
    string? Program = null, string? Title = null);

public sealed class RecordingOptions
{
    public bool RecordMouseMoves { get; set; } = true;
    /// <summary>Moves closer than this (pixels) to the last kept point are dropped.</summary>
    public int MinMoveDistance { get; set; } = 8;
    /// <summary>Moves arriving sooner than this after the last kept point are dropped (mouse sensitivity).</summary>
    public int MinMoveIntervalMs { get; set; }
    /// <summary>Gaps longer than this become Wait actions.</summary>
    public int WaitThresholdMs { get; set; } = 50;
    /// <summary>Record buttons and keys as separate Down and Up rows (captures drags and holds).</summary>
    public bool SeparateDownUp { get; set; }
    /// <summary>Record a Window focus action when another window comes to the front.</summary>
    public bool RecordFocusChanges { get; set; } = true;
}

/// <summary>
/// Turns raw hook events into actions: clicks become Click, movements become thinned Move, the wheel
/// becomes Wheel, keys become Keypress (modifiers folded in), window changes become Window focus,
/// and gaps over the threshold become Wait.
/// </summary>
public static class RecorderLogic
{
    public static List<MacroAction> BuildActions(IEnumerable<RecEvent> events, RecordingOptions options)
    {
        var result = new List<MacroAction>();
        long? lastActionTime = null;
        (int X, int Y)? lastPoint = null;
        long lastMoveTime = long.MinValue;
        var held = Modifiers.None;
        var keysDown = new HashSet<int>();
        int? loneModifierVk = null; // a modifier pressed with nothing else since

        void Emit(MacroAction action, long time)
        {
            if (lastActionTime is { } t)
            {
                var gap = time - t;
                if (gap > options.WaitThresholdMs) result.Add(new WaitAction { Ms = (int)Math.Min(gap, int.MaxValue) });
            }
            result.Add(action);
            lastActionTime = time;
        }

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RecEventKind.MouseMove:
                    if (!options.RecordMouseMoves) break;
                    if (lastPoint is { } lp)
                    {
                        var dx = e.X - lp.X;
                        var dy = e.Y - lp.Y;
                        if (dx * dx + dy * dy < options.MinMoveDistance * options.MinMoveDistance) break;
                        if (lastMoveTime != long.MinValue && e.TimeMs - lastMoveTime < options.MinMoveIntervalMs) break;
                    }
                    lastPoint = (e.X, e.Y);
                    lastMoveTime = e.TimeMs;
                    Emit(new MoveAction { X = e.X, Y = e.Y }, e.TimeMs);
                    break;

                case RecEventKind.MouseDown:
                    loneModifierVk = null;
                    lastPoint = (e.X, e.Y);
                    Emit(new ClickAction { X = e.X, Y = e.Y, Button = e.Button, Kind = options.SeparateDownUp ? ClickKind.Down : ClickKind.Click }, e.TimeMs);
                    break;

                case RecEventKind.MouseUp:
                    if (!options.SeparateDownUp) break;
                    lastPoint = (e.X, e.Y);
                    Emit(new ClickAction { X = e.X, Y = e.Y, Button = e.Button, Kind = ClickKind.Up }, e.TimeMs);
                    break;

                case RecEventKind.MouseWheel:
                    loneModifierVk = null;
                    if (result.Count > 0 && result[^1] is WheelAction prev && prev.Horizontal == e.Horizontal &&
                        Math.Sign(prev.Value) == Math.Sign(e.Delta) && lastActionTime is { } lt && e.TimeMs - lt <= options.WaitThresholdMs)
                    {
                        prev.Value += e.Delta; // one smooth scroll becomes one row
                        lastActionTime = e.TimeMs;
                    }
                    else Emit(new WheelAction { Value = e.Delta, Horizontal = e.Horizontal }, e.TimeMs);
                    break;

                case RecEventKind.FocusChange:
                    if (!options.RecordFocusChanges || string.IsNullOrWhiteSpace(e.Title)) break;
                    Emit(new WindowFocusAction { Program = string.IsNullOrWhiteSpace(e.Program) ? null : e.Program, Title = e.Title, TitleMatch = TitleMatch.Exact }, e.TimeMs);
                    break;

                case RecEventKind.KeyDown:
                    if (options.SeparateDownUp)
                    {
                        if (keysDown.Add(e.Vk)) // ignore auto-repeat while a key is held
                            Emit(new KeyAction { Key = KeyNames.GetName(GenericModifierVk(e.Vk)), Option = KeyOption.Down }, e.TimeMs);
                    }
                    else if (KeyNames.IsModifier(e.Vk))
                    {
                        var m = KeyNames.ModifierOf(e.Vk);
                        if (!held.HasFlag(m)) loneModifierVk = e.Vk; // ignore auto-repeat of a held modifier
                        held |= m;
                    }
                    else
                    {
                        loneModifierVk = null;
                        Emit(new KeyAction { Key = KeyNames.GetName(e.Vk), Modifiers = held }, e.TimeMs);
                    }
                    break;

                case RecEventKind.Suppressed:
                    loneModifierVk = null;
                    break;

                case RecEventKind.KeyUp:
                    if (options.SeparateDownUp)
                    {
                        if (keysDown.Remove(e.Vk))
                            Emit(new KeyAction { Key = KeyNames.GetName(GenericModifierVk(e.Vk)), Option = KeyOption.Up }, e.TimeMs);
                    }
                    else if (KeyNames.IsModifier(e.Vk))
                    {
                        var m = KeyNames.ModifierOf(e.Vk);
                        held &= ~m;
                        if (loneModifierVk == e.Vk)
                            Emit(new KeyAction { Key = KeyNames.GetName(GenericModifierVk(e.Vk)), Modifiers = held }, e.TimeMs);
                        loneModifierVk = null;
                    }
                    break;
            }
        }

        TrimTail(result);
        return result;
    }

    /// <summary>Drops trailing moves and waits: they are the trip to the Stop button.</summary>
    public static void TrimTail(List<MacroAction> actions)
    {
        while (actions.Count > 0 && actions[^1] is MoveAction or WaitAction) actions.RemoveAt(actions.Count - 1);
    }

    /// <summary>Lone left/right Ctrl, Alt and Shift are recorded as the generic key; Win keeps its side.</summary>
    private static int GenericModifierVk(int vk) => vk switch
    {
        KeyNames.VK_LSHIFT or KeyNames.VK_RSHIFT => KeyNames.VK_SHIFT,
        KeyNames.VK_LCONTROL or KeyNames.VK_RCONTROL => KeyNames.VK_CONTROL,
        KeyNames.VK_LMENU or KeyNames.VK_RMENU => KeyNames.VK_MENU,
        _ => vk,
    };
}
