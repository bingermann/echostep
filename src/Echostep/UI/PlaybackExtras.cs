using System.Diagnostics;
using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>
/// A small always-on-top box in the bottom-right corner while a macro plays: what it is doing, the row,
/// time elapsed and passes left. Clicks go through it, and it is left out of screen captures, so it never
/// gets in the way of the macro (or of Find image).
/// </summary>
internal sealed class PlaybackOverlay : Form
{
    private string _title = "", _line1 = "", _line2 = "", _line3 = "";

    public PlaybackOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Surface;
        Opacity = 0.92;
        Font = new Font("Segoe UI", 9f);
        Text = "Echostep progress";
        DoubleBuffered = true;
        var k = this.DeviceDpi / 96f;
        Size = new Size((int)(320 * k), (int)(92 * k));
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
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void ShowProgress(string title, string line1, string line2, string line3)
    {
        if (title != _title || line1 != _line1 || line2 != _line2 || line3 != _line3)
        {
            (_title, _line1, _line2, _line3) = (title, line1, line2, line3);
            Invalidate();
        }
        if (Visible) return;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var gap = (int)(16 * this.DeviceDpi / 96f);
        Location = new Point(area.Right - Width - gap, area.Bottom - Height - gap);
        Show();
    }

    public void HideOverlay()
    {
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var k = this.DeviceDpi / 96f;
        using (var border = new Pen(Theme.Accent, 2 * k)) g.DrawRectangle(border, k, k, Width - 2 * k, Height - 2 * k);
        using var bold = new Font(Font, FontStyle.Bold);
        var x = 12 * k;
        TextRenderer.DrawText(g, _title, bold, new Rectangle((int)x, (int)(8 * k), Width - (int)(24 * k), (int)(20 * k)), Theme.Success,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var y = 30 * k;
        foreach (var (line, color) in new[] { (_line1, Theme.Text), (_line2, Theme.Text), (_line3, Theme.Muted) })
        {
            TextRenderer.DrawText(g, line, Font, new Rectangle((int)x, (int)y, Width - (int)(24 * k), (int)(18 * k)), color,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += 18 * k;
        }
    }
}

/// <summary>Shared look for the small Extras dialogs: a field grid with buttons underneath.</summary>
internal abstract class SmallDialog : Form
{
    protected readonly FieldGrid Grid = new();
    protected readonly FlowLayoutPanel Buttons;

    protected SmallDialog(string title)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.Prepare(this);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        Buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 0), WrapContents = false,
        };
    }

    protected RoundButton AddButton(string text, bool primary = false, DialogResult result = DialogResult.None)
    {
        var b = new RoundButton { Text = text, AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = result };
        if (primary) b.Kind = ButtonKind.Primary;
        Buttons.Controls.Add(b);
        return b;
    }

    protected void Finish()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Location = new Point(Padding.Left, Padding.Top), Margin = Padding.Empty,
        };
        layout.Controls.Add(DialogFit.Wrap(this, Grid, Buttons), 0, 0);
        layout.Controls.Add(Buttons, 0, 1);
        Controls.Add(layout);
        Theme.Apply(this);
        ResumeLayout(false);
        PerformLayout();
    }

    protected static ListBox NewList(int width, int height) => new()
    {
        Width = width, Height = height, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
        IntegralHeight = false,
    };

    protected static string? BrowseMacro(IWin32Window owner, string? current)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Echostep files (*.estep;*.omr;*.orec)|*.estep;*.omr;*.orec|All files|*.*",
            InitialDirectory = string.IsNullOrWhiteSpace(current) ? AppPaths.MacrosDir : Path.GetDirectoryName(current),
        };
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
    }
}

/// <summary>Hotkeys that play a macro file straight away, without opening it.</summary>
internal sealed class MacroHotkeysDialog : SmallDialog
{
    private readonly List<MacroHotkey> _items;
    private readonly ListBox _list = NewList(520, 150);

    /// <summary>The edited list; only meaningful after OK.</summary>
    public List<MacroHotkey> Result => _items;

    public MacroHotkeysDialog(IEnumerable<MacroHotkey> current, string? currentFile) : base("Macro hotkeys")
    {
        _items = current.Select(h => new MacroHotkey { Hotkey = h.Hotkey, File = h.File }).ToList();
        Grid.Note("Each hotkey plays its macro file at once, even while Echostep is minimized. The file does not need to be open, " +
                  "and the macro in the window is left as it is. Use combinations like Ctrl+Alt+1 so they do not clash with normal typing.", 520);
        Grid.Add("", _list);
        var hotkey = new KeyCaptureBox { Width = 200 };
        var hotkeyText = new TextBox { Width = 150 };
        Grid.Row("Hotkey", hotkeyText, hotkey);
        hotkey.Captured += (vk, mods) => hotkeyText.Text = new Hotkey(mods, vk).ToString();
        var file = new TextBox { Width = 380, Text = currentFile ?? "" };
        var browse = new RoundButton { Text = "Browse…", AutoSize = true };
        Grid.Row("Macro file", file, browse);
        browse.Click += (_, _) => { if (BrowseMacro(this, file.Text) is { } f) file.Text = f; };
        var add = new RoundButton { Text = "Add", AutoSize = true, Kind = ButtonKind.Primary };
        var remove = new RoundButton { Text = "Remove selected", AutoSize = true };
        Grid.Row("", add, remove);
        add.Click += (_, _) =>
        {
            if (!Hotkey.TryParse(hotkeyText.Text, out var hk) || hk.IsEmpty)
            {
                MessageBox.Show(this, "Press the hotkey in the box (or type it like Ctrl+Alt+1).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(file.Text.Trim()))
            {
                MessageBox.Show(this, "Choose a macro file that exists.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var text = hk.ToString();
            _items.RemoveAll(i => string.Equals(i.Hotkey, text, StringComparison.OrdinalIgnoreCase));
            _items.Add(new MacroHotkey { Hotkey = text, File = Path.GetFullPath(file.Text.Trim()) });
            Refill();
            hotkeyText.Text = "";
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _items.RemoveAt(_list.SelectedIndex);
            Refill();
        };
        var ok = AddButton("OK", primary: true, DialogResult.OK);
        var cancel = AddButton("Cancel", result: DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Refill();
        Finish();
    }

    private void Refill()
    {
        _list.Items.Clear();
        foreach (var i in _items) _list.Items.Add($"{i.Hotkey,-18}  {i.File}");
        if (_items.Count == 0) _list.Items.Add("(no macro hotkeys yet)");
    }
}

/// <summary>Creates and removes Windows Task Scheduler tasks that play a macro at set times.</summary>
internal sealed class ScheduleDialog : SmallDialog
{
    private readonly ListBox _list = NewList(560, 140);

    public ScheduleDialog(string? currentFile) : base("Schedule macros")
    {
        Grid.Note("Plays a macro at a set time with Windows Task Scheduler, even when Echostep is closed. " +
                  "You need to be signed in to Windows at that time (the macro uses your screen), and the PC must be awake. " +
                  "Echostep opens, plays the macro and closes again.", 560);
        Grid.Add("", _list);
        var remove = new RoundButton { Text = "Remove selected", AutoSize = true };
        var openScheduler = new RoundButton { Text = "Open Task Scheduler", AutoSize = true };
        Grid.Row("", remove, openScheduler);

        Grid.Header("New schedule");
        var file = new TextBox { Width = 400, Text = currentFile ?? "" };
        var browse = new RoundButton { Text = "Browse…", AutoSize = true };
        Grid.Row("Macro file", file, browse);
        var name = Grid.TextField("Name", currentFile is null ? "" : Path.GetFileNameWithoutExtension(currentFile), 240);
        browse.Click += (_, _) =>
        {
            if (BrowseMacro(this, file.Text) is not { } f) return;
            file.Text = f;
            if (name.Text.Trim().Length == 0) name.Text = Path.GetFileNameWithoutExtension(f);
        };
        var hour = FieldGrid.NewInt(8, 0, 23, 56);
        var minute = FieldGrid.NewInt(0, 0, 59, 56);
        Grid.Row("Time (24-hour)", hour, new Label { Text = "h", AutoSize = true }, minute, new Label { Text = "min", AutoSize = true });
        var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var days = dayNames.Select((d, i) => new CheckBox { Text = d, AutoSize = true, Checked = i < 5 }).ToArray();
        Grid.Row("Days", days.Cast<Control>().ToArray());
        RoundButton Quick(string text, Func<int, bool> on)
        {
            var b = new RoundButton { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
            b.Click += (_, _) => { for (var i = 0; i < 7; i++) days[i].Checked = on(i); };
            return b;
        }
        Grid.Row("", Quick("Weekdays", i => i < 5), Quick("Weekend", i => i >= 5), Quick("Every day", _ => true));
        var add = new RoundButton { Text = "Create schedule", AutoSize = true, Kind = ButtonKind.Primary };
        Grid.Row("", add);

        add.Click += (_, _) =>
        {
            var path = file.Text.Trim();
            var taskName = string.Concat(name.Text.Trim().Where(c => !"\\/:*?\"<>|".Contains(c)));
            string? problem = !File.Exists(path) ? "Choose a macro file that exists (save the macro first)."
                : taskName.Length == 0 ? "Give the schedule a name."
                : !days.Any(d => d.Checked) ? "Tick at least one day."
                : null;
            if (problem is not null)
            {
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var schedule = new MacroSchedule
            {
                Name = taskName,
                File = Path.GetFullPath(path),
                Time = $"{hour.Value:00}:{minute.Value:00}",
                Days = IfAction.DayKeys.Where((_, i) => days[i].Checked).ToList(),
            };
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var (ok, output) = SchTasks(schedule.CreateArguments(exe));
            if (!ok)
            {
                MessageBox.Show(this, "Task Scheduler refused the schedule:\n\n" + output, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var list = Program.Settings.Schedules;
            list.RemoveAll(s => string.Equals(s.Name, schedule.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(schedule);
            Program.Settings.Save(AppPaths.SettingsFile);
            Log.Write($"Scheduled {Path.GetFileName(schedule.File)}: {schedule.Describe()}.");
            Refill();
        };
        remove.Click += (_, _) =>
        {
            var list = Program.Settings.Schedules;
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= list.Count) return;
            var s = list[_list.SelectedIndex];
            var (ok, output) = SchTasks(["/Delete", "/F", "/TN", s.TaskPath]);
            // A task already deleted in Task Scheduler is simply dropped from the list.
            if (!ok && !output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, $"Task Scheduler could not remove it:\n\n{output}\n\nRemove it from this list anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            list.Remove(s);
            Program.Settings.Save(AppPaths.SettingsFile);
            Refill();
        };
        openScheduler.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        };

        var close = AddButton("Close", primary: true, DialogResult.OK);
        AcceptButton = close;
        CancelButton = close;
        Refill();
        Finish();
    }

    private void Refill()
    {
        _list.Items.Clear();
        foreach (var s in Program.Settings.Schedules) _list.Items.Add($"{s.Name}  —  {s.Describe()}  —  {Path.GetFileName(s.File)}");
    }

    private static (bool Ok, string Output) SchTasks(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>Asks for one whole number (used by Repeat → Other number…).</summary>
internal sealed class NumberDialog : SmallDialog
{
    private readonly IntBox _box;

    /// <summary>The number entered; only meaningful after OK.</summary>
    public int Value => _box.Value;

    public NumberDialog(string title, string prompt, int value, int min, int max) : base(title)
    {
        _box = FieldGrid.NewInt(Math.Clamp(value, min, max), min, max, 110);
        Grid.Row(prompt, _box, new Label { Text = $"{min:N0}–{max:N0}", AutoSize = true, ForeColor = Theme.Muted });
        var ok = AddButton("OK", primary: true, DialogResult.OK);
        var cancel = AddButton("Cancel", result: DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
        Finish();
    }
}
