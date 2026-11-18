using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Echostep.UI;

internal enum RibbonStyle
{
    /// <summary>Icon over a caption.</summary>
    Big,
    /// <summary>One line of text.</summary>
    Small,
    /// <summary>The thin ▾ strip under a split button.</summary>
    Strip,
}

/// <summary>An owner-drawn ribbon button. Give it a menu to make it a drop-down.</summary>
internal sealed class RibbonButton : Control
{
    private readonly Bitmap? _icon;
    private readonly RibbonStyle _style;
    private string _caption;
    private ContextMenuStrip? _menu;
    private bool _hover, _down, _checked;

    public RibbonButton(string caption, Bitmap? icon, RibbonStyle style)
    {
        _caption = caption;
        _icon = icon;
        _style = style;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    public bool IsChecked => _checked;

    public void SetCaption(string caption)
    {
        if (_caption == caption) return;
        _caption = caption;
        Invalidate();
    }

    public void SetChecked(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        Invalidate();
    }

    /// <summary>Makes the button open a menu below itself when clicked.</summary>
    public void SetMenu(ContextMenuStrip menu) => _menu = menu;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _menu?.Show(this, new Point(0, Height));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Parent?.BackColor ?? Theme.Surface;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Enabled && (_hover || _down || _checked))
        {
            var fill = _down ? Theme.Border : _checked ? Theme.AccentDim : Theme.Hover;
            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), _style == RibbonStyle.Strip ? 4f : 7f);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        var text = Enabled ? Theme.Text : Theme.Disabled;
        switch (_style)
        {
            case RibbonStyle.Strip:
                TextRenderer.DrawText(g, "▾", Font, ClientRectangle, Enabled ? Theme.Muted : Theme.Disabled,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;

            case RibbonStyle.Small:
                TextRenderer.DrawText(g, _caption, Font, new Rectangle(10, 0, Width - 12, Height), text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                break;

            default:
                var caption = _menu is null ? _caption : _caption + " ▾";
                var top = 4;
                if (_icon is not null)
                {
                    var x = (Width - _icon.Width) / 2;
                    if (Enabled) g.DrawImage(_icon, x, top, _icon.Width, _icon.Height);
                    else
                    {
                        using var faded = new ImageAttributes();
                        faded.SetColorMatrix(new ColorMatrix { Matrix33 = 0.3f });
                        g.DrawImage(_icon, new Rectangle(x, top, _icon.Width, _icon.Height), 0, 0, _icon.Width, _icon.Height, GraphicsUnit.Pixel, faded);
                    }
                    top += _icon.Height + 2;
                }
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak |
                            (_icon is null ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top);
                TextRenderer.DrawText(g, caption, Font, new Rectangle(0, top, Width, Math.Max(1, Height - top)), text, flags);
                break;
        }
    }
}

/// <summary>The tab row above the ribbon: File opens a menu, the other tabs switch the ribbon panel.</summary>
internal sealed class RibbonTabRow : Control
{
    private readonly (string Text, int X, int Width)[] _tabs;
    private int _selected = 1, _hover = -1;

    /// <summary>Raised with the index of the chosen tab (never 0: that is File).</summary>
    public event Action<int>? TabSelected;

    /// <summary>Raised when the File tab is clicked.</summary>
    public event Action? FileClicked;

    /// <param name="tabs">Text, left edge and width in design pixels (96 dpi). Tab 0 is File.</param>
    public RibbonTabRow(params (string Text, int X, int Width)[] tabs)
    {
        _tabs = tabs;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    public void Select(int index)
    {
        if (index <= 0 || index >= _tabs.Length) return;
        if (index != _selected)
        {
            _selected = index;
            Invalidate();
        }
        // Raised on every click, also for the tab that is already selected (e.g. to get back to the macro from Settings).
        TabSelected?.Invoke(index);
    }

    private float DpiScale => this.DeviceDpi / 96f;

    private Rectangle TabBounds(int i) => new((int)(_tabs[i].X * DpiScale), 0, (int)(_tabs[i].Width * DpiScale), Height);

    private int Hit(Point p)
    {
        for (var i = 0; i < _tabs.Length; i++)
            if (TabBounds(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h == 0) FileClicked?.Invoke();
        else Select(h);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        for (var i = 0; i < _tabs.Length; i++)
        {
            var r = TabBounds(i);
            var fill = i == 0 ? (Theme.IsDark ? (i == _hover ? Theme.Accent : Theme.AccentDim) : i == _hover ? Theme.AccentHover : Theme.Accent)
                : i == _selected ? Theme.Surface
                : i == _hover ? Theme.SurfaceAlt : Theme.Bg;
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            var color = i == 0 ? (Theme.IsDark ? Color.White : Theme.OnAccent) : i == _selected ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, _tabs[i].Text, Font, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>One tab's panel of the ribbon. Positions are design pixels; the form's DPI scaling does the rest.</summary>
internal sealed class RibbonPanel : Panel
{
    private readonly ToolTip _tips = new();

    public RibbonPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
        Visible = false;
        AutoScroll = true; // scrolls sideways when the window is narrower than the buttons
        HandleCreated += (_, _) => Echostep.Win.Native.UseDarkScrollbars(Handle, Theme.IsDark);
    }

    public T Put<T>(T control, int x, int y, int width, int height, string? tip = null) where T : Control
    {
        control.SetBounds(x, y, width, height);
        Controls.Add(control);
        if (tip is not null) _tips.SetToolTip(control, tip);
        return control;
    }

    /// <summary>A 90 px vertical divider.</summary>
    public void Separator(int x) => Put(new Panel { BackColor = Theme.Border }, x, 4, 1, 90);

    /// <summary>A group caption on the bottom line.</summary>
    public void Caption(string text, int x, int width) =>
        Put(new Label { Text = text, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false }, x, 80, width, 16);

    public Label Note(string text, int x, int y) =>
        Put(new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted }, x, y + 3, 80, 18);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
