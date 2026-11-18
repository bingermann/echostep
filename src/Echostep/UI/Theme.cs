using System.ComponentModel;
using System.Drawing.Drawing2D;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>The app's look: a dark or light palette with an accent color, fonts, and the code that applies them.</summary>
internal static class Theme
{
    /// <summary>Accent choices: name and the bright version used on dark backgrounds.</summary>
    public static readonly (string Name, Color Color)[] Accents =
    [
        ("Blue", Color.FromArgb(0x5B, 0x9D, 0xFF)), ("Teal", Color.FromArgb(0x2E, 0xC4, 0xB6)),
        ("Green", Color.FromArgb(0x46, 0xC2, 0x77)), ("Purple", Color.FromArgb(0xA4, 0x7B, 0xFF)),
        ("Orange", Color.FromArgb(0xFF, 0x9F, 0x43)), ("Pink", Color.FromArgb(0xFF, 0x6F, 0xB5)),
        ("Red", Color.FromArgb(0xFF, 0x5F, 0x5F)),
    ];

    public static readonly string[] ThemeNames = ["Dark", "Light", "Match Windows"];
    public static readonly string[] TextSizes = ["Normal", "Large"];

    /// <summary>True when the dark palette is in use.</summary>
    public static bool IsDark { get; private set; } = true;

    public static Color Bg { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceAlt { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Disabled { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    /// <summary>Selected rows and tabs.</summary>
    public static Color AccentDim { get; private set; }
    /// <summary>Text on an Accent fill (primary buttons).</summary>
    public static Color OnAccent { get; private set; }
    /// <summary>Text on an AccentDim fill (selected rows and tabs).</summary>
    public static Color SelectionText { get; private set; }
    public static Color Danger { get; private set; }
    public static Color Success { get; private set; }
    public static Color ErrorBack { get; private set; }
    public static Color ErrorText { get; private set; }
    public static Color PlayingBack { get; private set; }

    public static Font UiFont { get; private set; } = new("Segoe UI", 10f);
    public static Font BoldFont { get; private set; } = new("Segoe UI", 10f, FontStyle.Bold);

    static Theme() => Init(0, "Blue", 0);

    /// <summary>
    /// Chooses the palette. Call once at startup, before any window is created (windows read the colors as
    /// they are built, so a change takes effect after a restart).
    /// </summary>
    /// <param name="theme">0 dark, 1 light, 2 match Windows' app mode.</param>
    public static void Init(int theme, string? accent, int textSize)
    {
        IsDark = theme switch { 1 => false, 2 => !WindowsUsesLightApps(), _ => true };
        var baseAccent = Accents.FirstOrDefault(a => string.Equals(a.Name, accent, StringComparison.OrdinalIgnoreCase)).Color;
        if (baseAccent.IsEmpty) baseAccent = Accents[0].Color;

        if (IsDark)
        {
            Bg = Color.FromArgb(0x1B, 0x1D, 0x23);
            Surface = Color.FromArgb(0x25, 0x28, 0x31);
            SurfaceAlt = Color.FromArgb(0x30, 0x34, 0x40);
            Hover = Color.FromArgb(0x3B, 0x40, 0x4E);
            Border = Color.FromArgb(0x46, 0x4B, 0x5A);
            Text = Color.FromArgb(0xE8, 0xEA, 0xF0);
            Muted = Color.FromArgb(0x9A, 0xA1, 0xB0);
            Disabled = Color.FromArgb(0x6A, 0x70, 0x7E);
            Danger = Color.FromArgb(0xFF, 0x6B, 0x6B);
            Success = Color.FromArgb(0x4F, 0xD0, 0x8A);
            ErrorBack = Color.FromArgb(0x4A, 0x24, 0x28);
            ErrorText = Color.FromArgb(0xFF, 0x9A, 0x9A);
            PlayingBack = Color.FromArgb(0x4A, 0x43, 0x1E);
            Accent = baseAccent;
            AccentHover = Blend(baseAccent, Color.White, 0.8);
            AccentDim = Blend(baseAccent, Surface, 0.38);
            OnAccent = Color.FromArgb(0x0E, 0x14, 0x20);
            SelectionText = Color.White;
        }
        else
        {
            Bg = Color.FromArgb(0xEE, 0xF0, 0xF4);
            Surface = Color.FromArgb(0xFF, 0xFF, 0xFF);
            SurfaceAlt = Color.FromArgb(0xE6, 0xE9, 0xEF);
            Hover = Color.FromArgb(0xD9, 0xDD, 0xE5);
            Border = Color.FromArgb(0xC3, 0xC8, 0xD2);
            Text = Color.FromArgb(0x1C, 0x1F, 0x26);
            Muted = Color.FromArgb(0x5E, 0x65, 0x73);
            Disabled = Color.FromArgb(0xA3, 0xA9, 0xB4);
            Danger = Color.FromArgb(0xC9, 0x34, 0x34);
            Success = Color.FromArgb(0x17, 0x8F, 0x4F);
            ErrorBack = Color.FromArgb(0xFB, 0xE1, 0xE1);
            ErrorText = Color.FromArgb(0xA8, 0x1E, 0x1E);
            PlayingBack = Color.FromArgb(0xFF, 0xF0, 0xBE);
            // The bright accents are too pale on white: darken them for text, buttons and borders.
            Accent = Blend(baseAccent, Color.Black, 0.64);
            AccentHover = Blend(baseAccent, Color.Black, 0.76);
            AccentDim = Blend(baseAccent, Surface, 0.24);
            OnAccent = Color.White;
            SelectionText = Text;
        }

        var size = textSize == 1 ? 11.5f : 10f;
        UiFont = new Font("Segoe UI", size);
        BoldFont = new Font("Segoe UI", size, FontStyle.Bold);
    }

    /// <summary>Mixes <paramref name="a"/> with <paramref name="b"/>: weight 1 is all a, 0 all b.</summary>
    public static Color Blend(Color a, Color b, double weight) => Color.FromArgb(
        (int)Math.Round(a.R * weight + b.R * (1 - weight)),
        (int)Math.Round(a.G * weight + b.G * (1 - weight)),
        (int)Math.Round(a.B * weight + b.B * (1 - weight)));

    /// <summary>
    /// A row or group color for the current palette. The stored colors are dark tints; the light palette
    /// shows the same hue as a pastel, so a macro looks right in either theme.
    /// </summary>
    public static Color RowTint(Color stored)
    {
        if (IsDark) return stored;
        var hue = stored.GetHue();
        var sat = Math.Min(1.0, stored.GetSaturation() * 1.4 + 0.15);
        return FromHsl(hue, stored.GetSaturation() < 0.05 ? 0 : sat, 0.88);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var hh = h / 360.0;
        return Color.FromArgb((int)Math.Round(Channel(hh + 1 / 3.0) * 255), (int)Math.Round(Channel(hh) * 255), (int)Math.Round(Channel(hh - 1 / 3.0) * 255));
    }

    /// <summary>Windows' "Choose your default app mode" (Settings → Personalization → Colors).</summary>
    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Call at the start of a form's constructor: colors, font and a dark title bar.</summary>
    /// <summary>The app icon (embedded app.ico), shown in every window's title bar, the taskbar and Alt+Tab.</summary>
    public static Icon? AppIcon { get; } = LoadIcon();

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("Echostep.app.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null; // cosmetic: fall back to the default icon
        }
    }

    public static void Prepare(Form form)
    {
        if (AppIcon is not null) form.Icon = AppIcon;
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = UiFont;
        if (form.IsHandleCreated) Native.UseDarkTitleBar(form.Handle, IsDark);
        form.HandleCreated += (_, _) => Native.UseDarkTitleBar(form.Handle, IsDark);
    }

    /// <summary>Call once all controls are added: styles every standard control below <paramref name="root"/>.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case RoundButton:
                    break;
                case TextBox t:
                    t.BackColor = t.Multiline ? Surface : SurfaceAlt;
                    t.ForeColor = Text;
                    t.BorderStyle = t.Multiline ? BorderStyle.None : BorderStyle.FixedSingle;
                    if (t.Multiline) DarkScrollbars(t);
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = SurfaceAlt;
                    cb.ForeColor = Text;
                    break;
                case DataGridView g:
                    StyleGrid(g);
                    break;
                case ScrollBar sb:
                    DarkScrollbars(sb);
                    break;
                case ToolStrip ts:
                    ts.BackColor = Bg;
                    ts.ForeColor = Text;
                    break;
            }
            Apply(c);
        }
    }

    public static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Surface;
        g.GridColor = Border;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        // Visible dividers between headers show where to drag to resize a column.
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 36;
        g.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        g.ColumnHeadersDefaultCellStyle.Font = BoldFont;
        g.DefaultCellStyle.BackColor = Surface;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = AccentDim;
        g.DefaultCellStyle.SelectionForeColor = SelectionText;
        g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        g.RowTemplate.Height = 32;
        DarkScrollbars(g);
    }

    private static void DarkScrollbars(Control c)
    {
        if (c.IsHandleCreated) Native.UseDarkScrollbars(c.Handle, IsDark);
        c.HandleCreated += (_, _) => Native.UseDarkScrollbars(c.Handle, IsDark);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal enum ButtonKind { Normal, Primary, Danger, Tab }

/// <summary>A large, rounded, owner-drawn button.</summary>
internal class RoundButton : Button
{
    private bool _hover, _down, _selected;
    private ButtonKind _kind;

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Padding = new Padding(14, 7, 14, 7);
        Margin = new Padding(3, 3, 3, 5);
        Cursor = Cursors.Hand;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    /// <summary>An icon drawn in the button's text color; with no text it is centered, otherwise it sits left of the text.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Glyphs.Draw? Glyph { get; set; }

    /// <summary>For tab buttons: the page it shows is the current one.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color? fill, border = null;
        Color text;
        if (!Enabled)
        {
            fill = _kind == ButtonKind.Tab ? null : Theme.Surface;
            text = Theme.Disabled;
            if (_kind != ButtonKind.Tab) border = Theme.SurfaceAlt;
        }
        else switch (_kind)
        {
            case ButtonKind.Primary:
                fill = _down ? Theme.Accent : _hover ? Theme.AccentHover : Theme.Accent;
                text = Theme.OnAccent;
                break;
            case ButtonKind.Tab:
                fill = _selected ? Theme.AccentDim : _hover ? Theme.SurfaceAlt : null;
                text = _selected ? Theme.SelectionText : Theme.Muted;
                break;
            case ButtonKind.Danger:
                fill = _down ? Theme.Border : _hover ? Theme.Hover : Theme.SurfaceAlt;
                text = Theme.Danger;
                border = Theme.Border;
                break;
            default:
                fill = _down ? Theme.Border : _hover ? Theme.Hover : Theme.SurfaceAlt;
                text = Theme.Text;
                border = Theme.Border;
                break;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = _kind == ButtonKind.Tab ? Height / 2f : 9f * this.DeviceDpi / 96f;
        using (var path = Theme.RoundRect(rect, radius))
        {
            if (fill is { } f)
            {
                using var b = new SolidBrush(f);
                g.FillPath(b, path);
            }
            if (Focused && ShowFocusCues) border = Theme.Accent;
            if (border is { } bc)
            {
                using var p = new Pen(bc);
                g.DrawPath(p, path);
            }
        }
        if (Glyph is { } glyph)
        {
            var size = 18f * this.DeviceDpi / 96f;
            var box = string.IsNullOrEmpty(Text)
                ? new RectangleF((Width - size) / 2, (Height - size) / 2, size, size)
                : new RectangleF(Padding.Left, (Height - size) / 2, size, size);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            glyph(g, box, text);
            if (string.IsNullOrEmpty(Text)) return;
            var textBox = Rectangle.FromLTRB((int)(box.Right + 6), 0, Width - Padding.Right, Height);
            TextRenderer.DrawText(g, Text, Font, textBox, text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>A rounded surface that groups content.</summary>
internal sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Padding = new Padding(14);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 14f * this.DeviceDpi / 96f);
        using var b = new SolidBrush(Theme.Surface);
        g.FillPath(b, path);
    }
}

/// <summary>A whole-number text box: type a value, or use Up/Down and the mouse wheel.</summary>
internal sealed class IntBox : TextBox
{
    private int _min, _max;

    public IntBox(int value, int min, int max, int width)
    {
        _min = min;
        _max = max;
        Width = width;
        TextAlign = HorizontalAlignment.Right;
        Text = Math.Clamp(value, min, max).ToString();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum { get => _min; set => _min = value; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => _max; set => _max = value; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => long.TryParse(Text.Trim(), out var v) ? (int)Math.Clamp(v, _min, _max) : Math.Clamp(0, _min, _max);
        set => Text = Math.Clamp(value, _min, _max).ToString();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        var ok = char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar) || (e.KeyChar == '-' && _min < 0 && SelectionStart == 0);
        if (!ok) e.Handled = true;
        base.OnKeyPress(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            Step(e.KeyCode == Keys.Up ? 1 : -1, e.Shift ? 10 : 1);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Focused) Step(Math.Sign(e.Delta), 1);
        base.OnMouseWheel(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        Text = Value.ToString();
        base.OnLeave(e);
    }

    private void Step(int direction, int size)
    {
        Value = (int)Math.Clamp((long)Value + direction * size, _min, _max);
        SelectionStart = TextLength;
    }
}

internal sealed class DarkColors : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => Theme.Bg;
    public override Color ToolStripGradientMiddle => Theme.Bg;
    public override Color ToolStripGradientEnd => Theme.Bg;
    public override Color ToolStripBorder => Theme.Bg;
    public override Color MenuStripGradientBegin => Theme.Bg;
    public override Color MenuStripGradientEnd => Theme.Bg;
    public override Color StatusStripGradientBegin => Theme.Bg;
    public override Color StatusStripGradientEnd => Theme.Bg;
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Hover;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.SurfaceAlt;
    public override Color MenuItemSelectedGradientEnd => Theme.SurfaceAlt;
    public override Color MenuItemPressedGradientBegin => Theme.Surface;
    public override Color MenuItemPressedGradientMiddle => Theme.Surface;
    public override Color MenuItemPressedGradientEnd => Theme.Surface;
    public override Color ButtonSelectedHighlight => Theme.SurfaceAlt;
    public override Color ButtonSelectedBorder => Theme.Hover;
    public override Color ButtonSelectedGradientBegin => Theme.SurfaceAlt;
    public override Color ButtonSelectedGradientMiddle => Theme.SurfaceAlt;
    public override Color ButtonSelectedGradientEnd => Theme.SurfaceAlt;
    public override Color ButtonPressedGradientBegin => Theme.Hover;
    public override Color ButtonPressedGradientMiddle => Theme.Hover;
    public override Color ButtonPressedGradientEnd => Theme.Hover;
    public override Color ButtonCheckedGradientBegin => Theme.AccentDim;
    public override Color ButtonCheckedGradientMiddle => Theme.AccentDim;
    public override Color ButtonCheckedGradientEnd => Theme.AccentDim;
    public override Color CheckBackground => Theme.AccentDim;
    public override Color CheckSelectedBackground => Theme.AccentDim;
    public override Color CheckPressedBackground => Theme.AccentDim;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color OverflowButtonGradientBegin => Theme.Surface;
    public override Color OverflowButtonGradientMiddle => Theme.Surface;
    public override Color OverflowButtonGradientEnd => Theme.Surface;
    public override Color GripDark => Theme.Border;
    public override Color GripLight => Theme.Border;
}

/// <summary>Dark rendering for the menu, toolbar, status bar and context menus, with rounded toolbar buttons.</summary>
internal sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled) e.TextColor = Theme.Disabled;
        else if (e.Item.ForeColor == Theme.Muted || e.Item.ForeColor == Theme.Danger || e.Item.ForeColor == Theme.Success) e.TextColor = e.Item.ForeColor;
        else e.TextColor = Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Enabled: false } ? Theme.Disabled : Theme.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        if (e.Vertical) e.Graphics.DrawLine(p, e.Item.Width / 2, 6, e.Item.Width / 2, e.Item.Height - 6);
        else e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => Pill(e);

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => Pill(e);

    private static void Pill(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var isChecked = item is ToolStripButton { Checked: true };
        if (!item.Enabled || !(item.Selected || item.Pressed || isChecked)) return;
        var color = item.Pressed ? Theme.Hover : isChecked ? Theme.AccentDim : Theme.SurfaceAlt;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, item.Width - 1, item.Height - 1), 8f);
        using var b = new SolidBrush(color);
        e.Graphics.FillPath(b, path);
    }
}
