using System.ComponentModel;
using Echostep.Core;

namespace Echostep.UI;

/// <summary>Things the action list asks the main window to do.</summary>
internal enum EditorCommand { PlayFromSelected, PlayUntilSelected, PlaySelected, InsertRecording }

/// <summary>
/// The action list grid (#, Label, Action, Value, Comment) and everything that edits it: insert, edit,
/// edit all selected, in-grid labels and comments, delete, move, drag to reorder, cut/copy/paste,
/// undo/redo, enable/disable, breakpoints, groups, row colors, find (and replace) and go to line.
/// Rows with a bad label or a missing jump target are shown in red with the reason as a tooltip.
/// Used for the macro and for each pixel rule's actions.
/// </summary>
internal sealed class ActionListEditor : UserControl
{
    public static readonly (string Name, Func<MacroAction> Create)[] ActionTypes =
    [
        ("Click", () => new ClickAction()),
        ("Move", () => new MoveAction()),
        ("Wheel", () => new WheelAction()),
        ("Keypress", () => new KeyAction()),
        ("Wait", () => new WaitAction()),
        ("Wait until time", () => new WaitUntilAction()),
        ("Wait for pixel", () => new WaitPixelAction()),
        ("Wait for pixel change", () => new WaitPixelChangeAction()),
        ("Wait for screen change", () => new WaitScreenChangeAction()),
        ("If pixel", () => new IfPixelAction()),
        ("If time / day / window", () => new IfAction()),
        ("Repeat", () => new RepeatAction()),
        ("Goto", () => new GotoAction()),
        ("Window focus", () => new WindowFocusAction()),
        ("Find image", () => new FindImageAction()),
        ("Type text", () => new TypeTextAction()),
        ("Set variable", () => new SetVariableAction()),
        ("Run / open", () => new RunAction()),
        ("Message / sound", () => new MessageAction()),
    ];

    /// <summary>Actions under the More button.</summary>
    public static readonly string[] MoreActionNames = ["Find image", "Type text", "Set variable", "Run / open", "Message / sound"];

    /// <summary>The action types grouped for the Insert drop-downs.</summary>
    public static readonly (string Group, string[] Names)[] ActionGroups =
    [
        ("Mouse", ["Click", "Move", "Wheel"]),
        ("Keyboard", ["Keypress"]),
        ("Wait", ["Wait", "Wait until time", "Wait for pixel", "Wait for pixel change", "Wait for screen change"]),
        ("Logic", ["If time / day / window", "If pixel", "Repeat", "Goto", "Window focus"]),
    ];

    public static Func<MacroAction> Creator(string name) => ActionTypes.First(t => t.Name == name).Create;

    /// <summary>Fills a menu with one sub-menu per action group.</summary>
    public static void AddGroupedItems(ToolStripItemCollection items, Action<MacroAction> insert)
    {
        foreach (var (group, names) in ActionGroups)
        {
            var sub = new ToolStripMenuItem(group);
            foreach (var name in names)
            {
                var create = Creator(name);
                sub.DropDownItems.Add(name, null, (_, _) => insert(create()));
            }
            items.Add(sub);
        }
        var expert = new ToolStripMenuItem("More");
        foreach (var name in MoreActionNames)
        {
            var create = Creator(name);
            expert.DropDownItems.Add(name, null, (_, _) => insert(create()));
        }
        items.Add(expert);
    }

    private static readonly (string Name, string? Hex)[] RowColors =
    [
        ("None", null), ("Red", "#5A2A2E"), ("Orange", "#5A3F22"), ("Yellow", "#544B1F"), ("Green", "#24492F"),
        ("Teal", "#1F4A4A"), ("Blue", "#263F66"), ("Purple", "#44305E"), ("Grey", "#3D4150"),
    ];

    private const int CNum = 0, CLabel = 1, CAction = 2, CParams = 3, CComment = 4;

    private readonly DataGridView _grid;
    private readonly Button _edit, _delete, _up, _down;
    private readonly bool _isMain;
    private readonly HashSet<string> _collapsed = [];
    private readonly ToolStripMenuItem _miEnabled, _miGroupEnabled, _miBreakpoint, _miCollapse, _miEditAll;
    private List<MacroAction> _actions = [];
    private UndoHistory _history = new([]);
    private Dictionary<int, List<string>> _problems = new();
    private PlaybackFilter _filter = PlaybackFilter.All;
    private FindDialog? _findDialog;
    private int _playingRow = -1;
    private bool _readOnly, _showNumbers = true, _refreshing;
    private Point _dragStart;
    private List<int>? _dragRows, _draggedRows;

    /// <summary>Raised after any change to the list.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the selection (or the number of rows) changes.</summary>
    public event EventHandler? SelectionInfoChanged;

    /// <summary>Raised when a menu item or shortcut needs the main window (play from here, insert recording…).</summary>
    public event Action<EditorCommand>? CommandRequested;

    /// <param name="showInsertButton">Adds an Insert button beside the list (the main window has its own).</param>
    /// <param name="isMain">The macro list in the main window: enables play commands and remembers column widths.</param>
    public ActionListEditor(bool showInsertButton, bool isMain = false)
    {
        _isMain = isMain;
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ShowCellToolTips = true,
            StandardTab = true,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable, // Ctrl+C copies actions, not cell text
            EditMode = DataGridViewEditMode.EditProgrammatically,      // Label and Comment are edited in the cell
            AllowDrop = true,
        };
        DataGridViewTextBoxColumn Col(string name, string header, int width, bool readOnly) => new()
        {
            Name = name, HeaderText = header, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        _grid.Columns.Add(Col("Num", "#", 62, true));
        _grid.Columns.Add(Col("Label", "Label", 110, false));
        _grid.Columns.Add(Col("Action", "Action", 200, true));
        _grid.Columns.Add(Col("Params", "Value", 100, true));
        _grid.Columns.Add(Col("Comment", "Comment", 180, false));
        _grid.Columns[CNum].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        // Label, Action, Value and Comment share the width in proportion; drag a header divider to change it.
        GridColumns.Setup(_grid, 62, [110, 200, 250, 180],
            isMain ? Program.Settings.ColumnLayout : [],
            layout => { if (isMain) Program.Settings.ColumnLayout = layout; });

        _grid.CellDoubleClick += Grid_CellDoubleClick;
        _grid.CellEndEdit += Grid_CellEndEdit;
        _grid.CellPainting += Grid_CellPainting;
        _grid.EditingControlShowing += (_, e) =>
        {
            e.Control.BackColor = Theme.SurfaceAlt;
            e.Control.ForeColor = Theme.Text;
        };
        _grid.KeyDown += Grid_KeyDown;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            UpdateButtons();
            SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
        };
        _grid.MouseDown += Grid_MouseDown;
        _grid.MouseMove += Grid_MouseMove;
        _grid.DragOver += Grid_DragOver;
        _grid.DragDrop += Grid_DragDrop;

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 0, 0, 0),
            WrapContents = false,
        };
        // Compact icon buttons; names and shortcuts are in their tooltips.
        var tips = new ToolTip();
        Button Btn(Glyphs.Draw glyph, string tip, Action onClick, ButtonKind kind = ButtonKind.Normal, int gapAfter = 0)
        {
            var b = new IconButton(glyph, tip, tips, kind);
            if (gapAfter > 0) b.Margin = new Padding(0, 0, 0, gapAfter);
            b.Click += (_, _) => onClick();
            side.Controls.Add(b);
            return b;
        }

        if (showInsertButton)
        {
            var menu = Theme.Track(new ContextMenuStrip());
            AddGroupedItems(menu.Items, InsertNew);
            Button? insert = null;
            insert = Btn(Glyphs.PlusMenu, "Insert an action", () => menu.Show(insert!, new Point(0, insert!.Height)), ButtonKind.Primary, 14);
        }
        _edit = Btn(Glyphs.Pencil, "Edit (Enter)", EditSelected);
        _up = Btn(Glyphs.ArrowUp, "Move up (Alt+Up)", () => MoveSelected(-1));
        _down = Btn(Glyphs.ArrowDown, "Move down (Alt+Down)", () => MoveSelected(+1), gapAfter: 14);
        _delete = Btn(Glyphs.Trash, "Delete (Del)", DeleteSelected, ButtonKind.Danger, 14);
        Btn(Glyphs.Search, "Find (Ctrl+F)", () => OpenFind(false));

        // ---- Right-click menu ------------------------------------------------------------------
        var ctx = Theme.Track(new ContextMenuStrip());
        ToolStripMenuItem Item(string text, Action click, string? shortcut = null)
        {
            var mi = new ToolStripMenuItem(text, null, (_, _) => click());
            if (shortcut is not null) mi.ShortcutKeyDisplayString = shortcut;
            return mi;
        }
        void Sep() => ctx.Items.Add(new ToolStripSeparator());

        var insertMenu = new ToolStripMenuItem("Insert");
        AddGroupedItems(insertMenu.DropDownItems, InsertNew);
        ctx.Items.Add(insertMenu);
        ctx.Items.Add(Item("Edit…", EditSelected, "Enter"));
        ctx.Items.Add(_miEditAll = Item("Edit all selected…", EditAllSelected));
        Sep();
        ctx.Items.Add(_miEnabled = Item("Enabled", ToggleEnabled));
        ctx.Items.Add(_miGroupEnabled = Item("Disable group", ToggleGroupEnabled));
        ctx.Items.Add(_miBreakpoint = Item("Set breakpoint", ToggleBreakpoint, "F9"));
        ctx.Items.Add(Item("Remove all breakpoints", RemoveAllBreakpoints));
        if (isMain)
        {
            Sep();
            ctx.Items.Add(Item("Insert recording", () => CommandRequested?.Invoke(EditorCommand.InsertRecording)));
            ctx.Items.Add(Item("Play from selected", () => CommandRequested?.Invoke(EditorCommand.PlayFromSelected), "Alt+Shift+Down"));
            ctx.Items.Add(Item("Play until selected", () => CommandRequested?.Invoke(EditorCommand.PlayUntilSelected), "Alt+Shift+Up"));
            ctx.Items.Add(Item("Play selected row", () => CommandRequested?.Invoke(EditorCommand.PlaySelected), "Ctrl+Down"));
        }
        Sep();
        var rowColor = new ToolStripMenuItem("Background color");
        var groupColor = new ToolStripMenuItem("Group color");
        foreach (var (name, hex) in RowColors)
        {
            rowColor.DropDownItems.Add(name, null, (_, _) => SetRowColor(hex));
            groupColor.DropDownItems.Add(name, null, (_, _) => SetGroupColor(hex));
        }
        ctx.Items.Add(rowColor);
        ctx.Items.Add(groupColor);
        ctx.Items.Add(Item("Group", GroupSelected, "Ctrl+G"));
        ctx.Items.Add(Item("Ungroup", UngroupSelected, "Ctrl+Shift+G"));
        ctx.Items.Add(_miCollapse = Item("Collapse group", ToggleCollapse));
        Sep();
        ctx.Items.Add(Item("Go to line number…", GoToLine, "Alt+G"));
        ctx.Items.Add(Item("Find…", () => OpenFind(false), "Ctrl+F"));
        ctx.Items.Add(Item("Replace…", () => OpenFind(true), "Ctrl+H"));
        Sep();
        ctx.Items.Add(Item("Undo", Undo, "Ctrl+Z"));
        ctx.Items.Add(Item("Redo", Redo, "Ctrl+Shift+Z"));
        ctx.Items.Add(Item("Cut", CutSelected, "Ctrl+X"));
        ctx.Items.Add(Item("Copy", () => CopySelected(), "Ctrl+C"));
        ctx.Items.Add(Item("Paste", Paste, "Ctrl+V"));
        ctx.Items.Add(Item("Delete", DeleteSelected, "Del"));
        ctx.Items.Add(Item("Move up", () => MoveSelected(-1), "Alt+Up"));
        ctx.Items.Add(Item("Move down", () => MoveSelected(+1), "Alt+Down"));
        Sep();
        ctx.Items.Add(Item("Combine to click", CombineSelected));
        ctx.Items.Add(Item("Combine to press", CombineSelected));
        ctx.Opening += (_, e) =>
        {
            if (_readOnly) { e.Cancel = true; return; }
            var sel = SelectedIndices();
            var first = sel.Count > 0 ? _actions[sel[0]] : null;
            _miEnabled.Checked = first is { Disabled: false };
            _miEnabled.Enabled = first is not null;
            _miEditAll.Enabled = sel.Count > 1;
            _miBreakpoint.Text = first is { Breakpoint: true } ? "Remove breakpoint" : "Set breakpoint";
            var range = sel.Count > 0 ? MacroEditing.GroupRange(_actions, sel[0]) : null;
            _miGroupEnabled.Enabled = _miCollapse.Enabled = range is not null;
            _miGroupEnabled.Text = first is { Disabled: true } ? "Enable group" : "Disable group";
            _miCollapse.Text = first?.Group is { } g && _collapsed.Contains(g) ? "Expand group" : "Collapse group";
        };
        _grid.ContextMenuStrip = ctx;

        Controls.Add(_grid);
        Controls.Add(side);
        UpdateButtons();
    }

    // ===========================================================================================
    // Public surface
    // ===========================================================================================

    /// <summary>The list being edited (the editor changes it in place).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<MacroAction> Actions
    {
        get => _actions;
        set
        {
            _actions = value;
            _history = new UndoHistory(_actions); // a new list (New, Open) starts with nothing to undo
            _collapsed.Clear();
            RefreshRows();
            SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Disables editing (while playing).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnlyMode
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            if (value && _grid.IsCurrentCellInEditMode) _grid.CancelEdit();
            UpdateButtons();
        }
    }

    /// <summary>Row after which new actions go: below the selected row, or at the end.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int InsertIndex
    {
        get
        {
            var sel = SelectedIndices();
            return sel.Count > 0 ? sel.Max() + 1 : _actions.Count;
        }
    }

    public int SelectedCount => _grid.SelectedRows.Count;

    /// <summary>The first selected row, or -1.</summary>
    public int FirstSelected => SelectedIndices() is { Count: > 0 } s ? s[0] : -1;

    /// <summary>Action types unticked in the playback filter are hidden here (and skipped when playing).</summary>
    public void SetFilter(PlaybackFilter filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        RefreshRows();
    }

    public void SetShowLineNumbers(bool show)
    {
        if (_showNumbers == show) return;
        _showNumbers = show;
        _grid.Columns[CNum].Width = show ? Math.Max(62, _grid.Columns[CNum].Width) : 34;
        for (var i = 0; i < _grid.Rows.Count; i++) StyleRow(i);
    }

    public void InsertNew(MacroAction action)
    {
        if (_readOnly) return;
        var at = InsertIndex;
        using var dlg = new ActionEditDialog(action, _actions, -1);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _actions.Insert(at, dlg.Result);
        AfterChange(at, at);
        if (IsFilteredOut(dlg.Result))
            MessageBox.Show(FindForm(), "The action you just added is hidden by the playback filter.", "Echostep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Inserts several actions (e.g. a recording) at a row and selects them.</summary>
    public void InsertRange(int at, IReadOnlyList<MacroAction> actions)
    {
        if (actions.Count == 0) return;
        at = Math.Clamp(at, 0, _actions.Count);
        _actions.InsertRange(at, actions);
        AfterChange(at, at + actions.Count - 1);
    }

    /// <summary>Replaces every action (a new recording); one undo step brings the old list back.</summary>
    public void ReplaceAll(IReadOnlyList<MacroAction> actions)
    {
        _actions.Clear();
        _actions.AddRange(actions);
        _collapsed.Clear();
        AfterChange(0, -1);
    }

    /// <summary>Highlights the row being played (-1 for none) and scrolls to it.</summary>
    public void SetPlayingRow(int row)
    {
        if (row == _playingRow) return;
        var old = _playingRow;
        _playingRow = row;
        StyleRow(old);
        StyleRow(row);
        if (row >= 0 && row < _grid.Rows.Count && _grid.Rows[row].Visible)
        {
            var first = _grid.FirstDisplayedScrollingRowIndex;
            var visible = _grid.DisplayedRowCount(false);
            if (first >= 0 && (row < first || row >= first + visible))
            {
                try { _grid.FirstDisplayedScrollingRowIndex = row; }
                catch (InvalidOperationException) { /* row not scrollable right now */ }
            }
        }
    }

    public void SelectRow(int row)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;
        if (!_grid.Rows[row].Visible && _actions[row].Group is { } g && _collapsed.Remove(g)) RefreshRows();
        if (!_grid.Rows[row].Visible) return;
        _grid.ClearSelection();
        _grid.Rows[row].Selected = true;
        _grid.CurrentCell = _grid.Rows[row].Cells[CLabel];
    }

    public void FocusGrid() => _grid.Focus();

    // ===========================================================================================
    // Rows
    // ===========================================================================================

    /// <summary>Rebuilds the rows from the list and re-checks labels and targets.</summary>
    public void RefreshRows()
    {
        var selected = SelectedIndices();
        _refreshing = true;
        try
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var a in _actions) _grid.Rows.Add("", a.Label ?? "", "", LiveText.ValueText(a), a.Comment ?? "");
            _live.Clear();
            _problems = MacroValidator.Validate(_actions);
            _grid.CurrentCell = null; // a hidden row cannot be the current one
            for (var i = 0; i < _grid.Rows.Count; i++)
            {
                StyleRow(i);
                _grid.Rows[i].Visible = !IsHidden(i);
            }
            _grid.ResumeLayout();
            var keep = selected.Where(i => i < _grid.Rows.Count && _grid.Rows[i].Visible).ToList();
            if (keep.Count > 0) _grid.CurrentCell = _grid.Rows[keep[0]].Cells[CLabel];
            _grid.ClearSelection();
            foreach (var i in keep) _grid.Rows[i].Selected = true;
        }
        finally
        {
            _refreshing = false;
        }
        UpdateButtons();
    }

    private bool IsFilteredOut(MacroAction a) => a.Category != PlaybackFilter.None && !_filter.HasFlag(a.Category);

    private bool IsHidden(int i)
    {
        var a = _actions[i];
        if (IsFilteredOut(a)) return true;
        return a.Group is { } g && _collapsed.Contains(g) && MacroEditing.GroupRange(_actions, i) is { } r && r.First != i;
    }

    private static Color? ParseColor(string? hex) => Rgb.TryParse(hex, out var c) ? Theme.RowTint(PixelUi.ToColor(c)) : null;

    private void StyleRow(int i)
    {
        if (i < 0 || i >= _grid.Rows.Count || i >= _actions.Count) return;
        var row = _grid.Rows[i];
        var a = _actions[i];
        var bad = _problems.TryGetValue(i, out var msgs);
        row.DefaultCellStyle.BackColor = i == _playingRow ? Theme.PlayingBack : bad ? Theme.ErrorBack : ParseColor(a.RowColor) ?? Theme.Surface;
        row.DefaultCellStyle.ForeColor = bad ? Theme.ErrorText : a.Disabled ? Theme.Disabled : Theme.Text;

        var range = MacroEditing.GroupRange(_actions, i);
        var num = row.Cells[CNum];
        num.Value = (i == _playingRow ? "▶ " : "") + (a.Breakpoint ? "● " : "") + (_showNumbers ? (i + 1).ToString() : "");
        num.Style.ForeColor = a.Breakpoint ? Theme.Danger : Color.Empty;
        num.Style.BackColor = range is null || i == _playingRow ? Color.Empty : ParseColor(a.GroupColor) ?? Theme.SurfaceAlt;

        var text = a.DisplayName;
        if (range is { } r)
        {
            var collapsed = _collapsed.Contains(a.Group!);
            text = r.First == i
                ? (collapsed ? "▸ " : "▾ ") + text + (collapsed && r.Last > r.First ? $"   (+{r.Last - r.First} hidden)" : "")
                : "     " + text;
        }
        if (a.Disabled) text += "   (disabled)";
        row.Cells[CAction].Value = text;

        var tip = bad ? string.Join("\n", msgs!) : "";
        foreach (DataGridViewCell c in row.Cells) c.ToolTipText = tip;
        // Wait for pixel shows only coordinates and a swatch; the full description is in the tooltip.
        if (!bad && a is WaitPixelAction) row.Cells[CParams].ToolTipText = a.Parameters;
    }

    // ===========================================================================================
    // Live values while playing
    // ===========================================================================================

    private readonly Dictionary<int, string> _live = new();

    /// <summary>
    /// Shows what is counting during playback in the Value column (a wait or timeout counting down,
    /// a repeat's runs left…). Rows not in <paramref name="values"/> show their normal value again;
    /// null puts every row back (on stop).
    /// </summary>
    public void SetLiveValues(IReadOnlyDictionary<int, string>? values)
    {
        foreach (var row in _live.Keys.ToList())
        {
            if (values is not null && values.ContainsKey(row)) continue;
            _live.Remove(row);
            if (row < _grid.Rows.Count && row < _actions.Count)
            {
                if (_actions[row] is WaitPixelAction) _grid.InvalidateCell(CParams, row);
                else _grid[CParams, row].Value = LiveText.ValueText(_actions[row]);
            }
        }
        if (values is null) return;
        foreach (var (row, text) in values)
        {
            if (row >= _grid.Rows.Count || row >= _actions.Count) continue;
            if (_live.TryGetValue(row, out var old) && old == text) continue;
            _live[row] = text;
            if (_actions[row] is WaitPixelAction) _grid.InvalidateCell(CParams, row); // drawn on the swatch
            else _grid[CParams, row].Value = text;
        }
    }

    /// <summary>Wait for pixel's Value cell: coordinates, then the rest of the cell filled with the color it looks for.</summary>
    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != CParams || e.RowIndex >= _actions.Count || e.Graphics is null) return;
        if (_actions[e.RowIndex] is not WaitPixelAction p || !Rgb.TryParse(p.Color, out var target)) return;
        var style = e.CellStyle!;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);
        var b = e.CellBounds;
        var fore = selected ? style.SelectionForeColor : style.ForeColor;
        var text = e.FormattedValue?.ToString() ?? "";
        var font = style.Font ?? _grid.Font;
        var textWidth = TextRenderer.MeasureText(e.Graphics, text, font, b.Size, TextFormatFlags.NoPadding).Width;
        var pad = style.Padding.Left + 2;
        TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(b.X + pad, b.Y, b.Width - pad, b.Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var swatch = Rectangle.FromLTRB(b.X + pad + textWidth + 10, b.Y + 6, b.Right - 8, b.Bottom - 7);
        if (swatch.Width >= 12)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(swatch, 5))
            using (var brush = new SolidBrush(PixelUi.ToColor(target)))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            if (_live.TryGetValue(e.RowIndex, out var countdown))
            {
                // Black or white text, whichever reads better on the color.
                var light = target.R * 0.299 + target.G * 0.587 + target.B * 0.114 > 150;
                TextRenderer.DrawText(g, countdown, font, swatch, light ? Color.Black : Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
            }
        }
        e.Handled = true;
    }

    private List<int> SelectedIndices() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _actions.Count).OrderBy(i => i).ToList();

    private void UpdateButtons()
    {
        var sel = SelectedIndices();
        _edit.Enabled = !_readOnly && sel.Count >= 1;
        _delete.Enabled = !_readOnly && sel.Count > 0;
        _up.Enabled = !_readOnly && sel.Count > 0 && sel[0] > 0;
        _down.Enabled = !_readOnly && sel.Count > 0 && sel[^1] < _actions.Count - 1;
    }

    /// <summary>Refreshes, selects a range of rows, records an undo step and raises Changed.</summary>
    private void AfterChange(int selectFrom, int selectTo)
    {
        RefreshRows();
        _grid.ClearSelection();
        var first = -1;
        for (var i = Math.Max(0, selectFrom); i <= selectTo && i < _grid.Rows.Count; i++)
        {
            if (!_grid.Rows[i].Visible) continue;
            if (first < 0) first = i;
        }
        if (first >= 0) _grid.CurrentCell = _grid.Rows[first].Cells[CLabel];
        _grid.ClearSelection();
        for (var i = Math.Max(0, selectFrom); i <= selectTo && i < _grid.Rows.Count; i++)
            if (_grid.Rows[i].Visible) _grid.Rows[i].Selected = true;
        UpdateButtons();
        _history.Record(_actions);
        Changed?.Invoke(this, EventArgs.Empty);
        SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a change to each selected row, keeping the selection.</summary>
    private void ChangeSelected(Action<List<int>> change)
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count == 0) return;
        change(sel);
        var keep = sel.Where(i => i < _actions.Count).ToList();
        RefreshRows();
        _grid.ClearSelection();
        foreach (var i in keep.Where(i => _grid.Rows[i].Visible)) _grid.Rows[i].Selected = true;
        _history.Record(_actions);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ===========================================================================================
    // Keyboard and in-grid editing
    // ===========================================================================================

    private void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_grid.IsCurrentCellInEditMode) return;
        bool ctrl = e.Control && !e.Alt, alt = e.Alt && !e.Control;
        void Done() => e.Handled = e.SuppressKeyPress = true;

        if (ctrl && !e.Shift && e.KeyCode == Keys.C) { CopySelected(); Done(); return; } // allowed while playing too
        if (ctrl && !e.Shift && e.KeyCode == Keys.F) { OpenFind(false); Done(); return; }
        if (ctrl && !e.Shift && e.KeyCode == Keys.H && !_readOnly) { OpenFind(true); Done(); return; }
        if (alt && !e.Shift && e.KeyCode == Keys.G) { GoToLine(); Done(); return; }
        if (_readOnly) return;

        switch (e.KeyCode)
        {
            case Keys.Z when ctrl && !e.Shift: Undo(); Done(); break;
            case Keys.Y when ctrl && !e.Shift:
            case Keys.Z when ctrl && e.Shift: Redo(); Done(); break;
            case Keys.V when ctrl && !e.Shift: Paste(); Done(); break;
            case Keys.X when ctrl && !e.Shift: CutSelected(); Done(); break;
            case Keys.G when ctrl && !e.Shift: GroupSelected(); Done(); break;
            case Keys.G when ctrl && e.Shift: UngroupSelected(); Done(); break;
            case Keys.Enter when !e.Control && !e.Alt: EditSelected(); Done(); break;
            case Keys.F2: BeginCellEdit(CLabel); Done(); break;
            case Keys.F9: ToggleBreakpoint(); Done(); break;
            case Keys.Delete: DeleteSelected(); Done(); break;
            case Keys.Up when alt && !e.Shift: MoveSelected(-1); Done(); break;
            case Keys.Down when alt && !e.Shift: MoveSelected(+1); Done(); break;
            case Keys.Down when alt && e.Shift && _isMain: CommandRequested?.Invoke(EditorCommand.PlayFromSelected); Done(); break;
            case Keys.Up when alt && e.Shift && _isMain: CommandRequested?.Invoke(EditorCommand.PlayUntilSelected); Done(); break;
            case Keys.Down when ctrl && _isMain: CommandRequested?.Invoke(EditorCommand.PlaySelected); Done(); break;
        }
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _readOnly) return;
        if (e.ColumnIndex is CLabel or CComment) BeginCellEdit(e.ColumnIndex);
        else if (e.ColumnIndex == CNum && MacroEditing.GroupRange(_actions, e.RowIndex) is { } r && r.First == e.RowIndex) ToggleCollapse();
        else EditSelected();
    }

    private void BeginCellEdit(int column)
    {
        if (_readOnly || _grid.CurrentRow is not { } row || !row.Visible) return;
        _grid.CurrentCell = row.Cells[column];
        _grid.BeginEdit(true);
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var i = e.RowIndex;
        if (i < 0 || i >= _actions.Count || e.ColumnIndex is not (CLabel or CComment)) return;
        var text = (Convert.ToString(_grid[e.ColumnIndex, i].Value) ?? "").Trim();
        var a = _actions[i];
        string? problem = null;
        var changed = false;
        if (e.ColumnIndex == CComment)
        {
            changed = text != (a.Comment ?? "");
            if (changed) a.Comment = text.Length == 0 ? null : text;
        }
        else if (!string.Equals(text, a.Label ?? "", StringComparison.Ordinal))
        {
            problem = Labels.CheckCustomLabel(text, _actions, i);
            if (problem is null)
            {
                // Jumps that pointed at the old name follow the rename.
                if (a.Label is { Length: > 0 } old && text.Length > 0)
                    foreach (var other in _actions)
                        other.MapTargets(t => string.Equals(t.Trim(), old, StringComparison.OrdinalIgnoreCase) ? text : t);
                a.Label = text.Length == 0 ? null : text;
                changed = true;
            }
        }
        // Rebuilding the rows from inside an edit event is not allowed; do it right after.
        BeginInvoke(() =>
        {
            if (problem is not null) MessageBox.Show(FindForm(), problem, "Label", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (changed) AfterChange(i, i);
            else RefreshRows();
        });
    }

    // ===========================================================================================
    // Drag to reorder
    // ===========================================================================================

    private void Grid_MouseDown(object? sender, MouseEventArgs e)
    {
        _dragRows = null;
        if (e.Button != MouseButtons.Left || _readOnly || ModifierKeys != Keys.None) return;
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        _dragStart = e.Location;
        // Remember the selection: clicking a row of a multi-selection collapses it before the drag starts.
        var sel = SelectedIndices();
        _dragRows = sel.Contains(hit.RowIndex) ? sel : [hit.RowIndex];
    }

    private void Grid_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragRows is null || _readOnly || _grid.IsCurrentCellInEditMode) return;
        var size = SystemInformation.DragSize;
        if (Math.Abs(e.X - _dragStart.X) < size.Width && Math.Abs(e.Y - _dragStart.Y) < size.Height) return;
        var rows = _dragRows;
        _dragRows = null;
        _draggedRows = rows; // kept here; only a marker travels through the drag itself
        try { _grid.DoDragDrop("Echostep.Rows", DragDropEffects.Move); }
        finally { _draggedRows = null; }
    }

    private void Grid_DragOver(object? sender, DragEventArgs e) =>
        e.Effect = _draggedRows is not null && !_readOnly ? DragDropEffects.Move : DragDropEffects.None;

    private void Grid_DragDrop(object? sender, DragEventArgs e)
    {
        if (_readOnly || _draggedRows is not { Count: > 0 } rows) return;
        var p = _grid.PointToClient(new Point(e.X, e.Y));
        var hit = _grid.HitTest(p.X, p.Y);
        var target = hit.RowIndex >= 0 ? hit.RowIndex : _actions.Count; // dropping below the last row moves to the end
        if (rows.Contains(target)) return;
        var moving = rows.Where(i => i < _actions.Count).OrderBy(i => i).ToList();
        var block = moving.Select(i => _actions[i]).ToList();
        var insertAt = target - moving.Count(i => i < target);
        for (var k = moving.Count - 1; k >= 0; k--) _actions.RemoveAt(moving[k]);
        _actions.InsertRange(insertAt, block);
        AfterChange(insertAt, insertAt + block.Count - 1);
    }

    // ===========================================================================================
    // Edit, delete, move
    // ===========================================================================================

    public void EditSelected()
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count > 1) { EditAllSelected(); return; }
        if (sel.Count != 1) return;
        var i = sel[0];
        using var dlg = new ActionEditDialog(_actions[i], _actions, i);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _actions[i] = dlg.Result;
        AfterChange(i, i);
    }

    /// <summary>One dialog whose changes are applied to every selected action of the same type.</summary>
    public void EditAllSelected()
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count == 0) return;
        var type = _actions[sel[0]].GetType();
        var same = sel.Where(i => _actions[i].GetType() == type).ToList();
        var before = _actions[same[0]];
        using var dlg = new ActionEditDialog(before, _actions, same[0], bulk: same.Count > 1);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var after = dlg.Result;
        foreach (var i in same.Skip(1)) _actions[i] = MacroEditing.ApplyChanges(before, after, _actions[i]);
        _actions[same[0]] = after;
        RefreshRows();
        _grid.ClearSelection();
        foreach (var i in same.Where(i => _grid.Rows[i].Visible)) _grid.Rows[i].Selected = true;
        _history.Record(_actions);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count == 0 || !Dialogs.ConfirmDelete(FindForm(), sel.Count)) return;
        RemoveRows(sel);
    }

    private void RemoveRows(List<int> sel)
    {
        for (var k = sel.Count - 1; k >= 0; k--) _actions.RemoveAt(sel[k]);
        var next = Math.Min(sel[0], _actions.Count - 1);
        AfterChange(next, next);
    }

    /// <summary>Moves the selected rows (as a block) up or down by one.</summary>
    public void MoveSelected(int delta)
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count == 0) return;
        int first = sel[0], last = sel[^1];
        if (last - first + 1 != sel.Count) last = first; // non-contiguous selection: move the first row only
        if (delta < 0 && first == 0 || delta > 0 && last == _actions.Count - 1) return;
        var block = _actions.GetRange(first, last - first + 1);
        _actions.RemoveRange(first, block.Count);
        _actions.InsertRange(first + delta, block);
        AfterChange(first + delta, last + delta);
    }

    // ===========================================================================================
    // Undo, clipboard
    // ===========================================================================================

    /// <summary>Undoes the last change to the list (insert, edit, delete, move, paste, cut, recording…).</summary>
    public void Undo() { if (!_readOnly) Restore(_history.Undo(_actions)); }

    public void Redo() { if (!_readOnly) Restore(_history.Redo(_actions)); }

    private void Restore(bool changed)
    {
        if (!changed)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        RefreshRows();
        Changed?.Invoke(this, EventArgs.Empty);
        SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the selected rows to the clipboard (as .estep text). Returns false if nothing was copied.</summary>
    public bool CopySelected()
    {
        var sel = SelectedIndices();
        if (sel.Count == 0) return false;
        try
        {
            Clipboard.SetText(MacroClipboard.ToText(sel.Select(i => _actions[i])));
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            System.Media.SystemSounds.Beep.Play(); // another program is holding the clipboard
            return false;
        }
    }

    public void CutSelected()
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count > 0 && CopySelected()) RemoveRows(sel);
    }

    /// <summary>Pastes copied actions below the last selected row (or at the end) and selects them.</summary>
    public void Paste()
    {
        if (_readOnly) return;
        string? text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { text = null; }
        var pasted = MacroClipboard.FromText(text);
        if (pasted is null)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        MacroClipboard.PrepareForPaste(_actions, pasted);
        InsertRange(InsertIndex, pasted);
    }

    // ===========================================================================================
    // Enable/disable, breakpoints, colors, groups, combine
    // ===========================================================================================

    private void ToggleEnabled() => ChangeSelected(sel =>
    {
        var disable = !_actions[sel[0]].Disabled;
        foreach (var i in sel) _actions[i].Disabled = disable;
    });

    private void ToggleBreakpoint() => ChangeSelected(sel =>
    {
        var set = !_actions[sel[0]].Breakpoint;
        foreach (var i in sel) _actions[i].Breakpoint = set;
    });

    private void RemoveAllBreakpoints()
    {
        if (_readOnly || !_actions.Any(a => a.Breakpoint)) return;
        foreach (var a in _actions) a.Breakpoint = false;
        AfterChange(0, -1);
    }

    private void SetRowColor(string? hex) => ChangeSelected(sel =>
    {
        foreach (var i in sel) _actions[i].RowColor = hex;
    });

    private IEnumerable<int> GroupRows(List<int> sel) => sel
        .Select(i => MacroEditing.GroupRange(_actions, i)).Where(r => r is not null)
        .SelectMany(r => Enumerable.Range(r!.Value.First, r.Value.Last - r.Value.First + 1)).Distinct();

    private void SetGroupColor(string? hex) => ChangeSelected(sel =>
    {
        foreach (var i in GroupRows(sel).ToList()) _actions[i].GroupColor = hex;
    });

    private void ToggleGroupEnabled() => ChangeSelected(sel =>
    {
        var rows = GroupRows(sel).ToList();
        if (rows.Count == 0) return;
        var disable = !_actions[rows[0]].Disabled;
        foreach (var i in rows) _actions[i].Disabled = disable;
    });

    /// <summary>Wraps the selected rows (first to last) into one group.</summary>
    public void GroupSelected() => ChangeSelected(sel =>
    {
        var id = MacroEditing.NewGroupId(_actions);
        for (var i = sel[0]; i <= sel[^1]; i++)
        {
            _actions[i].Group = id;
            _actions[i].GroupColor = null;
        }
    });

    public void UngroupSelected() => ChangeSelected(sel =>
    {
        foreach (var i in GroupRows(sel).ToList())
        {
            if (_actions[i].Group is { } g) _collapsed.Remove(g);
            _actions[i].Group = null;
            _actions[i].GroupColor = null;
        }
    });

    /// <summary>Collapses or expands the group of the first selected row. Collapsed groups still run.</summary>
    private void ToggleCollapse()
    {
        var first = FirstSelected;
        if (first < 0 || _actions[first].Group is not { } g || MacroEditing.GroupRange(_actions, first) is not { } r) return;
        if (!_collapsed.Remove(g)) _collapsed.Add(g);
        RefreshRows();
        SelectRow(r.First);
    }

    /// <summary>Merges button or key Down + Up pairs among the selected rows into single Click / Keypress rows.</summary>
    private void CombineSelected()
    {
        if (_readOnly) return;
        var sel = SelectedIndices();
        if (sel.Count < 2) return;
        var merged = MacroEditing.CombineDownUp(_actions, sel);
        if (merged == 0)
        {
            MessageBox.Show(FindForm(), "Select a Down row and the matching Up row (same button or key) to combine them.",
                "Combine", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AfterChange(sel[0], sel[0]);
    }

    // ===========================================================================================
    // Go to line, find and replace
    // ===========================================================================================

    private void GoToLine()
    {
        if (_actions.Count == 0) return;
        var n = Dialogs.AskNumber(FindForm(), "Go to line", "Enter line number:", Math.Max(1, FirstSelected + 1), 1, _actions.Count);
        if (n is { } line) SelectRow(line - 1);
        _grid.Focus();
    }

    /// <summary>Opens the Find dialog, with or without its replace part.</summary>
    public void OpenFind(bool replace)
    {
        if (_findDialog is null || _findDialog.IsDisposed) _findDialog = new FindDialog(this);
        if (!_findDialog.Visible) _findDialog.Show(FindForm());
        _findDialog.Activate();
        _findDialog.SetReplaceMode(replace && !_readOnly);
    }

    /// <summary>Selects every row with a parameter containing the text; returns how many.</summary>
    public int SelectAllMatches(string find, FindField field)
    {
        var rows = Enumerable.Range(0, _actions.Count).Where(i => MacroEditing.Matches(_actions[i], field, find)).ToList();
        if (rows.Count == 0) return 0;
        foreach (var i in rows)
            if (!_grid.Rows[i].Visible && _actions[i].Group is { } g) _collapsed.Remove(g);
        RefreshRows();
        _grid.ClearSelection();
        var first = rows.FirstOrDefault(i => _grid.Rows[i].Visible, -1);
        if (first >= 0) _grid.CurrentCell = _grid.Rows[first].Cells[CLabel];
        _grid.ClearSelection();
        foreach (var i in rows.Where(i => _grid.Rows[i].Visible)) _grid.Rows[i].Selected = true;
        SelectionInfoChanged?.Invoke(this, EventArgs.Empty);
        return rows.Count;
    }

    /// <summary>Selects the next (or previous) row with a parameter containing the text. False if there is none.</summary>
    public bool FindNext(string find, FindField field, bool forward)
    {
        var n = _actions.Count;
        if (n == 0) return false;
        var start = FirstSelected;
        for (var step = 1; step <= n; step++)
        {
            var i = forward ? (Math.Max(start, -1) + step) % n : ((start < 0 ? 0 : start) - step + n * 2) % n;
            if (!MacroEditing.Matches(_actions[i], field, find)) continue;
            SelectRow(i);
            return true;
        }
        return false;
    }

    /// <summary>Replaces in the selected rows; returns how many parameters changed.</summary>
    public int ReplaceInSelected(string find, string replace, FindField field) => ReplaceIn(SelectedIndices(), find, replace, field);

    public int ReplaceAll(string find, string replace, FindField field) =>
        ReplaceIn(Enumerable.Range(0, _actions.Count).ToList(), find, replace, field);

    private int ReplaceIn(List<int> rows, string find, string replace, FindField field)
    {
        if (_readOnly) return 0;
        var total = 0;
        foreach (var i in rows)
        {
            if (MacroEditing.Replace(_actions[i], field, find, replace, out var count) is not { } changed) continue;
            _actions[i] = changed;
            total += count;
        }
        if (total > 0)
        {
            var keep = SelectedIndices();
            RefreshRows();
            _grid.ClearSelection();
            foreach (var i in keep.Where(i => i < _grid.Rows.Count && _grid.Rows[i].Visible)) _grid.Rows[i].Selected = true;
            _history.Record(_actions);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _findDialog?.Dispose();
        base.Dispose(disposing);
    }
}
