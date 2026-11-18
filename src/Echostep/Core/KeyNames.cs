namespace Echostep.Core;

/// <summary>
/// Maps key names used in .estep files ("Enter", "A", "F5", "Left"…) to Windows virtual-key codes and back.
/// Names follow the WinForms <c>Keys</c> enum where possible, with friendly aliases accepted when reading.
/// </summary>
public static class KeyNames
{
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> VkToName = new();

    /// <summary>Canonical names, in the order shown in key pickers.</summary>
    public static IReadOnlyList<string> AllNames { get; }

    static KeyNames()
    {
        var list = new List<string>();
        void Add(string name, int vk, params string[] aliases)
        {
            NameToVk[name] = vk;
            VkToName.TryAdd(vk, name);
            list.Add(name);
            foreach (var a in aliases) NameToVk.TryAdd(a, vk);
        }

        Add("Enter", 0x0D, "Return");
        Add("Escape", 0x1B, "Esc");
        Add("Space", 0x20, " ");
        Add("Tab", 0x09);
        Add("Back", 0x08, "Backspace", "BkSp");
        Add("Delete", 0x2E, "Del");
        Add("Insert", 0x2D, "Ins");
        Add("Home", 0x24);
        Add("End", 0x23);
        Add("PageUp", 0x21, "PgUp", "Prior");
        Add("PageDown", 0x22, "PgDn", "Next");
        Add("Left", 0x25, "LeftArrow");
        Add("Up", 0x26, "UpArrow");
        Add("Right", 0x27, "RightArrow");
        Add("Down", 0x28, "DownArrow");
        for (var c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (var d = 0; d <= 9; d++) Add($"D{d}", 0x30 + d, d.ToString());
        for (var f = 1; f <= 24; f++) Add($"F{f}", 0x70 + f - 1);
        for (var n = 0; n <= 9; n++) Add($"NumPad{n}", 0x60 + n, $"Num{n}");
        Add("Multiply", 0x6A, "NumMultiply");
        Add("Add", 0x6B, "NumAdd", "NumPlus");
        Add("Subtract", 0x6D, "NumSubtract", "NumMinus");
        Add("Decimal", 0x6E, "NumDecimal");
        Add("Divide", 0x6F, "NumDivide");
        Add("Shift", VK_SHIFT, "ShiftKey");
        Add("Ctrl", VK_CONTROL, "ControlKey", "Control");
        Add("Alt", VK_MENU, "Menu");
        Add("LShift", VK_LSHIFT, "LShiftKey");
        Add("RShift", VK_RSHIFT, "RShiftKey");
        Add("LCtrl", VK_LCONTROL, "LControlKey");
        Add("RCtrl", VK_RCONTROL, "RControlKey");
        Add("LAlt", VK_LMENU, "LMenu");
        Add("RAlt", VK_RMENU, "RMenu", "AltGr");
        Add("LWin", VK_LWIN, "Win", "Windows");
        Add("RWin", VK_RWIN);
        Add("Apps", 0x5D, "ContextMenu");
        Add("CapsLock", 0x14, "Capital");
        Add("NumLock", 0x90);
        Add("Scroll", 0x91, "ScrollLock");
        Add("PrintScreen", 0x2C, "PrtSc", "Snapshot");
        Add("Pause", 0x13, "Break");
        Add("OemSemicolon", 0xBA, ";", "Oem1");
        Add("Oemplus", 0xBB, "=", "OemPlus");
        Add("Oemcomma", 0xBC, ",", "OemComma");
        Add("OemMinus", 0xBD, "-");
        Add("OemPeriod", 0xBE, ".");
        Add("OemQuestion", 0xBF, "/", "Oem2");
        Add("Oemtilde", 0xC0, "`", "OemTilde", "Oem3");
        Add("OemOpenBrackets", 0xDB, "[", "Oem4");
        Add("OemPipe", 0xDC, "\\", "Oem5");
        Add("OemCloseBrackets", 0xDD, "]", "Oem6");
        Add("OemQuotes", 0xDE, "'", "Oem7");
        Add("OemBackslash", 0xE2, "Oem102");
        Add("VolumeMute", 0xAD);
        Add("VolumeDown", 0xAE);
        Add("VolumeUp", 0xAF);
        Add("MediaNextTrack", 0xB0);
        Add("MediaPreviousTrack", 0xB1);
        Add("MediaStop", 0xB2);
        Add("MediaPlayPause", 0xB3);
        AllNames = list;
    }

    public static bool TryGetVk(string? name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (NameToVk.TryGetValue(n, out vk)) return true;
        // "VK_xx" or "0x41" hex escape hatch for keys without a name.
        if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(n[2..], System.Globalization.NumberStyles.HexNumber, null, out vk) && vk is > 0 and < 256)
            return true;
        return false;
    }

    public static string GetName(int vk) => VkToName.TryGetValue(vk, out var n) ? n : $"0x{vk:X2}";

    /// <summary>Canonical spelling of a key name (e.g. "esc" → "Escape"), or the input if unknown.</summary>
    public static string Normalize(string name) => TryGetVk(name, out var vk) ? GetName(vk) : name;

    public static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU
        or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    public static Modifiers ModifierOf(int vk) => vk switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => Modifiers.Shift,
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => Modifiers.Ctrl,
        VK_MENU or VK_LMENU or VK_RMENU => Modifiers.Alt,
        VK_LWIN or VK_RWIN => Modifiers.Win,
        _ => Modifiers.None,
    };

    /// <summary>Keys that need KEYEVENTF_EXTENDEDKEY when sent with SendInput.</summary>
    public static bool IsExtended(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PgUp PgDn End Home arrows
        or 0x2D or 0x2E or 0x2C // Insert Delete PrintScreen
        or 0x6F or 0x90 // Divide NumLock
        or VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN or 0x5D
        or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB2 or 0xB3;
}

[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

public static class ModifiersExtensions
{
    /// <summary>"Ctrl+Shift+" style prefix (empty for none).</summary>
    public static string ToPrefix(this Modifiers m)
    {
        var s = "";
        if (m.HasFlag(Modifiers.Ctrl)) s += "Ctrl+";
        if (m.HasFlag(Modifiers.Alt)) s += "Alt+";
        if (m.HasFlag(Modifiers.Shift)) s += "Shift+";
        if (m.HasFlag(Modifiers.Win)) s += "Win+";
        return s;
    }
}
