using System.Diagnostics;
using System.Runtime.InteropServices;
using Echostep.Core;

namespace Echostep.Win;

/// <summary>
/// Low-level mouse and keyboard hooks (WH_MOUSE_LL, WH_KEYBOARD_LL) on their own thread with its own
/// message loop, so a busy UI never delays input. Subclasses decide what to do with each event.
/// </summary>
internal abstract class HookThread : IDisposable
{
    private Native.HookProc? _mouseProc, _keyProc; // kept alive while hooked
    private IntPtr _mouseHook, _keyHook;
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private string? _startError;

    /// <summary>Modifiers currently held by the user (real keys only).</summary>
    protected Modifiers Held { get; private set; }

    public bool IsRunning => _thread is not null;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Echostep hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
        if (_startError is not null)
        {
            StopHooks();
            throw new InvalidOperationException(_startError);
        }
    }

    protected void StopHooks()
    {
        if (_thread is not { } t) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != t) t.Join(2000);
        _thread = null;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        var module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            _startError = "Failed to set the mouse and keyboard hook! Make sure security software is not blocking Echostep." +
                          $" (error {Marshal.GetLastWin32Error()})";
        else OnThreadStarted();
        _ready.Set();
        if (_startError is null)
        {
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            OnThreadStopping();
        }
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    protected virtual void OnThreadStarted() { }
    protected virtual void OnThreadStopping() { }

    /// <summary>A real (not injected) mouse event. Return true to swallow it.</summary>
    protected abstract bool OnMouse(int message, Native.MSLLHOOKSTRUCT data);

    /// <summary>A real (not injected) key event. Return true to swallow it.</summary>
    protected abstract bool OnKey(int vk, bool down);

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if ((data.flags & Native.LLMHF_INJECTED) == 0 && OnMouse((int)wParam, data)) return 1;
            }
            catch
            {
                // Never let an exception escape a hook callback.
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                var msg = (int)wParam;
                var down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if ((data.flags & Native.LLKHF_INJECTED) == 0 && (down || up))
                {
                    var vk = (int)data.vkCode;
                    var mod = KeyNames.ModifierOf(vk);
                    if (mod != Modifiers.None) Held = down ? Held | mod : Held & ~mod;
                    if (OnKey(vk, down)) return 1;
                }
            }
            catch
            {
                // Never let an exception escape a hook callback.
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public virtual void Dispose()
    {
        StopHooks();
        _ready.Dispose();
    }
}

/// <summary>
/// Records mouse, wheel, keyboard and foreground-window changes system-wide. Ignores Echostep's own
/// injected input, clicks/keys on Echostep's own windows, and its hotkeys. The stop key ends the
/// recording and is never recorded.
/// </summary>
internal sealed class HookRecorder : HookThread
{
    private readonly List<RecEvent> _events = [];
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly HashSet<(Modifiers, int)> _swallow;
    private readonly Hotkey _stopKey;
    private Native.WinEventProc? _focusProc;
    private IntPtr _focusHook;
    private bool _stopKeyDown;

    /// <summary>Raised (on the hook thread) when the stop key is pressed.</summary>
    public event Action? StopRequested;

    /// <param name="hotkeys">Echostep hotkeys; pressing them is not recorded.</param>
    /// <param name="stopKey">The key that ends the recording (swallowed).</param>
    public HookRecorder(IEnumerable<Hotkey> hotkeys, Hotkey stopKey)
    {
        _swallow = hotkeys.Where(h => !h.IsEmpty).Select(h => (h.Modifiers, h.Vk)).ToHashSet();
        _stopKey = stopKey;
    }

    public int EventCount { get { lock (_gate) return _events.Count; } }

    public bool IsRecording => IsRunning;

    public new void Start()
    {
        _clock.Restart();
        base.Start();
    }

    /// <summary>Removes the hooks and returns the captured events.</summary>
    public List<RecEvent> Stop()
    {
        StopHooks();
        lock (_gate) return [.. _events];
    }

    protected override void OnThreadStarted()
    {
        _focusProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            var title = Native.GetTitle(hwnd);
            if (title.Length == 0) return;
            Add(new RecEvent(RecEventKind.FocusChange, _clock.ElapsedMilliseconds, Program: Native.ProgramOf(hwnd), Title: title));
        };
        _focusHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _focusProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    protected override void OnThreadStopping()
    {
        if (_focusHook != IntPtr.Zero) Native.UnhookWinEvent(_focusHook);
        _focusHook = IntPtr.Zero;
    }

    private void Add(RecEvent e)
    {
        lock (_gate) _events.Add(e);
    }

    protected override bool OnMouse(int msg, Native.MSLLHOOKSTRUCT data)
    {
        if (Native.IsOwnWindow(Native.WindowFromPoint(data.pt))) return false;
        var t = _clock.ElapsedMilliseconds;
        var (x, y) = (data.pt.X, data.pt.Y);
        var hi = (short)(data.mouseData >> 16);
        var xButton = hi == 2 ? MouseButton.X2 : MouseButton.X1;
        switch (msg)
        {
            case Native.WM_MOUSEMOVE: Add(new RecEvent(RecEventKind.MouseMove, t, x, y)); break;
            case Native.WM_LBUTTONDOWN: Add(new RecEvent(RecEventKind.MouseDown, t, x, y, MouseButton.Left)); break;
            case Native.WM_RBUTTONDOWN: Add(new RecEvent(RecEventKind.MouseDown, t, x, y, MouseButton.Right)); break;
            case Native.WM_MBUTTONDOWN: Add(new RecEvent(RecEventKind.MouseDown, t, x, y, MouseButton.Middle)); break;
            case Native.WM_XBUTTONDOWN: Add(new RecEvent(RecEventKind.MouseDown, t, x, y, xButton)); break;
            case Native.WM_LBUTTONUP: Add(new RecEvent(RecEventKind.MouseUp, t, x, y, MouseButton.Left)); break;
            case Native.WM_RBUTTONUP: Add(new RecEvent(RecEventKind.MouseUp, t, x, y, MouseButton.Right)); break;
            case Native.WM_MBUTTONUP: Add(new RecEvent(RecEventKind.MouseUp, t, x, y, MouseButton.Middle)); break;
            case Native.WM_XBUTTONUP: Add(new RecEvent(RecEventKind.MouseUp, t, x, y, xButton)); break;
            case Native.WM_MOUSEWHEEL: Add(new RecEvent(RecEventKind.MouseWheel, t, x, y, Delta: hi)); break;
            case Native.WM_MOUSEHWHEEL: Add(new RecEvent(RecEventKind.MouseWheel, t, x, y, Delta: hi, Horizontal: true)); break;
        }
        return false;
    }

    protected override bool OnKey(int vk, bool down)
    {
        var t = _clock.ElapsedMilliseconds;
        var mod = KeyNames.ModifierOf(vk);
        if (mod != Modifiers.None)
        {
            Add(new RecEvent(down ? RecEventKind.KeyDown : RecEventKind.KeyUp, t, Vk: vk));
            return false;
        }

        // The stop key ends the recording; both halves are swallowed so the app below never sees it.
        if (!_stopKey.IsEmpty && vk == _stopKey.Vk && (down ? Held == _stopKey.Modifiers : _stopKeyDown))
        {
            if (down && !_stopKeyDown)
            {
                Add(new RecEvent(RecEventKind.Suppressed, t, Vk: vk));
                StopRequested?.Invoke();
            }
            _stopKeyDown = down;
            return true;
        }

        if (_swallow.Contains((Held, vk)) || Native.IsOwnWindow(Native.GetForegroundWindow()))
        {
            // Our hotkeys and keys typed into Echostep itself are not part of the macro.
            if (down) Add(new RecEvent(RecEventKind.Suppressed, t, Vk: vk));
        }
        else Add(new RecEvent(down ? RecEventKind.KeyDown : RecEventKind.KeyUp, t, Vk: vk));
        return false;
    }
}

/// <summary>
/// Watches real (not macro-sent) input during playback: the pause key toggles pause and is swallowed,
/// and other key presses or mouse movement can abort playback.
/// </summary>
internal sealed class PlaybackMonitor : HookThread
{
    private readonly Hotkey _pauseKey;
    private readonly bool _abortOnKey, _abortOnMove;
    private bool _pauseKeyDown, _aborted;
    private Native.POINT? _firstPoint;

    /// <summary>Raised on the hook thread.</summary>
    public event Action? PauseToggled;

    /// <summary>Raised once, on the hook thread, when user input should abort playback.</summary>
    public event Action? UserInput;

    public PlaybackMonitor(Hotkey pauseKey, bool abortOnKey, bool abortOnMove)
    {
        _pauseKey = pauseKey;
        _abortOnKey = abortOnKey;
        _abortOnMove = abortOnMove;
    }

    /// <summary>
    /// While this returns true (the color picker is open), keys and mouse moves are left alone: Esc closes the
    /// picker instead of pausing, and moving the mouse to pick a pixel does not abort playback.
    /// </summary>
    public Func<bool>? Suspended { get; set; }

    /// <summary>True if there is anything to watch for.</summary>
    public bool Needed => !_pauseKey.IsEmpty || _abortOnKey || _abortOnMove;

    public void Stop() => StopHooks();

    protected override bool OnMouse(int msg, Native.MSLLHOOKSTRUCT data)
    {
        if (!_abortOnMove || _aborted || msg != Native.WM_MOUSEMOVE) return false;
        if (Suspended?.Invoke() == true)
        {
            _firstPoint = null; // measure again from wherever the mouse is when the picker closes
            return false;
        }
        _firstPoint ??= data.pt;
        // A few pixels of jitter (a bumped desk) should not abort a long run.
        if (Math.Abs(data.pt.X - _firstPoint.Value.X) + Math.Abs(data.pt.Y - _firstPoint.Value.Y) < 6) return false;
        _aborted = true;
        UserInput?.Invoke();
        return false;
    }

    protected override bool OnKey(int vk, bool down)
    {
        if (KeyNames.IsModifier(vk)) return false;
        if (Suspended?.Invoke() == true)
        {
            _pauseKeyDown = false;
            return false;
        }
        if (!_pauseKey.IsEmpty && vk == _pauseKey.Vk && (down ? Held == _pauseKey.Modifiers : _pauseKeyDown))
        {
            if (down && !_pauseKeyDown) PauseToggled?.Invoke();
            _pauseKeyDown = down;
            return true; // consumed: never reaches the app and never counts as user input
        }
        if (_abortOnKey && down && !_aborted && !Native.IsOwnWindow(Native.GetForegroundWindow()))
        {
            _aborted = true;
            UserInput?.Invoke();
        }
        return false;
    }
}
