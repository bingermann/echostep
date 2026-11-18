using Echostep.Core;

namespace Echostep.Win;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window. Callbacks run on the UI thread,
/// and fire even when Echostep is hidden or another app has focus.
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "Echostep hotkeys" });
    }

    /// <summary>Registers a hotkey; returns its id, or 0 if Windows refused (usually: another app owns it).</summary>
    public int Register(Hotkey hotkey, Action callback, bool allowRepeat = false)
    {
        if (hotkey.IsEmpty) return 0;
        var id = _nextId++;
        var mods = Native.ToHotkeyModifiers(hotkey.Modifiers) | (allowRepeat ? 0 : Native.MOD_NOREPEAT);
        if (!Native.RegisterHotKey(Handle, id, mods, (uint)hotkey.Vk)) return 0;
        _callbacks[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (id == 0 || !_callbacks.Remove(id)) return;
        Native.UnregisterHotKey(Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToList()) Native.UnregisterHotKey(Handle, id);
        _callbacks.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _callbacks.TryGetValue((int)m.WParam, out var cb))
        {
            cb();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
