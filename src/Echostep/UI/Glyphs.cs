using System.Drawing.Drawing2D;

namespace Echostep.UI;

/// <summary>
/// Small line icons drawn straight onto a button in the button's current text color, so they stay
/// sharp at any display scaling and dim with the button when it is disabled.
/// </summary>
internal static class Glyphs
{
    public delegate void Draw(Graphics g, RectangleF box, Color color);

    private static Pen Line(Color c, float k) => new(c, 1.7f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    /// <summary>Runs <paramref name="body"/> in a 16 × 16 design grid centered in <paramref name="box"/>.</summary>
    private static void In(Graphics g, RectangleF box, Action<float, float, float> body)
    {
        var k = Math.Min(box.Width, box.Height) / 16f;
        body(box.X + (box.Width - 16 * k) / 2, box.Y + (box.Height - 16 * k) / 2, k);
    }

    public static readonly Draw Pencil = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        PointF P(float px, float py) => new(x + px * k, y + py * k);
        g.DrawPolygon(p, [P(3, 13), P(3.5f, 10), P(11, 2.5f), P(13.5f, 5), P(6, 12.5f)]);
        g.DrawLine(p, P(9.5f, 4), P(12, 6.5f));
    });

    public static readonly Draw Trash = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        PointF P(float px, float py) => new(x + px * k, y + py * k);
        g.DrawLine(p, P(2.5f, 4), P(13.5f, 4));
        g.DrawLines(p, [P(6, 4), P(6.5f, 2), P(9.5f, 2), P(10, 4)]);
        g.DrawLines(p, [P(4, 4), P(5, 14), P(11, 14), P(12, 4)]);
        g.DrawLine(p, P(7, 7), P(7, 11.5f));
        g.DrawLine(p, P(9, 7), P(9, 11.5f));
    });

    public static readonly Draw ArrowUp = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        PointF P(float px, float py) => new(x + px * k, y + py * k);
        g.DrawLine(p, P(8, 13.5f), P(8, 2.5f));
        g.DrawLines(p, [P(3.5f, 7), P(8, 2.5f), P(12.5f, 7)]);
    });

    public static readonly Draw ArrowDown = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        PointF P(float px, float py) => new(x + px * k, y + py * k);
        g.DrawLine(p, P(8, 2.5f), P(8, 13.5f));
        g.DrawLines(p, [P(3.5f, 9), P(8, 13.5f), P(12.5f, 9)]);
    });

    public static readonly Draw Search = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        g.DrawEllipse(p, x + 2.5f * k, y + 2.5f * k, 8 * k, 8 * k);
        g.DrawLine(p, x + 9.3f * k, y + 9.3f * k, x + 13.5f * k, y + 13.5f * k);
    });

    public static readonly Draw Plus = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        g.DrawLine(p, x + 8 * k, y + 3 * k, x + 8 * k, y + 13 * k);
        g.DrawLine(p, x + 3 * k, y + 8 * k, x + 13 * k, y + 8 * k);
    });

    public static readonly Draw Copy = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        using var back = Theme.RoundRect(new RectangleF(x + 2.5f * k, y + 2.5f * k, 8 * k, 8 * k), 1.5f * k);
        using var front = Theme.RoundRect(new RectangleF(x + 5.5f * k, y + 5.5f * k, 8 * k, 8 * k), 1.5f * k);
        g.DrawPath(p, back);
        g.DrawPath(p, front);
    });

    public static readonly Draw Reload = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        g.DrawArc(p, x + 2.5f * k, y + 2.5f * k, 11 * k, 11 * k, -60, 300);
        PointF P(float px, float py) => new(x + px * k, y + py * k);
        g.DrawLines(p, [P(10.2f, 1.6f), P(11.2f, 3.8f), P(8.8f, 4.6f)]);
    });

    /// <summary>A plus with a small ▾, for the Insert menu button.</summary>
    public static readonly Draw PlusMenu = (g, box, c) => In(g, box, (x, y, k) =>
    {
        using var p = Line(c, k);
        g.DrawLine(p, x + 6 * k, y + 3 * k, x + 6 * k, y + 11 * k);
        g.DrawLine(p, x + 2 * k, y + 7 * k, x + 10 * k, y + 7 * k);
        using var b = new SolidBrush(c);
        g.FillPolygon(b, [new PointF(x + 11 * k, y + 10 * k), new PointF(x + 15 * k, y + 10 * k), new PointF(x + 13 * k, y + 12.5f * k)]);
    });
}

/// <summary>A compact square button showing only a glyph; its name and shortcut are in the tooltip.</summary>
internal sealed class IconButton : RoundButton
{
    public IconButton(Glyphs.Draw glyph, string tip, ToolTip tips, ButtonKind kind = ButtonKind.Normal)
    {
        Glyph = glyph;
        Kind = kind;
        AutoSize = false;
        Size = new Size(40, 34);
        Padding = Padding.Empty;
        Margin = new Padding(0, 0, 0, 6);
        AccessibleName = tip;
        tips.SetToolTip(this, tip);
    }
}
