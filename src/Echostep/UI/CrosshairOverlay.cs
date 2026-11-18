using Echostep.Win;

namespace Echostep.UI;

/// <summary>
/// A small click-through, always-on-top crosshair drawn on the desktop around the pixel a playing macro
/// is watching. The centre is left open, so the watched pixel itself is never covered.
/// </summary>
internal sealed class CrosshairOverlay : Form
{
    private static readonly Color Key = Color.FromArgb(1, 2, 3);
    private static readonly Color Line = Color.FromArgb(0xFF, 0x3D, 0x71);
    private const int Gap = 6; // pixels left clear on each side of the watched pixel

    public CrosshairOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Key;
        TransparencyKey = Key;
        Size = new Size(81, 81);
        Text = "Echostep crosshair";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Keep the crosshair out of screen captures where Windows supports it (the open centre covers the rest).
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Centres the crosshair on a physical screen pixel and shows it.</summary>
    public void ShowAt(int x, int y)
    {
        var target = new Point(x - Width / 2, y - Height / 2);
        if (Location != target) Location = target;
        if (!Visible) Show();
    }

    public void HideOverlay()
    {
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; // no anti-aliasing: blended edges would not match the transparency key
        int cx = ClientSize.Width / 2, cy = ClientSize.Height / 2;
        int w = ClientSize.Width - 1, h = ClientSize.Height - 1;
        using var outline = new Pen(Color.Black, 3);
        using var line = new Pen(Line, 1);
        foreach (var pen in new[] { outline, line })
        {
            g.DrawLine(pen, 1, cy, cx - Gap, cy);
            g.DrawLine(pen, cx + Gap, cy, w - 1, cy);
            g.DrawLine(pen, cx, 1, cx, cy - Gap);
            g.DrawLine(pen, cx, cy + Gap, cx, h - 1);
            g.DrawRectangle(pen, cx - Gap, cy - Gap, Gap * 2, Gap * 2);
        }
    }
}
