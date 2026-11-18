using System.Runtime.InteropServices;
using System.Text;
using Echostep.Core;

namespace Echostep.Win;

/// <summary>Win32 calls used by Echostep.</summary>
internal static class Native
{
    // ---- DPI ---------------------------------------------------------------------------------

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    /// <summary>Makes coordinates physical pixels at any display scaling. Call before any window is created.</summary>
    public static void MakeDpiAware()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            SetProcessDPIAware();
        }
    }

    // ---- Screen pixels -----------------------------------------------------------------------

    public const uint CLR_INVALID = 0xFFFFFFFF;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>Reads one screen pixel; null for CLR_INVALID (off-screen or desktop locked).</summary>
    public static Rgb? ReadPixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            var c = GetPixel(dc, x, y);
            return c == CLR_INVALID ? null : Rgb.FromColorRef(c);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // ---- Cursor and metrics ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    // ---- SendInput ---------------------------------------------------------------------------

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Marks input sent by Echostep (dwExtraInfo), in addition to Windows' own injected flag.</summary>
    public static readonly IntPtr InjectedSignature = new(0x4F524543); // "OREC"

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new PlaybackException(
                "Windows blocked the input (the target may be running as administrator; run Echostep elevated to automate it).");
    }

    public static INPUT MouseInput(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, dwExtraInfo = InjectedSignature } },
    };

    public static INPUT KeyInput(int vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (KeyNames.IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */),
                    dwFlags = flags,
                    dwExtraInfo = InjectedSignature,
                },
            },
        };
    }

    /// <summary>Moves the pointer to a physical screen pixel (any monitor).</summary>
    public static void MoveMouse(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        // Absolute coordinates are 0..65535 across the virtual desktop; round up so we land on the pixel itself.
        var nx = (int)(((long)(x - left) * 65536 + width - 1) / width);
        var ny = (int)(((long)(y - top) * 65536 + height - 1) / height);
        Send(MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
        // Rounding on unusual monitor layouts can be a pixel off; pixel-exact positioning matters here.
        if (GetCursorPos(out var p) && (p.X != x || p.Y != y)) SetCursorPos(x, y);
    }

    /// <summary>Moves to x, y and clicks the button.</summary>
    public static void Click(int x, int y, MouseButton button = MouseButton.Left)
    {
        MoveMouse(x, y);
        ButtonEvent(button, true);
        ButtonEvent(button, false);
    }

    public const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>Types text with Unicode key events, so any character works whatever the keyboard layout.
    /// Line breaks press Enter and tabs press Tab.</summary>
    public static void TypeText(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch is '\n' or '\t')
            {
                var vk = ch == '\n' ? 0x0D : 0x09;
                list.Add(KeyInput(vk, up: false));
                list.Add(KeyInput(vk, up: true));
                continue;
            }
            foreach (var up in new[] { false, true })
                list.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = InjectedSignature },
                    },
                });
        }
        // Long texts go in chunks so a busy target window does not drop keys.
        for (var i = 0; i < list.Count; i += 200)
        {
            Send(list.Skip(i).Take(200).ToArray());
            if (i + 200 < list.Count) Thread.Sleep(10);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Presses and releases a key with its modifiers.</summary>
    public static void PressKey(int vk, Modifiers mods = Modifiers.None)
    {
        var list = new List<INPUT>();
        var modVks = new List<int>();
        if (mods.HasFlag(Modifiers.Ctrl)) modVks.Add(KeyNames.VK_CONTROL);
        if (mods.HasFlag(Modifiers.Alt)) modVks.Add(KeyNames.VK_MENU);
        if (mods.HasFlag(Modifiers.Shift)) modVks.Add(KeyNames.VK_SHIFT);
        if (mods.HasFlag(Modifiers.Win)) modVks.Add(KeyNames.VK_LWIN);
        foreach (var m in modVks) list.Add(KeyInput(m, up: false));
        list.Add(KeyInput(vk, up: false));
        list.Add(KeyInput(vk, up: true));
        for (var i = modVks.Count - 1; i >= 0; i--) list.Add(KeyInput(modVks[i], up: true));
        Send(list.ToArray());
    }

    public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;

    /// <summary>Presses or releases a mouse button where the pointer is.</summary>
    public static void ButtonEvent(MouseButton button, bool down)
    {
        var (flag, data) = button switch
        {
            MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1u),
            MouseButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2u),
            _ => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
        };
        var input = MouseInput(flag);
        input.U.mi.mouseData = data;
        Send(input);
    }

    /// <summary>Turns the wheel; 120 is one notch, positive is up (or right when horizontal).</summary>
    public static void Wheel(int amount, bool horizontal)
    {
        var input = MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);
        input.U.mi.mouseData = unchecked((uint)amount);
        Send(input);
    }

    /// <summary>Presses or releases one key.</summary>
    public static void KeyEvent(int vk, bool down) => Send(KeyInput(vk, up: !down));

    // ---- Screen capture ----------------------------------------------------------------------

    /// <summary>Captures a zone (or the whole virtual screen when width or height is 0) as 0x00RRGGBB pixels.</summary>
    public static ScreenImage? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        if (width <= 0 || height <= 0) return null;
        try
        {
            using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var pixels = new int[width * height];
                if (data.Stride == width * 4) Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                else
                    for (var row = 0; row < height; row++)
                        Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * width, width);
                for (var i = 0; i < pixels.Length; i++) pixels[i] &= 0x00FFFFFF;
                return new ScreenImage(x, y, width, height, pixels);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch (Exception)
        {
            return null; // locked desktop or secure screen
        }
    }

    /// <summary>The monitors as text, e.g. "2 monitors: 1920x1080 at 0,0; 2560x1440 at 1920,0".</summary>
    public static string ScreenLayout()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select(s => $"{s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y}").ToList();
        return $"{screens.Count} monitor{(screens.Count == 1 ? "" : "s")}: {string.Join("; ", screens)}";
    }

    /// <summary>Hides a window from screen captures (so its own updates are not seen as screen changes).</summary>
    public static void ExcludeFromCapture(IntPtr hWnd, bool exclude)
    {
        try { SetWindowDisplayAffinity(hWnd, exclude ? WDA_EXCLUDEFROMCAPTURE : 0); }
        catch (Exception) { /* not supported: ignore */ }
    }

    // ---- Window focus ------------------------------------------------------------------------

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>File name of the program that owns a window ("notepad.exe"), or null.</summary>
    public static string? ProgramOf(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Finds the first visible window an action describes.</summary>
    public static IntPtr FindWindow(WindowFocusAction a)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsOwnWindow(h)) return true;
            var title = GetTitle(h);
            if (title.Length == 0 || !a.TitleMatches(title)) return true;
            if (!string.IsNullOrWhiteSpace(a.Program) && !a.ProgramMatches(ProgramOf(h))) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Brings the described window to the front and applies its position and size. False if it is not there.</summary>
    public static bool FocusWindow(WindowFocusAction a)
    {
        var h = FindWindow(a);
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        if (GetForegroundWindow() != h && !SetForegroundWindow(h))
        {
            // Windows only lets the foreground process change focus; a tap of Alt lifts that lock.
            Send(KeyInput(KeyNames.VK_MENU, up: false), KeyInput(KeyNames.VK_MENU, up: true));
            SetForegroundWindow(h);
        }
        var move = a.Left is not null && a.Top is not null;
        var size = a.Width is not null && a.Height is not null;
        if (move || size)
        {
            const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
            SetWindowPos(h, IntPtr.Zero, a.Left ?? 0, a.Top ?? 0, a.Width ?? 0, a.Height ?? 0,
                SWP_NOZORDER | SWP_NOACTIVATE | (move ? 0 : SWP_NOMOVE) | (size ? 0 : SWP_NOSIZE));
        }
        return true;
    }

    // ---- Power and start-up ------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);

    /// <summary>1 shutdown, 2 restart, 3 lock, 4 log out, 5 standby (the After playback choices).</summary>
    public static void PowerAction(int action)
    {
        void Shutdown(string args) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
        switch (action)
        {
            case 1: Shutdown("/s /t 0"); break;
            case 2: Shutdown("/r /t 0"); break;
            case 3: LockWorkStation(); break;
            case 4: Shutdown("/l"); break;
            case 5: SetSuspendState(false, false, false); break;
        }
    }

    /// <summary>Adds or removes Echostep from the programs started when the user signs in.</summary>
    public static void SetStartOnLogin(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue("Echostep", $"\"{exe}\"");
            else key.DeleteValue("Echostep", throwOnMissingValue: false);
            key.DeleteValue("OpenMR", throwOnMissingValue: false); // entries from before the rename
            key.DeleteValue("OpenRecorder", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("Could not change the start-on-login setting: " + ex.Message);
        }
    }

    // ---- Foreground-window events --------------------------------------------------------------

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;

    // ---- Low-level hooks ---------------------------------------------------------------------

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    public const uint LLMHF_INJECTED = 0x01, LLKHF_INJECTED = 0x10;
    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Hotkeys -----------------------------------------------------------------------------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static uint ToHotkeyModifiers(Modifiers m) =>
        (m.HasFlag(Modifiers.Alt) ? MOD_ALT : 0) | (m.HasFlag(Modifiers.Ctrl) ? MOD_CONTROL : 0) |
        (m.HasFlag(Modifiers.Shift) ? MOD_SHIFT : 0) | (m.HasFlag(Modifiers.Win) ? MOD_WIN : 0);

    // ---- Windows -----------------------------------------------------------------------------

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    public const uint GA_ROOT = 2;

    public static string GetTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Finds a visible, non-minimised top-level window: an exact (case-insensitive) title match first,
    /// otherwise the first title containing the text.
    /// </summary>
    public static IntPtr FindWindowByTitle(string title)
    {
        var t = title.Trim();
        IntPtr exact = IntPtr.Zero, partial = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var text = GetTitle(h);
            if (text.Length == 0) return true;
            if (string.Equals(text, t, StringComparison.OrdinalIgnoreCase)) { exact = h; return false; }
            if (partial == IntPtr.Zero && text.Contains(t, StringComparison.OrdinalIgnoreCase)) partial = h;
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : partial;
    }

    /// <summary>Titles of visible top-level windows (minimized included), for the If action.</summary>
    public static List<string> TopLevelWindowTitles()
    {
        var titles = new List<string>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetTitle(h) is { Length: > 0 } t) titles.Add(t);
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    public static (int X, int Y)? ClientOrigin(IntPtr hWnd)
    {
        var p = new POINT(0, 0);
        return hWnd != IntPtr.Zero && ClientToScreen(hWnd, ref p) ? (p.X, p.Y) : null;
    }

    public static bool IsOwnWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>The top-level window under a screen point, and its title.</summary>
    public static (IntPtr Handle, string Title) TopLevelWindowAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT(x, y));
        var root = h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
        return (root, root == IntPtr.Zero ? "" : GetTitle(root));
    }

    // ---- Misc --------------------------------------------------------------------------------

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hWnd, string? appName, string? idList);

    /// <summary>Dark title bar (Windows 10 2004+ / Windows 11); silently does nothing on older systems.</summary>
    public static void UseDarkTitleBar(IntPtr hWnd, bool dark = true)
    {
        try
        {
            var on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hWnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hWnd, 19, ref on, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>Dark scrollbars for a control, where Windows supports it.</summary>
    public static void UseDarkScrollbars(IntPtr hWnd, bool dark = true)
    {
        try { SetWindowTheme(hWnd, dark ? "DarkMode_Explorer" : "Explorer", null); }
        catch (Exception) { /* cosmetic only */ }
    }

    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private static int _timerUsers;

    /// <summary>Raises timer resolution to 1 ms while playing or watching, so waits and polls are accurate.</summary>
    public static void AcquireHighResTimer()
    {
        if (Interlocked.Increment(ref _timerUsers) == 1) timeBeginPeriod(1);
    }

    public static void ReleaseHighResTimer()
    {
        if (Interlocked.Decrement(ref _timerUsers) == 0) timeEndPeriod(1);
    }
}
