using System.ComponentModel;
using System.Drawing.Drawing2D;
using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>A two-column caption/control grid for dialogs.</summary>
internal sealed class FieldGrid : TableLayoutPanel
{
    public FieldGrid()
    {
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, 0, 6);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public T Add<T>(string caption, T control) where T : Control
    {
        var row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (caption.Length > 0)
            Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 14, 5), ForeColor = Theme.Muted }, 0, row);
        control.Margin = new Padding(3, 4, 3, 5);
        if (control.Anchor == (AnchorStyles.Top | AnchorStyles.Left)) control.Anchor = AnchorStyles.Left;
        Controls.Add(control, 1, row);
        return control;
    }

    /// <summary>A wrapping explanation spanning both columns.</summary>
    public Label Note(string text, int width = 440)
    {
        var row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Theme.Muted, Margin = new Padding(3, 0, 3, 10) };
        Controls.Add(l, 0, row);
        SetColumnSpan(l, 2);
        return l;
    }

    public DurationField Duration(string caption, long ms, long minMs = 0, long maxMs = int.MaxValue) =>
        Add(caption, new DurationField(ms, minMs, maxMs));

    public IntBox Int(string caption, int value, int min, int max, int width = 90) =>
        Add(caption, NewInt(value, min, max, width));

    public static IntBox NewInt(int value, int min, int max, int width = 90) => new(value, min, max, width);

    /// <summary>A section heading spanning both columns.</summary>
    public Label Header(string text)
    {
        var row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = text, AutoSize = true, Font = Theme.BoldFont, ForeColor = Theme.Accent, Margin = new Padding(3, row == 0 ? 0 : 16, 3, 6) };
        Controls.Add(l, 0, row);
        SetColumnSpan(l, 2);
        return l;
    }

    public TextBox TextField(string caption, string? value, int width = 240) =>
        Add(caption, new TextBox { Text = value ?? "", Width = width });

    public ComboBox Combo(string caption, IEnumerable<string> items, string? value, bool editable, int width = 180)
    {
        var c = new ThemedCombo
        {
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            Width = width,
        };
        foreach (var i in items) c.Items.Add(i);
        if (editable) c.Text = value ?? "";
        else c.SelectedItem = value;
        if (!editable && c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        return Add(caption, c);
    }

    public FlowLayoutPanel Row(string caption, params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in controls)
        {
            if (c is Label or CheckBox) c.Margin = new Padding(3, 8, 6, 3);
            else if (c is RoundButton) c.Margin = new Padding(4, 0, 4, 0);
            else if (c is TextBox or ComboBox) c.Margin = new Padding(0, 4, 6, 3);
            p.Controls.Add(c);
        }
        return Add(caption, p);
    }
}

/// <summary>
/// A time amount: a number box and a unit (milliseconds, seconds, minutes, hours). Decimals are allowed
/// ("1.5 minutes"). It opens in the largest unit that shows the value as a whole number.
/// </summary>
internal sealed class DurationField : FlowLayoutPanel
{
    private readonly TextBox _amount = new() { Width = 90, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 128 };
    private readonly long _minMs, _maxMs;

    public DurationField(long ms, long minMs = 0, long maxMs = int.MaxValue, WaitUnit? unit = null)
    {
        _minMs = minMs;
        _maxMs = maxMs;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = Padding.Empty;
        _unit.Items.AddRange(Durations.UnitNames);
        _amount.Margin = new Padding(0, 4, 4, 3);
        _unit.Margin = new Padding(0, 4, 3, 3);
        Controls.Add(_amount);
        Controls.Add(_unit);
        SetMs(ms, unit);
        _amount.TextChanged += (_, _) => _amount.BackColor = Ms is null ? Theme.ErrorBack : Theme.SurfaceAlt;
    }

    /// <summary>The time in milliseconds, or null if the text is not a number in range.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public long? Ms
    {
        get
        {
            var ms = Durations.ToMs(_amount.Text, Unit);
            return ms is { } v && v >= _minMs && v <= _maxMs ? v : null;
        }
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public WaitUnit Unit => (WaitUnit)Math.Max(0, _unit.SelectedIndex);

    public void SetMs(long ms, WaitUnit? unit = null)
    {
        var u = unit ?? Durations.BestUnit(ms);
        _unit.SelectedIndex = (int)u;
        _amount.Text = WaitAction.FormatAmount(ms / (double)WaitAction.MsPer(u));
    }

    /// <summary>"Enter … between X and Y" when invalid, else null.</summary>
    public string? Problem(string what) => Ms is not null ? null
        : $"Enter {what} as a number that is {Durations.Format(_minMs)} or more" +
          (_maxMs < int.MaxValue ? $" and at most {Durations.Format(_maxMs)}." : " (at most about 24 days).");

    public void SetTip(ToolTip tips, string text)
    {
        tips.SetToolTip(_amount, text);
        tips.SetToolTip(_unit, text);
    }
}

/// <summary>Hex color text box with a clickable swatch.</summary>
internal sealed class ColorField : FlowLayoutPanel
{
    private readonly TextBox _text = new() { Width = 90 };
    private readonly Panel _swatch = new() { Width = 44, Height = 25, Margin = new Padding(0, 4, 3, 3), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };

    public ColorField(string value)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = Padding.Empty;
        _text.Text = value;
        _text.TextChanged += (_, _) => UpdateSwatch();
        _swatch.Click += (_, _) => ChooseColor();
        Controls.Add(_text);
        Controls.Add(_swatch);
        new ToolTip().SetToolTip(_swatch, "Choose a color");
        UpdateSwatch();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => Rgb.TryParse(_text.Text, out var c) ? c.ToHex() : _text.Text.Trim();
        set => _text.Text = value;
    }

    public bool IsValid => Rgb.TryParse(_text.Text, out _);

    private void UpdateSwatch()
    {
        if (Rgb.TryParse(_text.Text, out var c))
        {
            _swatch.BackColor = Color.FromArgb(c.ToArgb());
            _text.BackColor = Theme.SurfaceAlt;
        }
        else
        {
            _swatch.BackColor = Theme.Surface;
            _text.BackColor = Theme.ErrorBack;
        }
    }

    private void ChooseColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
        if (Rgb.TryParse(_text.Text, out var c)) dlg.Color = Color.FromArgb(c.ToArgb());
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
            _text.Text = new Rgb(dlg.Color.R, dlg.Color.G, dlg.Color.B).ToHex();
    }
}

/// <summary>A box that captures the next key combination pressed in it (including Enter, Tab and Escape).</summary>
internal sealed class KeyCaptureBox : TextBox
{
    public event Action<int, Modifiers>? Captured;

    public KeyCaptureBox()
    {
        ReadOnly = true;
        Text = "Click here, then press a key";
        Width = 240;
        Cursor = Cursors.Hand;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (msg.Msg is not (WM_KEYDOWN or WM_SYSKEYDOWN)) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        var mods = ((keyData & Keys.Control) != 0 ? Modifiers.Ctrl : 0)
                   | ((keyData & Keys.Alt) != 0 ? Modifiers.Alt : 0)
                   | ((keyData & Keys.Shift) != 0 ? Modifiers.Shift : 0);
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            Text = mods.ToPrefix() + "…";
            return true;
        }
        Text = mods.ToPrefix() + KeyNames.GetName((int)key);
        Captured?.Invoke((int)key, mods);
        return true;
    }
}

/// <summary>Toolbar icons drawn at runtime, so the EXE needs no image resources.</summary>
internal static class Icons
{
    public static Bitmap Make(int size, Action<Graphics, float> draw)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        draw(g, size / 16f);
        return bmp;
    }

    public static Bitmap Record(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Theme.Danger);
        g.FillEllipse(b, 2 * k, 2 * k, 12 * k, 12 * k);
    });

    public static Bitmap Stop(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Theme.Text);
        g.FillRectangle(b, 3 * k, 3 * k, 10 * k, 10 * k);
    });

    public static Bitmap Play(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Theme.Success);
        g.FillPolygon(b, [new PointF(4 * k, 2 * k), new PointF(14 * k, 8 * k), new PointF(4 * k, 14 * k)]);
    });

    public static Bitmap Watch(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Accent, 1.5f * k);
        g.DrawEllipse(p, 1.5f * k, 4 * k, 13 * k, 8 * k);
        using var b = new SolidBrush(Theme.Accent);
        g.FillEllipse(b, 5.5f * k, 5.5f * k, 5 * k, 5 * k);
    });

    public static Bitmap Mouse(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.3f * k);
        using var body = Theme.RoundRect(new RectangleF(4 * k, 1.5f * k, 8 * k, 13 * k), 4 * k);
        g.DrawPath(p, body);
        g.DrawLine(p, 8 * k, 1.5f * k, 8 * k, 7 * k);
        g.DrawLine(p, 4 * k, 7 * k, 12 * k, 7 * k);
        using var b = new SolidBrush(Theme.Accent);
        g.FillRectangle(b, 4.8f * k, 2.6f * k, 2.6f * k, 3.8f * k);
    });

    public static Bitmap Key(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.3f * k);
        using var cap = Theme.RoundRect(new RectangleF(2 * k, 2.5f * k, 12 * k, 11 * k), 2.5f * k);
        g.DrawPath(p, cap);
        using var a = new Pen(Theme.Accent, 1.5f * k);
        g.DrawLines(a, [new PointF(5.5f * k, 11 * k), new PointF(8 * k, 5 * k), new PointF(10.5f * k, 11 * k)]);
        g.DrawLine(a, 6.6f * k, 9 * k, 9.4f * k, 9 * k);
    });

    public static Bitmap Clock(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.3f * k);
        g.DrawEllipse(p, 2 * k, 2 * k, 12 * k, 12 * k);
        using var a = new Pen(Theme.Accent, 1.5f * k);
        g.DrawLines(a, [new PointF(8 * k, 4.5f * k), new PointF(8 * k, 8 * k), new PointF(11 * k, 9.5f * k)]);
    });

    public static Bitmap Flow(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.3f * k);
        g.DrawLines(p, [new PointF(3 * k, 4 * k), new PointF(12 * k, 4 * k), new PointF(12 * k, 8 * k), new PointF(4 * k, 8 * k), new PointF(4 * k, 12 * k), new PointF(11 * k, 12 * k)]);
        using var b = new SolidBrush(Theme.Accent);
        g.FillPolygon(b, [new PointF(10.5f * k, 9.5f * k), new PointF(14 * k, 12 * k), new PointF(10.5f * k, 14.5f * k)]);
    });

    public static Bitmap Lines(int s, bool numbered) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.3f * k);
        using var a = new SolidBrush(Theme.Accent);
        for (var i = 0; i < 4; i++)
        {
            var y = (3 + i * 3.3f) * k;
            if (numbered) g.FillRectangle(a, 2 * k, y - 0.8f * k, 2 * k, 1.6f * k);
            g.DrawLine(p, (numbered ? 6 : 2) * k, y, 14 * k, y);
        }
    });

    public static Bitmap Gear(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.6f * k);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            g.DrawLine(p, (float)(8 + 4.5 * Math.Cos(angle)) * k, (float)(8 + 4.5 * Math.Sin(angle)) * k,
                (float)(8 + 6.5 * Math.Cos(angle)) * k, (float)(8 + 6.5 * Math.Sin(angle)) * k);
        }
        g.DrawEllipse(p, 3.8f * k, 3.8f * k, 8.4f * k, 8.4f * k);
        using var a = new SolidBrush(Theme.Accent);
        g.FillEllipse(a, 6.3f * k, 6.3f * k, 3.4f * k, 3.4f * k);
    });

    public static Bitmap StepIcon(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Theme.Success);
        g.FillPolygon(b, [new PointF(3 * k, 3 * k), new PointF(10 * k, 8 * k), new PointF(3 * k, 13 * k)]);
        using var bar = new SolidBrush(Theme.Text);
        g.FillRectangle(bar, 11 * k, 3 * k, 2.2f * k, 10 * k);
    });

    /// <summary>Two arrows chasing round a loop: Repeat.</summary>
    public static Bitmap Repeat(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.7f * k);
        g.DrawArc(p, 2.5f * k, 3 * k, 11 * k, 10 * k, 200, 140);
        g.DrawArc(p, 2.5f * k, 3 * k, 11 * k, 10 * k, 20, 140);
        using var a = new SolidBrush(Theme.Accent);
        g.FillPolygon(a, [new PointF(12.2f * k, 1.6f * k), new PointF(15.2f * k, 5.6f * k), new PointF(10.6f * k, 6.4f * k)]);
        g.FillPolygon(a, [new PointF(3.8f * k, 14.4f * k), new PointF(0.8f * k, 10.4f * k), new PointF(5.4f * k, 9.6f * k)]);
    });

    /// <summary>A gauge with its needle: playback speed.</summary>
    public static Bitmap Speed(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.7f * k);
        g.DrawArc(p, 1.5f * k, 3 * k, 13 * k, 13 * k, 180, 180);
        g.DrawLine(p, 1.5f * k, 9.5f * k, 14.5f * k, 9.5f * k);
        using var n = new Pen(Theme.Accent, 1.9f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(n, 8 * k, 9.5f * k, 12 * k, 5 * k);
        using var hub = new SolidBrush(Theme.Accent);
        g.FillEllipse(hub, 6.6f * k, 8.1f * k, 2.8f * k, 2.8f * k);
    });

    /// <summary>A curved path ending in a cursor dot: how the mouse moves.</summary>
    public static Bitmap MousePath(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.6f * k) { DashStyle = DashStyle.Dash };
        g.DrawBezier(p, 2 * k, 13 * k, 4 * k, 3 * k, 10 * k, 14 * k, 13 * k, 4 * k);
        using var a = new SolidBrush(Theme.Accent);
        g.FillEllipse(a, 11 * k, 2 * k, 4 * k, 4 * k);
        g.FillEllipse(a, 0.6f * k, 11.6f * k, 3 * k, 3 * k);
    });

    /// <summary>A power symbol: what happens when playback is done.</summary>
    public static Bitmap Power(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.8f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(p, 2.5f * k, 3 * k, 11 * k, 11 * k, 300, 300);
        using var a = new Pen(Theme.Accent, 1.9f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(a, 8 * k, 1.5f * k, 8 * k, 8 * k);
    });

    /// <summary>Funnel: the playback filter.</summary>
    public static Bitmap Filter(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Theme.Text);
        g.FillPolygon(b, [new PointF(2 * k, 3 * k), new PointF(14 * k, 3 * k), new PointF(9.2f * k, 8.6f * k), new PointF(9.2f * k, 13.5f * k),
            new PointF(6.8f * k, 12.2f * k), new PointF(6.8f * k, 8.6f * k)]);
        using var a = new SolidBrush(Theme.Accent);
        g.FillRectangle(a, 2 * k, 3 * k, 12 * k, 1.6f * k);
    });

    /// <summary>Four-point sparkle: the More button.</summary>
    public static Bitmap Spark(int s) => Make(s, (g, k) =>
    {
        using var a = new SolidBrush(Theme.Accent);
        g.FillPolygon(a, [new PointF(7 * k, 1 * k), new PointF(8.6f * k, 5.6f * k), new PointF(13 * k, 7 * k), new PointF(8.6f * k, 8.4f * k),
            new PointF(7 * k, 13 * k), new PointF(5.4f * k, 8.4f * k), new PointF(1 * k, 7 * k), new PointF(5.4f * k, 5.6f * k)]);
        using var t = new SolidBrush(Theme.Text);
        g.FillPolygon(t, [new PointF(12.5f * k, 9.5f * k), new PointF(13.3f * k, 11.7f * k), new PointF(15.5f * k, 12.5f * k), new PointF(13.3f * k, 13.3f * k),
            new PointF(12.5f * k, 15.5f * k), new PointF(11.7f * k, 13.3f * k), new PointF(9.5f * k, 12.5f * k), new PointF(11.7f * k, 11.7f * k)]);
    });

    public static Bitmap Picker(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Theme.Text, 1.5f * k);
        g.DrawEllipse(p, 3 * k, 3 * k, 10 * k, 10 * k);
        g.DrawLine(p, 8 * k, 0, 8 * k, 5 * k);
        g.DrawLine(p, 8 * k, 11 * k, 8 * k, 16 * k);
        g.DrawLine(p, 0, 8 * k, 5 * k, 8 * k);
        g.DrawLine(p, 11 * k, 8 * k, 16 * k, 8 * k);
    });
}

/// <summary>Shared Pick and Test logic for dialogs that edit a pixel position.</summary>
internal static class PixelUi
{
    /// <summary>
    /// Hides Echostep's windows, runs the picker, restores them, and returns screen or window-relative coordinates.
    /// </summary>
    /// <param name="relativeToWindowUnderCursor">Use the window under the cursor and store client-relative coordinates.</param>
    /// <param name="namedWindow">Otherwise, a window title to be relative to (may be empty).</param>
    /// <summary>Makes Echostep's windows invisible; the returned action brings them back.</summary>
    public static Action HideWindows(Form dialog)
    {
        var hidden = new List<(Form Form, double Opacity)>();
        foreach (Form f in Application.OpenForms)
        {
            if (f is PickerForm or CrosshairOverlay || !f.Visible) continue;
            hidden.Add((f, f.Opacity));
            f.Opacity = 0;
        }
        return () =>
        {
            foreach (var (f, o) in hidden)
                if (!f.IsDisposed) f.Opacity = o;
            if (!dialog.IsDisposed) dialog.Activate();
        };
    }

    /// <summary>Picks the window under the cursor (Enter saves, Esc cancels).</summary>
    public static void PickWindow(Form dialog, Action<IntPtr, string> done)
    {
        var restore = HideWindows(dialog);
        PickerForm.Open("Enter: use this window   Esc: cancel", Program.Settings.MagnifierPixels, result =>
        {
            restore();
            if (result.Window != IntPtr.Zero && result.WindowTitle.Length > 0) done(result.Window, result.WindowTitle);
        }, restore);
    }

    public static void Pick(Form dialog, bool relativeToWindowUnderCursor, string? namedWindow,
        Action<int, int, Rgb, string?> done)
    {
        var Restore = HideWindows(dialog);

        var hint = relativeToWindowUnderCursor
            ? "Enter: save (relative to this window)   Esc: cancel"
            : "Enter: save this position   Esc: cancel";
        PickerForm.Open(hint, Program.Settings.MagnifierPixels, result =>
        {
            Restore();
            if (relativeToWindowUnderCursor && result.WindowTitle.Length > 0 && Native.ClientOrigin(result.Window) is { } o)
            {
                done(result.X - o.X, result.Y - o.Y, result.Color, result.WindowTitle);
                return;
            }
            if (!relativeToWindowUnderCursor && !string.IsNullOrWhiteSpace(namedWindow))
            {
                if (Native.ClientOrigin(Native.FindWindowByTitle(namedWindow)) is { } w)
                {
                    done(result.X - w.X, result.Y - w.Y, result.Color, namedWindow);
                    return;
                }
                MessageBox.Show(dialog,
                    $"No visible window titled \"{namedWindow}\" was found, so the screen position was used and the window was cleared.",
                    "Pick", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            done(result.X, result.Y, result.Color, null);
        }, Restore);
    }

    /// <summary>Reads the pixel now and describes how it compares with the target.</summary>
    public static string Test(int x, int y, string? window, string? targetHex, int tolerance, MatchMode mode)
    {
        int sx = x, sy = y;
        if (!string.IsNullOrWhiteSpace(window))
        {
            if (Native.ClientOrigin(Native.FindWindowByTitle(window)) is not { } o)
                return $"Window \"{window}\" not found (no match).";
            sx += o.X;
            sy += o.Y;
        }
        if (Native.ReadPixel(sx, sy) is not { } c) return $"Pixel {sx}, {sy} cannot be read (no match).";
        if (targetHex is null || !Rgb.TryParse(targetHex, out var t)) return $"Now {c.ToHex()} at screen {sx}, {sy}.";
        var diff = mode == MatchMode.Distance ? $"distance {Rgb.Distance(c, t):0.#}" : $"max channel difference {Rgb.MaxChannelDiff(c, t)}";
        return Rgb.Matches(c, t, tolerance, mode)
            ? $"Now {c.ToHex()}: MATCHES ({diff})."
            : $"Now {c.ToHex()}: no match ({diff}, tolerance {tolerance}).";
    }

    public static Color ToColor(Rgb c) => Color.FromArgb(c.ToArgb());
}


/// <summary>
/// A hotkey setting: click the box and press the key or combination; Clear removes it. Shows "Not set" when empty,
/// so the field never looks blank.
/// </summary>
internal sealed class HotkeyField : FlowLayoutPanel
{
    private const string NotSet = "Not set";
    private readonly KeyCaptureBox _box = new() { Width = 200 };
    private string _value = "";

    public HotkeyField()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = Padding.Empty;
        _box.Margin = new Padding(0, 4, 6, 3);
        _box.MinimumSize = new Size(_box.Width, 0);
        _box.Text = NotSet;
        _box.Captured += (vk, mods) => _value = _box.Text = new Hotkey(mods, vk).ToString();
        _box.Enter += (_, _) => _box.Text = "Press a key…";
        _box.Leave += (_, _) => Show();
        var clear = new RoundButton { Text = "Clear", AutoSize = true, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 2, 0, 2) };
        clear.Click += (_, _) => Value = "";
        Controls.Add(_box);
        Controls.Add(clear);
    }

    /// <summary>The hotkey as text ("Ctrl+Alt+P"), or "" when not set.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _value;
        set
        {
            _value = value?.Trim() ?? "";
            Show();
        }
    }

    private new void Show() => _box.Text = _value.Length == 0 ? NotSet : _value;

    public new bool Focus() => _box.Focus();
}
