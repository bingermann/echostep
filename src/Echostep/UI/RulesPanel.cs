using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>
/// The Rules tab: one row per pixel rule with target and live color swatches, match state,
/// last fired time and an enabled checkbox. Changes are saved to rules.json straight away.
/// </summary>
internal sealed class RulesPanel : UserControl
{
    private readonly DataGridView _grid;
    private readonly RuleEngine _engine;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly Label _path;
    private readonly Button _edit, _duplicate, _delete;
    private List<PixelRule> _rules = [];

    private const int ColOn = 0, ColName = 1, ColTarget = 2, ColLive = 3, ColState = 4, ColFired = 5, ColPixel = 6, ColMode = 7;

    public RulesPanel(RuleEngine engine)
    {
        _engine = engine;
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EditMode = DataGridViewEditMode.EditProgrammatically,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 180, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Target", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Live", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State", Width = 110, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Last fired", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pixel", Width = 150, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        foreach (DataGridViewColumn c in _grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        GridColumns.Setup(_grid, 40, [180, 90, 90, 110, 90, 150, 120], Program.Settings.RuleColumnLayout,
            layout => Program.Settings.RuleColumnLayout = layout);
        _grid.CellPainting += Grid_CellPainting;
        _grid.CellContentClick += Grid_CellContentClick;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != ColOn) EditSelected(); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 0, 0), WrapContents = false,
        };
        // Compact icon buttons; names are in their tooltips.
        var tips = new ToolTip();
        Button Btn(Glyphs.Draw glyph, string tip, Action a, ButtonKind kind = ButtonKind.Normal, int gapAfter = 0)
        {
            var b = new IconButton(glyph, tip, tips, kind);
            if (gapAfter > 0) b.Margin = new Padding(0, 0, 0, gapAfter);
            b.Click += (_, _) => a();
            side.Controls.Add(b);
            return b;
        }
        Btn(Glyphs.Plus, "Add a rule", () => AddRule(new PixelRule()), ButtonKind.Primary, 14);
        _edit = Btn(Glyphs.Pencil, "Edit (Enter)", EditSelected);
        _duplicate = Btn(Glyphs.Copy, "Duplicate", DuplicateSelected, gapAfter: 14);
        _delete = Btn(Glyphs.Trash, "Delete (Del)", DeleteSelected, ButtonKind.Danger, 14);
        Btn(Glyphs.Reload, "Reload rules.json from disk", Reload);

        _path = new Label { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0), ForeColor = Theme.Muted };

        Controls.Add(_grid);
        Controls.Add(side);
        Controls.Add(_path);

        _timer.Tick += (_, _) => UpdateLive();
        _timer.Start();
    }

    public IReadOnlyList<PixelRule> Rules => _rules;

    /// <summary>Loads rules.json and gives the rules to the engine.</summary>
    public void Reload()
    {
        try
        {
            _rules = RulesFile.Load(AppPaths.RulesFile);
            Log.Write($"Loaded {_rules.Count} rule(s) from {AppPaths.RulesFile}.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Write(ex.Message);
            MessageBox.Show(FindForm(), ex.Message + "\n\nFix the file and press Reload file. Saving from this tab would overwrite it.",
                "Echostep rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _rules = [];
        }
        _path.Text = $"Rules file: {AppPaths.RulesFile}";
        PushToEngine();
        RebuildRows();
    }

    /// <summary>Adds a rule through the edit dialog (used by Add and by the picker hotkey).</summary>
    public void AddRule(PixelRule rule)
    {
        using var dlg = new RuleEditDialog(rule, isNew: true);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _rules.Add(dlg.Result);
        Commit();
        SelectRow(_rules.Count - 1);
    }

    private void EditSelected()
    {
        var i = SelectedIndex;
        if (i < 0) return;
        using var dlg = new RuleEditDialog(_rules[i], isNew: false);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _rules[i] = dlg.Result;
        Commit();
        SelectRow(i);
    }

    private void DuplicateSelected()
    {
        var i = SelectedIndex;
        if (i < 0) return;
        var copy = _rules[i].Clone();
        copy.Name += " (copy)";
        _rules.Insert(i + 1, copy);
        Commit();
        SelectRow(i + 1);
    }

    private void DeleteSelected()
    {
        var i = SelectedIndex;
        if (i < 0) return;
        if (MessageBox.Show(FindForm(), $"Delete the rule \"{_rules[i].Name}\"?", "Echostep",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _rules.RemoveAt(i);
        Commit();
        SelectRow(Math.Min(i, _rules.Count - 1));
    }

    private void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != ColOn) return;
        _rules[e.RowIndex].Enabled = !_rules[e.RowIndex].Enabled;
        Commit();
    }

    private int SelectedIndex => _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Index : -1;

    private void SelectRow(int i)
    {
        if (i < 0 || i >= _grid.Rows.Count) return;
        _grid.ClearSelection();
        _grid.Rows[i].Selected = true;
        _grid.CurrentCell = _grid.Rows[i].Cells[ColName];
    }

    private void UpdateButtons()
    {
        var has = SelectedIndex >= 0;
        _edit.Enabled = _duplicate.Enabled = _delete.Enabled = has;
    }

    /// <summary>Saves rules.json, updates the engine and the grid.</summary>
    private void Commit()
    {
        try
        {
            RulesFile.Save(AppPaths.RulesFile, _rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(FindForm(), $"Could not save {AppPaths.RulesFile}: {ex.Message}", "Echostep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        PushToEngine();
        RebuildRows();
    }

    /// <summary>Gives the engine fresh copies; if watching, the new rules are picked up on the next poll.</summary>
    private void PushToEngine() => _engine.SetRules(_rules);

    /// <summary>Redraws the rows (after a theme change).</summary>
    public void RefreshRows() => RebuildRows();

    private void RebuildRows()
    {
        var sel = SelectedIndex;
        _grid.Rows.Clear();
        foreach (var r in _rules)
        {
            var i = _grid.Rows.Add(r.Enabled, r.Name, r.Mode == RuleMode.OnChange ? "(any)" : r.Color, "", "", "", r.PixelText, r.ModeText);
            var problems = r.Validate();
            if (problems.Count > 0)
            {
                _grid.Rows[i].DefaultCellStyle.ForeColor = Theme.ErrorText;
                foreach (DataGridViewCell c in _grid.Rows[i].Cells) c.ToolTipText = string.Join("\n", problems);
            }
        }
        if (sel >= 0) SelectRow(Math.Min(sel, _rules.Count - 1));
        UpdateButtons();
        UpdateLive();
    }

    private readonly Dictionary<int, Rgb?> _liveColors = new();

    private void UpdateLive()
    {
        if (!Visible || _grid.Rows.Count != _rules.Count) return;
        var watching = _engine.Watching;
        for (var i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            var state = _engine.GetState(i);
            Rgb? live;
            string stateText;
            if (watching && r.Enabled && state is not null)
            {
                live = state.LastColor;
                stateText = state.Running ? "running…"
                    : r.Validate().Count > 0 ? "skipped (invalid)"
                    : live is null ? "unreadable"
                    : state.LastCondition ? (r.Mode == RuleMode.OnChange ? "changing…" : "condition met")
                    : "waiting";
            }
            else
            {
                live = ReadNow(r);
                stateText = r.Mode == RuleMode.OnChange ? (watching ? "off" : "not watching")
                    : live is null ? "unreadable"
                    : Rgb.TryParse(r.Color, out var t) && Rgb.Matches(live.Value, t, r.Tolerance, r.Match) ? "matches" : "no match";
                if (!watching && r.Mode != RuleMode.OnChange) stateText += " (not watching)";
                else if (!r.Enabled) stateText = "off";
            }
            _liveColors[i] = live;
            var row = _grid.Rows[i];
            SetIfChanged(row.Cells[ColLive], live?.ToHex() ?? "—");
            SetIfChanged(row.Cells[ColState], stateText);
            SetIfChanged(row.Cells[ColFired], state?.LastFiredAt is { } at ? $"{at:HH:mm:ss} ({state.FireCount})" : "");
        }
    }

    private static void SetIfChanged(DataGridViewCell cell, string value)
    {
        if (!Equals(cell.Value, value)) cell.Value = value;
    }

    private static Rgb? ReadNow(PixelRule r)
    {
        int x = r.X, y = r.Y;
        if (r.Window is not null)
        {
            if (Native.ClientOrigin(Native.FindWindowByTitle(r.Window)) is not { } o) return null;
            x += o.X;
            y += o.Y;
        }
        return Native.ReadPixel(x, y);
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rules.Count || e.ColumnIndex is not (ColTarget or ColLive) || e.Graphics is null) return;
        Rgb? color = e.ColumnIndex == ColTarget
            ? (_rules[e.RowIndex].Mode != RuleMode.OnChange && Rgb.TryParse(_rules[e.RowIndex].Color, out var t) ? t : null)
            : _liveColors.GetValueOrDefault(e.RowIndex);
        e.PaintBackground(e.CellBounds, true);
        var b = e.CellBounds;
        var sw = new Rectangle(b.X + 4, b.Y + 4, b.Height - 8, b.Height - 8);
        if (color is { } c)
        {
            using var brush = new SolidBrush(PixelUi.ToColor(c));
            e.Graphics.FillRectangle(brush, sw);
            e.Graphics.DrawRectangle(Pens.Gray, sw);
        }
        var text = e.FormattedValue?.ToString() ?? "";
        var fore = (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle!.SelectionForeColor : e.CellStyle!.ForeColor;
        TextRenderer.DrawText(e.Graphics, text, e.CellStyle.Font, new Rectangle(sw.Right + 4, b.Y, b.Right - sw.Right - 4, b.Height),
            fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
