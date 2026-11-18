using Echostep.Core;

namespace Echostep.UI;

/// <summary>Edits one background pixel rule, including its own action list.</summary>
internal sealed class RuleEditDialog : Form
{
    private readonly PixelRule _work;
    private readonly TextBox _name, _window, _macroFile;
    private readonly IntBox _x, _y, _tol;
    private readonly DurationField _interval, _hold, _cooldown;
    private readonly ColorField _color;
    private readonly ComboBox _match, _mode;
    private readonly CheckBox _enabled;
    private readonly ActionListEditor _actions;

    public PixelRule Result => _work;

    public RuleEditDialog(PixelRule rule, bool isNew)
    {
        _work = rule.Clone();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.Prepare(this);
        Text = isNew ? "New pixel rule" : $"Edit rule: {rule.Name}";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        Size = new Size(860, 800);
        MinimumSize = new Size(760, 640);
        Padding = new Padding(18);

        var grid = new FieldGrid { Dock = DockStyle.Top };
        _name = grid.TextField("Name", _work.Name, 300);

        _x = FieldGrid.NewInt(_work.X, -100_000, 100_000, 80);
        _y = FieldGrid.NewInt(_work.Y, -100_000, 100_000, 80);
        var pick = new RoundButton { Text = "Pick on screen…", AutoSize = true };
        var relative = new CheckBox { Text = "relative to window", AutoSize = true, Checked = _work.Window is not null };
        grid.Row("X, Y", _x, _y, pick, relative);
        _window = grid.TextField("Window", _work.Window, 300);
        _color = grid.Add("Color", new ColorField(_work.Color));

        _tol = FieldGrid.NewInt(_work.Tolerance, 0, 442, 70);
        _match = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        _match.Items.AddRange(["per channel (0–255)", "color distance (0–442)"]);
        _match.SelectedIndex = (int)_work.Match;
        grid.Row("Tolerance", _tol, _match);

        _mode = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        _mode.Items.AddRange([
            "on match: the pixel starts matching the color",
            "on mismatch: the pixel stops matching the color",
            "on change: the pixel changes to any new color",
        ]);
        _mode.SelectedIndex = (int)_work.Mode;
        grid.Add("Fire", _mode);

        _interval = grid.Duration("Check every", _work.IntervalMs, 5, 3_600_000);
        _hold = grid.Duration("Hold for", _work.HoldForMs, 0, 3_600_000);
        _cooldown = grid.Duration("Cooldown", _work.CooldownMs, 0, 86_400_000);

        _macroFile = new TextBox { Text = _work.MacroFile ?? "", Width = 300 };
        var browse = new RoundButton { Text = "Browse…", AutoSize = true };
        grid.Row("Then run macro", _macroFile, browse);
        _enabled = new CheckBox { Text = "Enabled", AutoSize = true, Checked = _work.Enabled };
        var test = new RoundButton { Text = "Test", AutoSize = true };
        var testResult = new Label { AutoSize = true, MaximumSize = new Size(420, 0) };
        grid.Row("", _enabled, test, testResult);

        var tips = new ToolTip();
        tips.SetToolTip(pick, "Follows the mouse: point at the pixel and press Enter to save its position and color (Esc cancels). You can also type the numbers.");
        tips.SetToolTip(_window, "Optional window title (exact, or part of it). When set, X and Y are relative to its client area.");
        _interval.SetTip(tips, "How often the pixel is read.");
        _hold.SetTip(tips, "The condition must hold this long before the rule fires (debounce).");
        _cooldown.SetTip(tips, "Minimum time between two firings.");
        tips.SetToolTip(_macroFile, "Optional .estep file to play after the actions below. Relative paths are beside rules.json.");

        pick.Click += (_, _) => PixelUi.Pick(this, relative.Checked, _window.Text, (px, py, color, title) =>
        {
            _x.Value = Math.Clamp(px, -100_000, 100_000);
            _y.Value = Math.Clamp(py, -100_000, 100_000);
            _window.Text = title ?? "";
            _color.Value = color.ToHex();
        });
        test.Click += (_, _) => testResult.Text = PixelUi.Test((int)_x.Value, (int)_y.Value, _window.Text,
            _mode.SelectedIndex == (int)RuleMode.OnChange ? null : _color.Value, (int)_tol.Value, (MatchMode)_match.SelectedIndex);
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Echostep files (*.estep;*.omr;*.orec)|*.estep;*.omr;*.orec|All files|*.*", InitialDirectory = AppPaths.MacrosDir };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var rel = Path.GetRelativePath(AppPaths.DataDir, dlg.FileName);
            _macroFile.Text = rel.StartsWith("..", StringComparison.Ordinal) ? dlg.FileName : rel;
        };

        var actionsLabel = new Label
        {
            Text = "Actions to run when the rule fires (labels, Goto and Repeat work here too):",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 4),
        };
        _actions = new ActionListEditor(showInsertButton: true) { Dock = DockStyle.Fill, Actions = _work.Actions };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 8, 0, 0),
        };
        var ok = new RoundButton { Text = "OK", Kind = ButtonKind.Primary, AutoSize = true, MinimumSize = new Size(100, 0) };
        var cancel = new RoundButton { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 0) };
        ok.Click += (_, _) => TryAccept();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;

        Controls.Add(_actions);
        Controls.Add(actionsLabel);
        Controls.Add(grid);
        Controls.Add(buttons);
        Theme.Apply(this);
        ResumeLayout(false);
        PerformLayout();
    }

    private void TryAccept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Give the rule a name; it is shown in the list and the log.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_mode.SelectedIndex != (int)RuleMode.OnChange && !_color.IsValid)
        {
            MessageBox.Show(this, "Enter a color like #2D7DD2, or use Pick.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        foreach (var (field, what) in new[] { (_interval, "how often to check"), (_hold, "the hold time"), (_cooldown, "the cooldown") })
        {
            if (field.Problem(what) is not { } problem) continue;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _work.Name = _name.Text.Trim();
        _work.X = (int)_x.Value;
        _work.Y = (int)_y.Value;
        _work.Window = string.IsNullOrWhiteSpace(_window.Text) ? null : _window.Text.Trim();
        _work.Color = _color.IsValid ? _color.Value : _work.Color;
        _work.Tolerance = (int)_tol.Value;
        _work.Match = (MatchMode)_match.SelectedIndex;
        _work.Mode = (RuleMode)_mode.SelectedIndex;
        _work.IntervalMs = (int)(_interval.Ms ?? _work.IntervalMs);
        _work.HoldForMs = (int)(_hold.Ms ?? _work.HoldForMs);
        _work.CooldownMs = (int)(_cooldown.Ms ?? _work.CooldownMs);
        _work.MacroFile = string.IsNullOrWhiteSpace(_macroFile.Text) ? null : _macroFile.Text.Trim();
        _work.Enabled = _enabled.Checked;
        _work.Actions = _actions.Actions;

        var problems = _work.Validate();
        if (problems.Count > 0 &&
            MessageBox.Show(this, "This rule will be skipped while watching until these are fixed:\n\n• " +
                                  string.Join("\n• ", problems) + "\n\nSave it anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        DialogResult = DialogResult.OK;
        Close();
    }
}
