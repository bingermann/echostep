using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

public sealed record PickResult(int X, int Y, Rgb Color, IntPtr Window, string WindowTitle);

/// <summary>
/// Live color picker: a small click-through magnifier that follows the cursor and shows its position,
/// the color under it and the window under it. Enter saves the position, Escape cancels. It only reads the screen.
/// </summary>
internal sealed class PickerForm : Form
{
    private const int HotkeyPick = 1, HotkeyCancel = 2;
    private static PickerForm? _current;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly string _hint;
    private readonly int _pixels;
    private readonly Action<PickResult> _onPick;
    private readonly Action? _onCancel;
    private Bitmap _capture;
    private int _x = int.MinValue, _y = int.MinValue;
    private Rgb? _color;
    private IntPtr _window;
    private string _windowTitle = "";
    private bool _hotkeysRegistered;
    private bool _done;
    private float _k = 1;
    private readonly long _openedAt = Environment.TickCount64;

    public static bool IsOpen => _current is not null;

    /// <summary>Opens the picker. Only one picker is open at a time.</summary>
    public static void Open(string hint, int magnifierPixels, Action<PickResult> onPick, Action? onCancel = null)
    {
        _current?.Finish(null);
        var f = new PickerForm(hint, magnifierPixels, onPick, onCancel);
        _current = f;
        f.Show();
    }

    private PickerForm(string hint, int magnifierPixels, Action<PickResult> onPick, Action? onCancel)
    {
        _hint = hint;
        _pixels = Math.Clamp(magnifierPixels | 1, 7, 41);
        _onPick = onPick;
        _onCancel = onCancel;
        _capture = new Bitmap(_pixels, _pixels, PixelFormat.Format32bppArgb);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        Opacity = 0.97; // makes the window layered, which WS_EX_TRANSPARENT needs for click-through
        Text = "Echostep picker";
        _timer.Tick += (_, _) => UpdatePosition();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _k = this.DeviceDpi / 96f;
        var cell = (int)Math.Round(10 * _k);
        var mag = _pixels * cell;
        ClientSize = new Size(mag + (int)(16 * _k), mag + (int)(92 * _k));
        // Keep the magnifier out of its own screen captures (Windows 10 2004+); positioning keeps it clear otherwise.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        _hotkeysRegistered = Native.RegisterHotKey(Handle, HotkeyPick, Native.MOD_NOREPEAT, 0x0D /* Enter */)
                             & Native.RegisterHotKey(Handle, HotkeyCancel, Native.MOD_NOREPEAT, 0x1B /* Escape */);
        UpdatePosition();
        _timer.Start();
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private void UpdatePosition()
    {
        if (_done) return;
        if (!_hotkeysRegistered)
        {
            // Fallback if another program owns plain Enter/Escape as hotkeys. The short delay ignores
            // an Enter that is still held from opening the picker with the keyboard.
            if (Environment.TickCount64 - _openedAt > 400 && (GetAsyncKeyState(0x0D) & 0x8000) != 0) { Accept(); return; }
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { Finish(null); return; }
        }

        var (x, y) = Native.CursorPosition();
        if (x != _x || y != _y)
        {
            _x = x;
            _y = y;
            var (h, title) = Native.TopLevelWindowAt(x, y);
            _window = h;
            _windowTitle = Native.IsOwnWindow(h) ? "" : title;

            var screen = Screen.FromPoint(new Point(x, y)).Bounds;
            var off = (int)(24 * _k);
            var left = x + off;
            var top = y + off;
            if (left + Width > screen.Right) left = x - off - Width;
            if (top + Height > screen.Bottom) top = y - off - Height;
            Location = new Point(Math.Max(screen.Left, left), Math.Max(screen.Top, top));
        }

        _color = Native.ReadPixel(x, y);
        try
        {
            using var g = Graphics.FromImage(_capture);
            g.Clear(Color.Black);
            var half = _pixels / 2;
            g.CopyFromScreen(x - half, y - half, 0, 0, new Size(_pixels, _pixels), CopyPixelOperation.SourceCopy);
        }
        catch (Exception)
        {
            // Secure desktop or locked session: show black.
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pad = (int)(8 * _k);
        var mag = ClientSize.Width - 2 * pad;
        var cell = mag / (float)_pixels;
        var rect = new Rectangle(pad, pad, mag, mag);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_capture, rect);
        g.PixelOffsetMode = PixelOffsetMode.Default;

        using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
        {
            for (var i = 1; i < _pixels; i++)
            {
                var p = pad + i * cell;
                g.DrawLine(grid, p, pad, p, pad + mag);
                g.DrawLine(grid, pad, p, pad + mag, p);
            }
        }
        var c = _pixels / 2;
        var centre = new RectangleF(pad + c * cell, pad + c * cell, cell, cell);
        using (var outer = new Pen(Color.Black, 2 * _k)) g.DrawRectangle(outer, centre.X - _k, centre.Y - _k, centre.Width + 2 * _k, centre.Height + 2 * _k);
        using (var inner = new Pen(Color.White, _k)) g.DrawRectangle(inner, centre.X, centre.Y, centre.Width, centre.Height);
        g.DrawRectangle(Pens.Gray, rect);

        var y = pad + mag + 6 * _k;
        var sw = 18 * _k;
        using (var b = new SolidBrush(_color is { } col ? PixelUi.ToColor(col) : Color.Black))
            g.FillRectangle(b, pad, y + 2 * _k, sw, sw);
        g.DrawRectangle(Pens.Gray, pad, y + 2 * _k, sw, sw);

        using var big = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 8f);
        var colText = _color?.ToHex() ?? "unreadable";
        g.DrawString($"{_x}, {_y}   {colText}", big, Brushes.White, pad + sw + 6 * _k, y);
        y += 24 * _k;
        var title = _windowTitle.Length > 40 ? _windowTitle[..39] + "…" : _windowTitle;
        g.DrawString(title.Length > 0 ? title : "(desktop)", small, Brushes.Gainsboro, pad, y);
        y += 18 * _k;
        g.DrawString(_hint, small, Brushes.Khaki, new RectangleF(pad, y, mag, 36 * _k));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            if ((int)m.WParam == HotkeyPick) Accept();
            else if ((int)m.WParam == HotkeyCancel) Finish(null);
            return;
        }
        base.WndProc(ref m);
    }

    private void Accept()
    {
        var (x, y) = Native.CursorPosition();
        var color = Native.ReadPixel(x, y);
        if (color is null)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        var (h, title) = Native.TopLevelWindowAt(x, y);
        Finish(new PickResult(x, y, color.Value, h, Native.IsOwnWindow(h) ? "" : title));
    }

    private void Finish(PickResult? result)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        if (_current == this) _current = null;
        if (IsHandleCreated)
        {
            Native.UnregisterHotKey(Handle, HotkeyPick);
            Native.UnregisterHotKey(Handle, HotkeyCancel);
        }
        Close();
        // Let the window disappear before callers read the screen or open dialogs.
        BeginInvokeAfterClose(() =>
        {
            if (result is not null) _onPick(result);
            else _onCancel?.Invoke();
        });
    }

    private static void BeginInvokeAfterClose(Action a)
    {
        var t = new System.Windows.Forms.Timer { Interval = 1 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            a();
        };
        t.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _capture.Dispose();
        }
        base.Dispose(disposing);
    }
}
