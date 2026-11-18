using Echostep.Core;

namespace Echostep.Win;

/// <summary>Sends input with SendInput.</summary>
internal sealed class WinInput : IInputDriver
{
    public static readonly WinInput Instance = new();
    public (int X, int Y) Position => Native.CursorPosition();
    public void MoveTo(int x, int y) => Native.MoveMouse(x, y);
    public void ButtonEvent(MouseButton button, bool down) => Native.ButtonEvent(button, down);
    public void Wheel(int amount, bool horizontal) => Native.Wheel(amount, horizontal);
    public void KeyEvent(int vk, bool down) => Native.KeyEvent(vk, down);
    public void Click(int x, int y, MouseButton button) => Native.Click(x, y, button);
    public void KeyPress(int vk, Modifiers modifiers) => Native.PressKey(vk, modifiers);
    public void TypeText(string text) => Native.TypeText(text);
}

/// <summary>Clipboard, programs, message boxes and sounds for the actions under More. Safe to call from any thread.</summary>
internal sealed class WinHost : IHostServices
{
    public static readonly WinHost Instance = new();

    /// <summary>The clipboard needs a single-threaded apartment; playback runs on a pool thread.</summary>
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new PlaybackException("the clipboard is busy: " + error.Message);
        return result;
    }

    public string? GetClipboard() => OnSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "");

    public void SetClipboard(string text) => OnSta(() =>
    {
        if (text.Length == 0) Clipboard.Clear();
        else Clipboard.SetText(text);
        return 0;
    });

    public void Run(string target, string? arguments, bool wait, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
        using var process = System.Diagnostics.Process.Start(psi);
        if (!wait || process is null) return;
        while (!process.WaitForExit(200)) ct.ThrowIfCancellationRequested();
    }

    public bool ShowMessage(string text, bool question)
    {
        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        const int IDYES = 6;
        var type = MB_TOPMOST | MB_SETFOREGROUND | (question ? MB_YESNO | MB_ICONQUESTION : MB_ICONINFORMATION);
        var answer = Native.MessageBoxW(IntPtr.Zero, text, "Echostep", type);
        return !question || answer == IDYES;
    }

    public void PlaySound(MessageSound sound)
    {
        switch (sound)
        {
            case MessageSound.Info: System.Media.SystemSounds.Asterisk.Play(); break;
            case MessageSound.Warning: System.Media.SystemSounds.Exclamation.Play(); break;
            case MessageSound.Error: System.Media.SystemSounds.Hand.Play(); break;
            case MessageSound.Beep: System.Media.SystemSounds.Beep.Play(); break;
        }
    }
}

/// <summary>Reads pixels with GetPixel on the screen DC and finds windows by title.</summary>
internal sealed class WinScreen : IScreenReader
{
    public static readonly WinScreen Instance = new();
    public Rgb? GetPixel(int x, int y) => Native.ReadPixel(x, y);
    public IEnumerable<string> WindowTitles() => Native.TopLevelWindowTitles();

    public string? ForegroundTitle()
    {
        var h = Native.GetForegroundWindow();
        return h == IntPtr.Zero ? null : Native.GetTitle(h);
    }

    public (int X, int Y)? FindWindowClientOrigin(string title) => Native.ClientOrigin(Native.FindWindowByTitle(title));
    public ScreenImage? Capture(int x, int y, int width, int height) => Native.Capture(x, y, width, height);
    public bool FocusWindow(WindowFocusAction action) => Native.FocusWindow(action);
}
