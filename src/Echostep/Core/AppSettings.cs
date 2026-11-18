using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echostep.Core;

/// <summary>A key with modifiers, written like "Ctrl+Alt+Escape".</summary>
public readonly record struct Hotkey(Modifiers Modifiers, int Vk)
{
    public bool IsEmpty => Vk == 0;

    public override string ToString() => IsEmpty ? "" : Modifiers.ToPrefix() + KeyNames.GetName(Vk);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return true; // empty = no hotkey
        var mods = Modifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Modifiers.Ctrl; break;
                case "alt": mods |= Modifiers.Alt; break;
                case "shift": mods |= Modifiers.Shift; break;
                case "win" or "windows": mods |= Modifiers.Win; break;
                default: return false;
            }
        }
        if (!KeyNames.TryGetVk(parts[^1], out var vk) || KeyNames.IsModifier(vk)) return false;
        hotkey = new Hotkey(mods, vk);
        return true;
    }
}

/// <summary>Settings saved as settings.json beside the EXE (or in %APPDATA%\Echostep if that folder is read-only).</summary>
public sealed class AppSettings
{
    [Category("Hotkeys"), DisplayName("Stop / emergency stop"),
     Description("Stops recording and playback, halts running rule actions and turns watching off. Works while the window is hidden.")]
    public string StopHotkey { get; set; } = "Ctrl+Alt+Escape";

    [Category("Hotkeys"), DisplayName("Color picker"),
     Description("Opens the live color picker. While it is open, Enter adds a pixel rule at the cursor and Escape closes it.")]
    public string PickerHotkey { get; set; } = "Ctrl+Alt+P";

    [Category("Hotkeys"), DisplayName("Pause / resume rules"),
     Description("Turns background pixel rules on and off.")]
    public string WatchHotkey { get; set; } = "Ctrl+Alt+W";

    [Category("Recording"), DisplayName("Record mouse moves")]
    public bool RecordMouseMoves { get; set; } = true;

    [Category("Recording"), DisplayName("Minimum mouse movement (px)"),
     Description("Mouse moves shorter than this are dropped while recording.")]
    public int MinMoveDistance { get; set; } = 8;

    [Category("Recording"), DisplayName("Wait threshold (ms)"),
     Description("Pauses longer than this become Wait actions.")]
    public int WaitThresholdMs { get; set; } = 50;

    [Category("Rules"), DisplayName("Watch on startup"),
     Description("Start watching pixel rules as soon as Echostep opens.")]
    public bool WatchOnStartup { get; set; }

    [Category("Interface"), DisplayName("Magnifier zoom"),
     Description("How many screen pixels the picker magnifier shows across (odd number, 7–41).")]
    public int MagnifierPixels { get; set; } = 15;

    [Category("Interface"), DisplayName("Show crosshair while playing"),
     Description("Draws a crosshair on the pixel a playing macro is watching.")]
    public bool ShowCrosshair { get; set; } = true;

    [Category("Hotkeys"), DisplayName("Stop recording"),
     Description("A single key that ends a recording. It is swallowed and never recorded.")]
    public string StopRecordingKey { get; set; } = "Enter";

    [Category("Hotkeys"), DisplayName("Pause / resume playback"),
     Description("A single key that pauses and resumes playback. Only real key presses count, not keys the macro sends.")]
    public string PauseKey { get; set; } = "Escape";

    [Category("Recording")] public bool RecordDownUp { get; set; }
    [Category("Recording")] public bool RecordFocusChanges { get; set; } = true;
    /// <summary>1 (few points) to 40 (every point) for recorded mouse paths.</summary>
    [Category("Recording")] public int MouseSensitivity { get; set; } = 40;

    [Category("Playback")] public bool AbortOnKeyPress { get; set; }
    [Category("Playback")] public bool AbortOnMouseMove { get; set; }
    [Category("Playback")] public int MousePath { get; set; }
    [Category("Playback")] public int RepeatCount { get; set; } = 1;
    [Category("Playback")] public int AfterPlayback { get; set; }
    [Category("Playback")] public bool ShowCountdown { get; set; } = true;
    [Category("Playback")] public int PlaybackFilter { get; set; } = (int)Core.PlaybackFilter.All;

    /// <summary>0 dark, 1 light, 2 match Windows' app mode. Takes effect after a restart.</summary>
    [Category("Interface")] public int Theme { get; set; }
    /// <summary>Accent color name (Blue, Teal, Green, Purple, Orange, Pink, Red).</summary>
    [Category("Interface")] public string AccentColor { get; set; } = "Blue";
    /// <summary>0 normal, 1 large text in lists, dialogs and menus.</summary>
    [Category("Interface")] public int TextSize { get; set; }

    /// <summary>Playback speed multiplier; 1 is normal.</summary>
    [Category("Playback")] public double PlaybackSpeed { get; set; } = 1;
    /// <summary>Show the small always-on-top progress box while playing.</summary>
    [Category("Playback")] public bool ShowOverlay { get; set; }
    /// <summary>Hotkeys that play a macro file directly.</summary>
    [Browsable(false)] public List<MacroHotkey> MacroHotkeys { get; set; } = [];
    /// <summary>Schedules created in Windows Task Scheduler, kept here to list and remove them.</summary>
    [Browsable(false)] public List<MacroSchedule> Schedules { get; set; } = [];

    [Category("Interface")] public bool ShowDeleteConfirmation { get; set; } = true;
    [Category("Interface")] public bool HideWhenRecording { get; set; } = true;
    [Category("Interface")] public bool StartMinimized { get; set; }
    [Category("Interface")] public bool StartOnLogin { get; set; }
    [Category("Interface")] public bool ShowLineNumbers { get; set; } = true;
    [Browsable(false)] public List<int> ColumnWidths { get; set; } = [];
    /// <summary>Action list columns: width of the # column, then the share of each other column.</summary>
    [Browsable(false)] public List<float> ColumnLayout { get; set; } = [];
    /// <summary>Rules list columns, in the same form.</summary>
    [Browsable(false)] public List<float> RuleColumnLayout { get; set; } = [];

    [Browsable(false)]
    public List<string> RecentFiles { get; set; } = [];

    [JsonIgnore, Browsable(false)]
    public RecordingOptions Recording => new()
    {
        RecordMouseMoves = RecordMouseMoves,
        MinMoveDistance = Math.Max(0, MinMoveDistance),
        WaitThresholdMs = Math.Max(0, WaitThresholdMs),
        SeparateDownUp = RecordDownUp,
        RecordFocusChanges = RecordFocusChanges,
        MinMoveIntervalMs = Math.Clamp(40 - MouseSensitivity, 0, 39) * 5,
    };

    public const int MaxRecent = 8;

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
    }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), MacroFile.Json) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not read settings ({ex.Message}); using defaults.");
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, MacroFile.Json), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }
}

/// <summary>
/// Where settings.json, rules.json and the Macros folder live: the folder Echostep is installed in (or the portable
/// EXE's folder), so the app and everything it saves stay together. Only if that folder cannot be written to
/// does it fall back to %APPDATA%\Echostep.
/// </summary>
public static class AppPaths
{
    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ChooseDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RulesFile => Path.Combine(DataDir, "rules.json");

    /// <summary>Default folder for saving and opening macros (created when first needed).</summary>
    public static string MacrosDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "Macros");
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DataDir; }
            return dir;
        }
    }

    private static string ChooseDataDir()
    {
        var exeDir = AppContext.BaseDirectory;
        if (IsWritable(exeDir)) return exeDir;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Echostep");
        // Keep using the folder from before the rename if it is the only one there.
        if (!Directory.Exists(dir))
            foreach (var old in new[] { "OpenMR", "OpenRecorder" })
            {
                var legacy = Path.Combine(appData, old);
                if (Directory.Exists(legacy)) return legacy;
            }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".echostep-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Timestamped run log shown in the Log tab.</summary>
public static class Log
{
    private const int Keep = 2000;
    private static readonly object Gate = new();
    private static readonly Queue<string> Lines = new();

    /// <summary>Raised on the writing thread; UI subscribers must marshal to the UI thread.</summary>
    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            Lines.Enqueue(line);
            while (Lines.Count > Keep) Lines.Dequeue();
        }
        LineWritten?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Gate) return Lines.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) Lines.Clear();
    }
}

/// <summary>A hotkey that plays a macro file.</summary>
public sealed class MacroHotkey
{
    public string Hotkey { get; set; } = "";
    public string File { get; set; } = "";
}

/// <summary>A Windows Task Scheduler task that starts Echostep to play a macro.</summary>
public sealed class MacroSchedule
{
    /// <summary>Task name under the Echostep folder in Task Scheduler.</summary>
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>"HH:mm".</summary>
    public string Time { get; set; } = "08:00";
    /// <summary>Day keys (mon…sun); all seven means every day.</summary>
    public List<string> Days { get; set; } = [];

    public string Describe() => $"{(Days.Count == 7 ? "every day" : IfAction.DaysText(Days))} at {Time}";

    /// <summary>The schtasks.exe arguments that create this schedule.</summary>
    public IReadOnlyList<string> CreateArguments(string exePath)
    {
        var args = new List<string> { "/Create", "/F", "/TN", TaskPath, "/TR", $"\"{exePath}\" \"{File}\" --play --exit", "/ST", Time };
        if (Days.Count == 7) args.AddRange(["/SC", "DAILY"]);
        else
        {
            args.AddRange(["/SC", "WEEKLY", "/D"]);
            args.Add(string.Join(",", IfAction.DayKeys.Where(d => Days.Contains(d, StringComparer.OrdinalIgnoreCase)).Select(d => d.ToUpperInvariant())));
        }
        return args;
    }

    public string TaskPath => @"Echostep\" + Name;
}
