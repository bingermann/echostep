using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>Small themed dialogs: prompts, the delete confirmation and the after-playback countdown.</summary>
internal static class Dialogs
{
    private static Form NewDialog(string title, out TableLayoutPanel layout)
    {
        var f = new Form
        {
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18),
        };
        Theme.Prepare(f);
        layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Location = new Point(18, 18), Margin = Padding.Empty,
        };
        f.Controls.Add(layout);
        return f;
    }

    private static FlowLayoutPanel Buttons(params Control[] rightToLeft)
    {
        var p = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 12, 0, 0), WrapContents = false,
        };
        p.Controls.AddRange(rightToLeft);
        return p;
    }

    /// <summary>Asks for a whole number; null if cancelled.</summary>
    public static int? AskNumber(IWin32Window? owner, string title, string prompt, int value, int min, int max)
    {
        using var f = NewDialog(title, out var layout);
        var box = new IntBox(value, min, max, 140) { Margin = new Padding(3, 6, 3, 3) };
        var ok = new RoundButton { Text = "OK", Kind = ButtonKind.Primary, AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.OK };
        var cancel = new RoundButton { Text = "Cancel", AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.Cancel };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true });
        layout.Controls.Add(box);
        layout.Controls.Add(Buttons(cancel, ok));
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        Theme.Apply(f);
        return f.ShowDialog(owner) == DialogResult.OK ? box.Value : null;
    }

    /// <summary>
    /// "Delete the selected actions?" with "Do not show again". Returns true to delete.
    /// Skipped (returns true) when the confirmation is switched off in Settings.
    /// </summary>
    public static bool ConfirmDelete(IWin32Window? owner, int count)
    {
        if (!Program.Settings.ShowDeleteConfirmation) return true;
        using var f = NewDialog("Delete", out var layout);
        var again = new CheckBox { Text = "Do not show again", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        var delete = new RoundButton { Text = "Delete", Kind = ButtonKind.Danger, AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.OK };
        var cancel = new RoundButton { Text = "Cancel", AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.Cancel };
        layout.Controls.Add(new Label { Text = count == 1 ? "Delete the selected action?" : $"Delete the {count} selected actions?", AutoSize = true });
        layout.Controls.Add(again);
        layout.Controls.Add(Buttons(cancel, delete));
        f.AcceptButton = delete;
        f.CancelButton = cancel;
        Theme.Apply(f);
        if (f.ShowDialog(owner) != DialogResult.OK) return false;
        if (again.Checked)
        {
            Program.Settings.ShowDeleteConfirmation = false;
            Program.Settings.Save(AppPaths.SettingsFile);
        }
        return true;
    }

    /// <summary>Counts down before a power action. Returns false if the user cancelled.</summary>
    public static bool Countdown(IWin32Window? owner, string what, int seconds)
    {
        using var f = NewDialog("After playback", out var layout);
        f.TopMost = true;
        var label = new Label { AutoSize = true, MinimumSize = new Size(320, 0) };
        var bar = new ProgressBar { Width = 320, Height = 20, Maximum = seconds * 10, Margin = new Padding(3, 10, 3, 3) };
        var cancel = new RoundButton { Text = "Cancel", AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.Cancel };
        layout.Controls.Add(label);
        layout.Controls.Add(bar);
        layout.Controls.Add(Buttons(cancel));
        f.CancelButton = cancel;
        f.AcceptButton = cancel; // Enter cancels too: the safe choice is the default
        Theme.Apply(f);
        var ticks = 0;
        void Show() => label.Text = $"{what} in {Math.Max(0, seconds - ticks / 10)} seconds…";
        Show();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            ticks++;
            bar.Value = Math.Min(bar.Maximum, ticks);
            Show();
            if (ticks >= seconds * 10)
            {
                timer.Stop();
                f.DialogResult = DialogResult.OK;
                f.Close();
            }
        };
        timer.Start();
        return f.ShowDialog(owner) == DialogResult.OK;
    }
}

/// <summary>
/// Find across the action list, optionally with replace. Stays open beside the list while you work.
/// Tick "Replace" to show the replace field and buttons.
/// </summary>
internal sealed class FindDialog : Form
{
    private readonly ActionListEditor _editor;
    private readonly TextBox _find = new() { Width = 300 };
    private readonly TextBox _replace = new() { Width = 300 };
    private readonly ComboBox _field = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _replaceMode = new() { Text = "Replace", AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(300, 0) };
    private readonly List<Control> _replaceOnly = [];

    public FindDialog(ActionListEditor editor)
    {
        _editor = editor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.Prepare(this);
        Text = "Find";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        KeyPreview = true;

        foreach (var (_, name) in MacroEditing.FieldNames) _field.Items.Add(name);
        _field.SelectedIndex = 0;

        var grid = new FieldGrid();
        grid.Add("Find", _find);
        grid.Add("In", _field);
        grid.Add("", _replaceMode);
        var replaceRow = grid.Add("Replace with", _replace);
        _replaceOnly.Add(replaceRow);
        _replaceOnly.Add(grid.GetControlFromPosition(0, grid.GetRow(replaceRow))!);
        grid.Add("", _result);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(14, 0, 0, 0) };
        RoundButton Btn(string text, Action click, ButtonKind kind = ButtonKind.Normal)
        {
            var b = new RoundButton { Text = text, Kind = kind, AutoSize = true, MinimumSize = new Size(140, 0) };
            b.Click += (_, _) => click();
            buttons.Controls.Add(b);
            return b;
        }
        var next = Btn("Find next", () => Find(true), ButtonKind.Primary);
        Btn("Find previous", () => Find(false));
        Btn("Select all", SelectAll);
        _replaceOnly.Add(Btn("Replace", ReplaceOne));
        _replaceOnly.Add(Btn("Replace all", ReplaceAll));
        Btn("Close", Close);

        var tips = new ToolTip();
        tips.SetToolTip(_field, "Which parameter to look in. All looks in every one.");
        tips.SetToolTip(_replaceMode, "Show the replace field and buttons.");

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Location = new Point(18, 18) };
        layout.Controls.Add(grid, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        Controls.Add(layout);
        AcceptButton = next;
        _replaceMode.CheckedChanged += (_, _) => SyncMode();
        Theme.Apply(this);
        SyncMode();
        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>Shows or hides the replace part of the dialog.</summary>
    public void SetReplaceMode(bool replace)
    {
        _replaceMode.Checked = replace;
        SyncMode();
        (replace && _find.TextLength > 0 ? _replace : _find).Focus();
    }

    private void SyncMode()
    {
        foreach (var c in _replaceOnly) c.Visible = _replaceMode.Checked;
        Text = _replaceMode.Checked ? "Find and replace" : "Find";
        _result.Text = "";
    }

    private FindField Field => MacroEditing.FieldNames[Math.Max(0, _field.SelectedIndex)].Field;

    private void Find(bool forward) =>
        _result.Text = _find.Text.Length == 0 ? "Type what to find."
            : _editor.FindNext(_find.Text, Field, forward) ? "" : "Not found.";

    private void SelectAll()
    {
        if (_find.Text.Length == 0) { _result.Text = "Type what to find."; return; }
        var n = _editor.SelectAllMatches(_find.Text, Field);
        _result.Text = n == 0 ? "Not found." : $"{n} row{(n == 1 ? "" : "s")} selected.";
    }

    private void ReplaceOne()
    {
        if (_find.Text.Length == 0) { _result.Text = "Type what to find."; return; }
        var n = _editor.ReplaceInSelected(_find.Text, _replace.Text, Field);
        var more = _editor.FindNext(_find.Text, Field, true);
        _result.Text = n > 0 ? $"Replaced {n} occurrence{(n == 1 ? "" : "s")}." : more ? "" : "Not found.";
    }

    private void ReplaceAll()
    {
        if (_find.Text.Length == 0) { _result.Text = "Type what to find."; return; }
        var n = _editor.ReplaceAll(_find.Text, _replace.Text, Field);
        _result.Text = $"Replaced {n} occurrence{(n == 1 ? "" : "s")}.";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}

/// <summary>Dims every screen so a rectangle can be dragged: the zone for Wait for screen change.</summary>
internal sealed class ZoneSelectForm : Form
{
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;

    private ZoneSelectForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.Black;
        Opacity = 0.45;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Echostep zone";
    }

    /// <summary>Lets the user drag a rectangle; returns it in screen pixels, or null if cancelled (Esc).</summary>
    public static Rectangle? Pick()
    {
        using var f = new ZoneSelectForm();
        var ok = f.ShowDialog() == DialogResult.OK;
        var r = f._selection;
        if (!ok || r.Width < 2 || r.Height < 2) return null;
        r.Offset(f.Left, f.Top);
        return r;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { DialogResult = DialogResult.Cancel; return; }
        _dragging = true;
        _start = e.Location;
        _selection = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _selection = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DialogResult = DialogResult.OK;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var font = new Font("Segoe UI", 14f);
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(0x9A, 0xC4, 0xFF));
            g.FillRectangle(fill, _selection);
            g.DrawRectangle(Pens.White, _selection);
            g.DrawString($"{_selection.Width} × {_selection.Height}", font, Brushes.White, _selection.Left + 4, _selection.Bottom + 4);
        }
        else
        {
            var primary = Screen.PrimaryScreen?.Bounds ?? Bounds;
            g.DrawString("Drag a rectangle around the zone to watch.  Esc cancels.", font, Brushes.White,
                primary.Left - Left + 40, primary.Top - Top + 40);
        }
    }
}

/// <summary>Briefly outlines a zone on the screen.</summary>
internal sealed class ZoneFlash : Form
{
    private static readonly Color Key = Color.FromArgb(1, 2, 3);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1300 };

    private ZoneFlash(Rectangle zone)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Key;
        TransparencyKey = Key;
        zone.Inflate(3, 3);
        Bounds = zone;
        _timer.Tick += (_, _) => Close();
    }

    public static void Show(Rectangle zone)
    {
        var f = new ZoneFlash(zone);
        f.Show();
        f._timer.Start();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        using var outer = new Pen(Color.Black, 5);
        using var inner = new Pen(Color.FromArgb(0xFF, 0x3D, 0x71), 3);
        var r = new Rectangle(2, 2, ClientSize.Width - 5, ClientSize.Height - 5);
        e.Graphics.DrawRectangle(outer, r);
        e.Graphics.DrawRectangle(inner, r);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
