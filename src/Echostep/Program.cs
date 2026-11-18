using Echostep.Core;
using Echostep.UI;
using Echostep.Win;

namespace Echostep;

internal static class Program
{
    /// <summary>Settings shared by the whole app (loaded from settings.json at startup).</summary>
    public static AppSettings Settings { get; set; } = new();

    [STAThread]
    private static void Main(string[] args)
    {
        // Coordinates must be physical pixels at any display scaling, before any window exists.
        // WinForms' own call comes first so it knows the mode; Native.MakeDpiAware() covers the case where it could not set it.
#if !TYPECHECK
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
        Native.MakeDpiAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // After "Restart now" (a new look): wait for the old window to close so its hotkeys are free.
        var after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(10_000); }
            catch (ArgumentException) { /* already gone */ }
        }

        Settings = AppSettings.Load(AppPaths.SettingsFile);
        // The look is chosen before any window or menu renderer is created.
        Theme.Init(Settings.Theme, Settings.AccentColor, Settings.TextSize);
        ToolStripManager.Renderer = new DarkRenderer();
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Write("Unhandled: " + ex); };

        // Echostep.exe "macro.estep" [--play] [--exit]: open a macro, optionally play it, and close afterwards (used by schedules).
        var file = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(file, playOnStart: Flag("--play"), exitAfterPlay: Flag("--exit")));
    }

    private static void ShowCrash(Exception ex)
    {
        Log.Write("Error: " + ex);
        MessageBox.Show(ex.Message, "Echostep error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
