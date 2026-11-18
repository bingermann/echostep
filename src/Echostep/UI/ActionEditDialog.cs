using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>Edit dialog for one action. Fields depend on the action type.</summary>
internal sealed partial class ActionEditDialog : Form
{
    private readonly IReadOnlyList<MacroAction> _all;
    private readonly int _index;
    private readonly MacroAction _work;
    private readonly FieldGrid _grid = new();
    private readonly TextBox _label;
    private readonly List<Func<string?>> _validators = [];
    private readonly List<Action> _apply = [];
    private readonly ToolTip _tips = new();

    /// <summary>The edited copy; only meaningful after OK.</summary>
    public MacroAction Result => _work;

    /// <param name="all">The list the action belongs to (for label checks and target suggestions).</param>
    /// <param name="index">The action's row, or -1 for a new action.</param>
    /// <param name="bulk">Editing every selected action of this type at once (the label is left alone).</param>
    public ActionEditDialog(MacroAction action, IReadOnlyList<MacroAction> all, int index, bool bulk = false)
    {
        _all = all;
        _index = index;
        _work = action.Clone();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.Prepare(this);
        Text = bulk ? $"Edit all selected actions: {_work.DisplayName}"
            : index < 0 ? "Insert " + _work.DisplayName
            : $"Edit row {index + 1}: {_work.DisplayName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        BuildFields();

        _label = _grid.TextField("Label", _work.Label, 160);
        _tips.SetToolTip(_label,
            "Optional name other rows can jump to. Must be unique; start, end, stop, next and plain numbers are reserved.");
        var comment = _grid.TextField("Comment", _work.Comment, 300);
        if (bulk)
        {
            _label.Enabled = false;
            _grid.Note("Only the fields you change are applied to every selected action of this type. Labels stay as they are.");
        }
        else
        {
            _validators.Add(() => Labels.CheckCustomLabel(_label.Text, _all, _index));
            _apply.Add(() => _work.Label = string.IsNullOrWhiteSpace(_label.Text) ? null : _label.Text.Trim());
        }
        _apply.Add(() => _work.Comment = string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim());

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 0), WrapContents = false,
        };
        var ok = new RoundButton { Text = "OK", Kind = ButtonKind.Primary, AutoSize = true, MinimumSize = new Size(100, 0) };
        var cancel = new RoundButton { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 0) };
        ok.Click += (_, _) => TryAccept();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        // One auto-sized column: fields, then OK/Cancel on the right. The dialog sizes itself to fit.
        var layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Location = new Point(Padding.Left, Padding.Top), Margin = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_grid, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        Theme.Apply(this);
        ResumeLayout(false);
        PerformLayout();
    }

    private void TryAccept()
    {
        foreach (var v in _validators)
        {
            var problem = v();
            if (problem is null) continue;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        foreach (var a in _apply) a();
        DialogResult = DialogResult.OK;
        Close();
    }

    private IEnumerable<string> TargetSuggestions(bool includeStop)
    {
        yield return Labels.Next;
        yield return Labels.Start;
        yield return Labels.End;
        if (includeStop) yield return Labels.Stop;
        foreach (var a in _all)
            if (!string.IsNullOrWhiteSpace(a.Label) && !Labels.IsReserved(a.Label))
                yield return a.Label.Trim();
    }

    private ComboBox TargetField(string caption, string? value, bool includeStop, bool allowEmpty, string tip)
    {
        var items = TargetSuggestions(includeStop).ToList();
        var combo = _grid.Combo(caption, items, string.IsNullOrWhiteSpace(value) && allowEmpty ? Labels.Next : value ?? "", editable: true, width: 170);
        _tips.SetToolTip(combo, tip + " Choose a label, start, end, next (the following row) or type a row number.");
        if (!allowEmpty)
            _validators.Add(() => string.IsNullOrWhiteSpace(combo.Text) ? $"{caption}: choose where to go." : null);
        return combo;
    }

    /// <summary>A "go to" value for an optional branch: empty or "next" both mean the following row.</summary>
    /// <summary>Hour, minute and second boxes on one row; returns the row and a function giving "HH:mm[:ss]".</summary>
    private (FlowLayoutPanel Row, Func<string> Get) TimeRow(string caption, string? value, TimeOnly fallback)
    {
        if (!WaitUntilAction.TryParseTime(value, out var t)) t = fallback;
        var hour = FieldGrid.NewInt(t.Hour, 0, 23, 56);
        var minute = FieldGrid.NewInt(t.Minute, 0, 59, 56);
        var second = FieldGrid.NewInt(t.Second, 0, 59, 56);
        var row = _grid.Row(caption, hour, new Label { Text = "h", AutoSize = true }, minute,
            new Label { Text = "min", AutoSize = true }, second, new Label { Text = "s", AutoSize = true });
        return (row, () => WaitUntilAction.Format(new TimeOnly(hour.Value, minute.Value, second.Value)));
    }

    private static string? Branch(ComboBox combo)
    {
        var t = combo.Text.Trim();
        return t.Length == 0 || t.Equals(Labels.Next, StringComparison.OrdinalIgnoreCase) ? null : t;
    }

    private (TimeoutValue Timeout, ComboBox OnTimeout) TimeoutFields(int timeoutMs, string onTimeout, string what)
    {
        var field = _grid.Duration("Keep waiting for", timeoutMs);
        field.SetTip(_tips, "How long to wait before giving up. 0 waits forever (until Stop).");
        _validators.Add(() => field.Problem("how long to keep waiting"));
        var timeout = new TimeoutValue(field);
        var target = TargetField("…and then go to", onTimeout, includeStop: true, allowEmpty: false, $"Where to go when {what}.");
        return (timeout, target);
    }

    private void BuildFields()
    {
        switch (_work)
        {
            case ClickAction c:
            {
                _grid.Note("Clicks a mouse button at a screen position. Use Pick on screen to choose the position.");
                var button = _grid.Combo("Mouse button", ["Left", "Right", "Middle", "X1 (back)", "X2 (forward)"], null, false, 170);
                button.SelectedIndex = (int)c.Button;
                var kind = _grid.Combo("Action", ["Click", "Double-click", "Down (press and hold)", "Up (release)"], null, false, 170);
                kind.SelectedIndex = (int)c.Kind;
                var relative = _grid.Add("", new CheckBox { Text = "Relative to the current mouse position", AutoSize = true, Checked = c.Relative });
                var (x, y, window) = PointFields(c, pixel: null);
                var wiggle = _grid.Add("", new CheckBox { Text = "Random wiggle (a small random offset each time)", AutoSize = true, Checked = c.Wiggle });
                _tips.SetToolTip(relative, "X and Y become a distance from wherever the mouse is when the action runs.");
                _apply.Add(() =>
                {
                    c.X = x.Value; c.Y = y.Value; c.Window = relative.Checked ? null : NullIfBlank(window.Text);
                    c.Button = (MouseButton)button.SelectedIndex;
                    c.Kind = (ClickKind)kind.SelectedIndex;
                    c.Relative = relative.Checked;
                    c.Wiggle = wiggle.Checked;
                });
                break;
            }
            case MoveAction m:
            {
                _grid.Note("Moves the mouse to a point. Give it a start point and a duration to glide from one place to another.");
                var useStart = new CheckBox { Text = "Start from", AutoSize = true, Checked = m.FromX is not null && m.FromY is not null };
                var fx = FieldGrid.NewInt(m.FromX ?? 0, -100_000, 100_000, 84);
                var fy = FieldGrid.NewInt(m.FromY ?? 0, -100_000, 100_000, 84);
                var pickStart = new RoundButton { Text = "Pick start…", AutoSize = true };
                _grid.Row("", useStart, fx, fy, pickStart);
                var (x, y, window) = PointFields(m, pixel: null, caption: "End X, Y");
                var duration = _grid.Duration("Duration", m.TravelMs, 0, 999_999);
                _validators.Add(() => duration.Problem("the duration"));
                var relative = _grid.Add("", new CheckBox { Text = "Relative (X and Y are a distance from the current mouse position)", AutoSize = true, Checked = m.Relative });
                duration.SetTip(_tips, "How long the pointer takes to get there. 0 jumps straight to the point.");
                void Sync() => fx.Enabled = fy.Enabled = pickStart.Enabled = useStart.Checked && !relative.Checked;
                useStart.CheckedChanged += (_, _) => Sync();
                relative.CheckedChanged += (_, _) => Sync();
                Sync();
                pickStart.Click += (_, _) => PixelUi.Pick(this, false, window.Text, (px, py, _, _) => { fx.Value = px; fy.Value = py; });
                _apply.Add(() =>
                {
                    m.X = x.Value; m.Y = y.Value; m.Window = relative.Checked ? null : NullIfBlank(window.Text);
                    var start = useStart.Checked && !relative.Checked;
                    m.FromX = start ? fx.Value : null;
                    m.FromY = start ? fy.Value : null;
                    m.TravelMs = (int)(duration.Ms ?? m.TravelMs);
                    m.Relative = relative.Checked;
                });
                break;
            }
            case WheelAction w:
            {
                _grid.Note("Scrolls the mouse wheel. Positive values scroll up (or right); 120 is one notch.");
                var orientation = _grid.Combo("Wheel orientation", ["Vertical", "Horizontal"], null, false, 170);
                orientation.SelectedIndex = w.Horizontal ? 1 : 0;
                var value = _grid.Int("Value", w.Value, -23_400, 23_400, 110);
                _apply.Add(() => { w.Horizontal = orientation.SelectedIndex == 1; w.Value = value.Value; });
                break;
            }
            case KeyAction k:
            {
                _grid.Note("Simulates a key press.");
                var option = _grid.Combo("Key press option", ["Press and release", "Down (press and hold)", "Up (release)"], null, false, 200);
                option.SelectedIndex = (int)k.Option;
                var key = _grid.Combo("Key", KeyNames.AllNames, k.Key, editable: true, width: 200);
                var ctrl = new CheckBox { Text = "Ctrl", AutoSize = true, Checked = k.Ctrl };
                var alt = new CheckBox { Text = "Alt", AutoSize = true, Checked = k.Alt };
                var shift = new CheckBox { Text = "Shift", AutoSize = true, Checked = k.Shift };
                var win = new CheckBox { Text = "Win", AutoSize = true, Checked = k.Win };
                var modifiers = _grid.Row("Modifiers", ctrl, alt, shift, win);
                var capture = _grid.Add("Or press it", new KeyCaptureBox());
                var count = _grid.Int("Count", k.Count, 1, 65_535, 80);
                capture.Captured += (vk, mods) =>
                {
                    key.Text = KeyNames.GetName(vk);
                    ctrl.Checked = mods.HasFlag(Modifiers.Ctrl);
                    alt.Checked = mods.HasFlag(Modifiers.Alt);
                    shift.Checked = mods.HasFlag(Modifiers.Shift);
                };
                void Sync() => modifiers.Enabled = count.Enabled = option.SelectedIndex == 0;
                option.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                _validators.Add(() => KeyNames.TryGetVk(key.Text, out _) ? null : $"\"{key.Text}\" is not a key name. Pick one from the list or press it in the box.");
                _apply.Add(() =>
                {
                    k.Key = KeyNames.Normalize(key.Text);
                    k.Option = (KeyOption)option.SelectedIndex;
                    var press = k.Option == KeyOption.Press;
                    k.Ctrl = press && ctrl.Checked; k.Alt = press && alt.Checked; k.Shift = press && shift.Checked; k.Win = press && win.Checked;
                    k.Count = press ? count.Value : 1;
                });
                break;
            }
            case WaitAction w:
            {
                _grid.Note("Pauses playback for a set time.");
                var amount = new TextBox { Text = WaitAction.FormatAmount(w.Amount), Width = 110, TextAlign = HorizontalAlignment.Right };
                var unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
                unit.Items.AddRange(["milliseconds", "seconds", "minutes", "hours"]);
                unit.SelectedIndex = (int)w.Unit;
                _grid.Row("Wait for", amount, unit);
                _tips.SetToolTip(amount, "How long to pause. Decimals are fine, for example 1.5 minutes.");
                _validators.Add(() => WaitAction.ToMs(amount.Text, (WaitUnit)unit.SelectedIndex) is null
                    ? "Enter how long to wait as a number that is 0 or more (at most about 24 days)." : null);
                _apply.Add(() =>
                {
                    w.Unit = (WaitUnit)unit.SelectedIndex;
                    w.Ms = WaitAction.ToMs(amount.Text, w.Unit) ?? w.Ms;
                });
                break;
            }
            case WaitUntilAction wu:
            {
                if (!WaitUntilAction.TryParseTime(wu.Time, out var t)) t = new TimeOnly(9, 0);
                var hour = FieldGrid.NewInt(t.Hour, 0, 23, 56);
                var minute = FieldGrid.NewInt(t.Minute, 0, 59, 56);
                var second = FieldGrid.NewInt(t.Second, 0, 59, 56);
                _grid.Row("Time (24-hour)", hour, new Label { Text = "h", AutoSize = true }, minute,
                    new Label { Text = "min", AutoSize = true }, second, new Label { Text = "s", AutoSize = true });
                _grid.Note("Uses this PC's clock. If the time has already passed today, the macro waits until that time tomorrow.", 360);
                _apply.Add(() => wu.Time = WaitUntilAction.Format(new TimeOnly(hour.Value, minute.Value, second.Value)));
                break;
            }
            case WaitPixelAction wp:
            {
                _grid.Note("Waits until a pixel on screen shows a specific color.");
                var color = new ColorField(wp.Color);
                var (x, y, window) = PointFields(wp, color);
                _grid.Add("Color", color);
                var (tol, match) = ToleranceFields(wp);
                var then = TargetField("If the color shows up, go to", wp.Then, includeStop: true, allowEmpty: true, "Where to go once the color is there.");
                _grid.Header("If the color does not show up");
                var (timeout, onTimeout) = TimeoutFields(wp.TimeoutMs, wp.OnTimeout, "the color has not appeared in time");
                AddTestRow(x, y, window, color, tol, match);
                _validators.Add(() => color.IsValid ? null : "Enter a color like #2D7DD2, or use Pick.");
                _apply.Add(() =>
                {
                    wp.X = x.Value; wp.Y = y.Value; wp.Window = NullIfBlank(window.Text);
                    wp.Color = color.Value; wp.Tolerance = tol.Value; wp.Match = (MatchMode)match.SelectedIndex;
                    wp.Then = Branch(then);
                    wp.TimeoutMs = timeout.Value; wp.OnTimeout = onTimeout.Text.Trim();
                });
                break;
            }
            case WaitPixelChangeAction wc:
            {
                _grid.Note("Waits until a pixel changes from the color it has when this step starts.");
                var (x, y, window) = PointFields(wc, pixel: null);
                var (tol, match) = ToleranceFields(wc);
                _tips.SetToolTip(tol, "Continue once the color differs from its color at the start by more than this.");
                var then = TargetField("If it changes, go to", wc.Then, includeStop: true, allowEmpty: true, "Where to go once the pixel has changed.");
                _grid.Header("If it does not change");
                var (timeout, onTimeout) = TimeoutFields(wc.TimeoutMs, wc.OnTimeout, "nothing changed in time");
                AddTestRow(x, y, window, null, tol, match);
                _apply.Add(() =>
                {
                    wc.X = x.Value; wc.Y = y.Value; wc.Window = NullIfBlank(window.Text);
                    wc.Tolerance = tol.Value; wc.Match = (MatchMode)match.SelectedIndex;
                    wc.Then = Branch(then);
                    wc.TimeoutMs = timeout.Value; wc.OnTimeout = onTimeout.Text.Trim();
                });
                break;
            }
            case WaitScreenChangeAction ws:
            {
                _grid.Note("Waits until something changes inside a zone or on the entire screen. The area is captured when this step starts and compared with every later frame.");
                var where = _grid.Combo("Where to watch", ["Entire screen", "Zone"], null, false, 180);
                where.SelectedIndex = ws.EntireScreen ? 0 : 1;
                var zx = FieldGrid.NewInt(ws.X, -100_000, 100_000, 70);
                var zy = FieldGrid.NewInt(ws.Y, -100_000, 100_000, 70);
                var zw = FieldGrid.NewInt(ws.Width, 0, 100_000, 70);
                var zh = FieldGrid.NewInt(ws.Height, 0, 100_000, 70);
                var zoneRow = _grid.Row("Zone X, Y, W, H", zx, zy, zw, zh);
                var define = new RoundButton { Text = "Define zone…", AutoSize = true };
                var show = new RoundButton { Text = "Show zone", AutoSize = true };
                var zoneButtons = _grid.Row("", define, show);
                var tol = _grid.Int("Ignore differences up to", ws.Tolerance, 0, 255, 70);
                _tips.SetToolTip(tol, "A pixel counts as changed when any color channel differs by more than this (0–255). A little tolerance ignores video noise.");
                _tips.SetToolTip(define, "Dims the screen so you can drag a rectangle around the area to watch.");
                _grid.Header("If a change is found");
                var mouse = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
                mouse.Items.AddRange(["No mouse action", "Move", "Left-click", "Right-click", "Middle-click", "Double-click"]);
                mouse.SelectedIndex = (int)ws.Mouse;
                var anchor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
                anchor.Items.AddRange(["Centered", "Top-left", "Top-right", "Bottom-left", "Bottom-right"]);
                anchor.SelectedIndex = (int)ws.Anchor;
                _grid.Row("Mouse action", mouse, new Label { Text = "on the changed area:", AutoSize = true }, anchor);
                var then = TargetField("Go to", ws.Then, includeStop: true, allowEmpty: true, "Where to go once something has changed.");
                _grid.Header("If no change is found");
                var (timeout, onTimeout) = TimeoutFields(ws.TimeoutMs, ws.OnTimeout, "nothing changed in time");
                void Sync()
                {
                    zoneRow.Enabled = zoneButtons.Enabled = where.SelectedIndex == 1;
                    anchor.Enabled = mouse.SelectedIndex > 0;
                }
                where.SelectedIndexChanged += (_, _) => Sync();
                mouse.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                define.Click += (_, _) =>
                {
                    var restore = PixelUi.HideWindows(this);
                    var zone = ZoneSelectForm.Pick();
                    restore();
                    if (zone is not { } r) return;
                    zx.Value = r.X; zy.Value = r.Y; zw.Value = r.Width; zh.Value = r.Height;
                };
                show.Click += (_, _) =>
                {
                    if (zw.Value > 0 && zh.Value > 0) ZoneFlash.Show(new Rectangle(zx.Value, zy.Value, zw.Value, zh.Value));
                };
                _validators.Add(() => where.SelectedIndex == 1 && (zw.Value <= 0 || zh.Value <= 0)
                    ? "Define the zone to watch (its width and height must be more than 0), or choose Entire screen." : null);
                _apply.Add(() =>
                {
                    var zone = where.SelectedIndex == 1;
                    ws.X = zone ? zx.Value : 0; ws.Y = zone ? zy.Value : 0;
                    ws.Width = zone ? zw.Value : 0; ws.Height = zone ? zh.Value : 0;
                    ws.Tolerance = tol.Value;
                    ws.Mouse = (ChangeMouseAction)mouse.SelectedIndex;
                    ws.Anchor = (ChangeAnchor)anchor.SelectedIndex;
                    ws.Then = Branch(then);
                    ws.TimeoutMs = timeout.Value; ws.OnTimeout = onTimeout.Text.Trim();
                });
                break;
            }
            case IfAction cond:
            {
                _grid.Note("Checks something once and goes one way if it is true, another way if it is not. " +
                           "Leave a target empty to continue with the next row.");
                var kinds = new List<string> { "Time of day", "Day of the week", "Window" };
                kinds.Add("Variable");
                var kind = _grid.Combo("Check", kinds, null, editable: false, width: 200);
                kind.SelectedIndex = (int)cond.Condition;

                // Time of day
                var compare = _grid.Combo("The time is", ["before", "after", "between"], null, editable: false, width: 120);
                compare.SelectedIndex = (int)(cond.Compare ?? TimeCompare.After);
                var (timeRow, getTime) = TimeRow("Time (24-hour)", cond.Time, new TimeOnly(17, 0));
                var (time2Row, getTime2) = TimeRow("and", cond.Time2, new TimeOnly(23, 59));
                var timeNote = _grid.Note("Uses this PC's clock. \"After\" includes the time itself. \"Between\" includes the first time and " +
                                          "ends just before the second; a range like 22:00 to 06:00 runs past midnight.", 420);

                // Day of the week
                var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
                var dayBoxes = dayNames.Select((d, i) => new CheckBox
                {
                    Text = d, AutoSize = true,
                    Checked = cond.Days?.Contains(IfAction.DayKeys[i], StringComparer.OrdinalIgnoreCase) ?? i < 5,
                }).ToArray();
                var daysRow = _grid.Row("Today is", dayBoxes.Cast<Control>().ToArray());
                RoundButton Quick(string text, Func<int, bool> on)
                {
                    var b = new RoundButton { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
                    b.Click += (_, _) => { for (var i = 0; i < 7; i++) dayBoxes[i].Checked = on(i); };
                    return b;
                }
                var quickRow = _grid.Row("", Quick("Weekdays", i => i < 5), Quick("Weekend", i => i >= 5), Quick("Every day", _ => true));

                // Window
                var title = new TextBox { Text = cond.Title ?? "", Width = 260 };
                var pickWindow = new RoundButton { Text = "Pick window…", AutoSize = true };
                var titleRow = _grid.Row("Window title", title, pickWindow);
                var titleMatch = _grid.Combo("Title", ["is exactly", "begins with", "ends with", "contains", "(any window)"], null, editable: false, width: 160);
                titleMatch.SelectedIndex = (int)(cond.TitleMatch ?? TitleMatch.Contains);
                var state = _grid.Combo("The window", ["is open", "is in front"], null, editable: false, width: 160);
                state.SelectedIndex = (int)(cond.State ?? WindowCondition.Open);
                _tips.SetToolTip(state, "Open: any window with that title exists (minimized counts). In front: it is the active window.");
                pickWindow.Click += (_, _) => PixelUi.Pick(this, true, null, (_, _, _, picked) =>
                {
                    if (picked is { Length: > 0 }) title.Text = picked;
                });
                _tips.SetToolTip(pickWindow, "Point at a window and press Enter to take its title (Esc cancels).");

                // Variable
                var varName = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150, Text = cond.Variable ?? "count" };
                varName.Items.AddRange(VariableSuggestions().Cast<object>().ToArray());
                var varCompare = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
                varCompare.Items.AddRange(["=", "≠", "<", ">", "contains"]);
                varCompare.SelectedIndex = (int)(cond.VarCompare ?? VarCompare.Equal);
                var varValue = new TextBox { Text = cond.Value ?? "", Width = 150 };
                var varRow = _grid.Row("Variable", varName, varCompare, varValue);
                var varNote = _grid.Note("Numbers compare as numbers, anything else as text (not case-sensitive). The value may use {other} variables.", 420);

                var then = TargetField("If true, go to", cond.Then, includeStop: true, allowEmpty: true, "Where to go when the condition is true. Empty: the next row.");
                var els = TargetField("Otherwise, go to", cond.Else, includeStop: true, allowEmpty: true, "Where to go when it is false. Empty: the next row.");

                var test = new RoundButton { Text = "Test now", AutoSize = true };
                var result = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
                _grid.Row("", test, result);

                IfAction Build() => new()
                {
                    Condition = (IfCondition)kind.SelectedIndex,
                    Compare = kind.SelectedIndex == 0 ? (TimeCompare)compare.SelectedIndex : null,
                    Time = kind.SelectedIndex == 0 ? getTime() : null,
                    Time2 = kind.SelectedIndex == 0 && compare.SelectedIndex == 2 ? getTime2() : null,
                    Days = kind.SelectedIndex == 1 ? IfAction.DayKeys.Where((_, i) => dayBoxes[i].Checked).ToList() : null,
                    Title = kind.SelectedIndex == 2 && title.Text.Trim().Length > 0 ? title.Text.Trim() : null,
                    TitleMatch = kind.SelectedIndex == 2 ? (TitleMatch)titleMatch.SelectedIndex : null,
                    State = kind.SelectedIndex == 2 ? (WindowCondition)state.SelectedIndex : null,
                    Variable = kind.SelectedIndex == 3 ? varName.Text.Trim() : null,
                    VarCompare = kind.SelectedIndex == 3 ? (VarCompare)varCompare.SelectedIndex : null,
                    Value = kind.SelectedIndex == 3 ? varValue.Text : null,
                };
                test.Click += (_, _) =>
                {
                    var c = Build();
                    if (c.Problems().FirstOrDefault() is { } p) { result.Text = p; return; }
                    if (c.Condition == IfCondition.Variable) { result.Text = "Variables only have values while the macro plays."; return; }
                    var yes = c.Evaluate(DateTime.Now, Echostep.Win.WinScreen.Instance);
                    result.Text = $"Right now: {(yes ? "TRUE" : "false")} → goes to {(yes ? Branch(then) : Branch(els)) ?? "the next row"}.";
                };

                void Show(Control c, bool on)
                {
                    c.Visible = on;
                    if (_grid.GetControlFromPosition(0, _grid.GetRow(c)) is Label caption && caption != c) caption.Visible = on;
                }
                void Sync()
                {
                    int k = kind.SelectedIndex, cmp = compare.SelectedIndex;
                    Show(compare, k == 0); Show(timeRow, k == 0); Show(time2Row, k == 0 && cmp == 2); Show(timeNote, k == 0);
                    Show(daysRow, k == 1); Show(quickRow, k == 1);
                    Show(titleRow, k == 2); Show(titleMatch, k == 2); Show(state, k == 2);
                    Show(varRow, k == 3); Show(varNote, k == 3);
                    title.Enabled = pickWindow.Enabled = titleMatch.SelectedIndex != (int)TitleMatch.Any;
                    result.Text = "";
                }
                kind.SelectedIndexChanged += (_, _) => Sync();
                compare.SelectedIndexChanged += (_, _) => Sync();
                titleMatch.SelectedIndexChanged += (_, _) => Sync();
                Sync();

                _validators.Add(() => Build().Problems().FirstOrDefault());
                _apply.Add(() =>
                {
                    var c = Build();
                    cond.Condition = c.Condition; cond.Compare = c.Compare; cond.Time = c.Time; cond.Time2 = c.Time2;
                    cond.Days = c.Days; cond.Title = c.Title; cond.TitleMatch = c.TitleMatch; cond.State = c.State;
                    cond.Variable = c.Variable; cond.VarCompare = c.VarCompare; cond.Value = c.Value;
                    cond.Then = Branch(then); cond.Else = Branch(els);
                });
                break;
            }
            case IfPixelAction ip:
            {
                _grid.Note("Checks a pixel once and goes one way if it has the color and another way if it does not.");
                var color = new ColorField(ip.Color);
                var (x, y, window) = PointFields(ip, color);
                _grid.Add("Color", color);
                var (tol, match) = ToleranceFields(ip);
                var then = TargetField("If it matches, go to", ip.Then, includeStop: true, allowEmpty: true, "");
                var els = TargetField("Otherwise, go to", ip.Else, includeStop: true, allowEmpty: true, "");
                AddTestRow(x, y, window, color, tol, match);
                _validators.Add(() => color.IsValid ? null : "Enter a color like #2D7DD2, or use Pick.");
                _apply.Add(() =>
                {
                    ip.X = x.Value; ip.Y = y.Value; ip.Window = NullIfBlank(window.Text);
                    ip.Color = color.Value; ip.Tolerance = tol.Value; ip.Match = (MatchMode)match.SelectedIndex;
                    ip.Then = Branch(then); ip.Else = Branch(els);
                });
                break;
            }
            case RepeatAction r:
            {
                _grid.Note("Jumps back to the chosen label until the repeat condition is met.");
                var target = TargetField("Jump back to", r.Target, includeStop: false, allowEmpty: false,
                    "The first row of the block to repeat.");
                var mode = _grid.Combo("Repeat", ["a number of times", "for a length of time", "until a time of day", "forever (until Stop)"], null, false, 220);
                mode.SelectedIndex = (int)r.Mode;
                var count = _grid.Int("Repetitions", r.Count, 1, 1_000_000, 110);
                _tips.SetToolTip(count, "How many times the block runs in total.");
                var seconds = _grid.Duration("For", (r.Seconds ?? 60) * 1000L, 1000, 1_000_000_000L);
                _validators.Add(() => mode.SelectedIndex == 1 ? seconds.Problem("how long to repeat") : null);
                if (!WaitUntilAction.TryParseTime(r.Until, out var t)) t = new TimeOnly(17, 0);
                var hour = FieldGrid.NewInt(t.Hour, 0, 23, 56);
                var minute = FieldGrid.NewInt(t.Minute, 0, 59, 56);
                var untilRow = _grid.Row("Until (24-hour)", hour, new Label { Text = "h", AutoSize = true }, minute, new Label { Text = "min", AutoSize = true });
                var after = TargetField("After finishing, jump to", r.After, includeStop: true, allowEmpty: true, "Where to go once the repeating is done.");
                void Sync()
                {
                    count.Enabled = mode.SelectedIndex == 0;
                    seconds.Enabled = mode.SelectedIndex == 1;
                    untilRow.Enabled = mode.SelectedIndex == 2;
                    after.Enabled = mode.SelectedIndex != 3;
                }
                mode.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                _apply.Add(() =>
                {
                    r.Target = target.Text.Trim();
                    r.Mode = (RepeatMode)mode.SelectedIndex;
                    r.Count = count.Value;
                    r.Seconds = r.Mode == RepeatMode.Seconds ? (int)Math.Max(1, Math.Round((seconds.Ms ?? 60_000) / 1000.0)) : null;
                    r.Until = r.Mode == RepeatMode.Until ? WaitUntilAction.Format(new TimeOnly(hour.Value, minute.Value)) : null;
                    r.After = r.Mode == RepeatMode.Infinite ? null : Branch(after);
                });
                break;
            }
            case GotoAction g:
            {
                _grid.Note("Jumps to the row with the chosen label.");
                var target = TargetField("Go to", g.Target, includeStop: true, allowEmpty: false,
                    "A backwards Goto loops until Stop.");
                _apply.Add(() => g.Target = target.Text.Trim());
                break;
            }
            case WindowFocusAction wf:
            {
                _grid.Note("Waits for a program window, brings it to the front, and can set its position and size. It never starts the program.");
                var program = new TextBox { Text = wf.Program ?? "", Width = 220 };
                var capture = new RoundButton { Text = "Capture…", AutoSize = true };
                _grid.Row("Program (exe)", program, capture);
                var match = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
                match.Items.AddRange(["Is exactly", "Begins with", "Ends with", "Contains", "Any"]);
                match.SelectedIndex = (int)wf.TitleMatch;
                var title = new TextBox { Text = wf.Title ?? "", Width = 260 };
                _grid.Row("Window title", match, title);
                _grid.Header("If the window can be focused");
                var setPos = new CheckBox { Text = "Set window position", AutoSize = true, Checked = wf.Left is not null && wf.Top is not null };
                var px = FieldGrid.NewInt(wf.Left ?? 0, -100_000, 100_000, 70);
                var py = FieldGrid.NewInt(wf.Top ?? 0, -100_000, 100_000, 70);
                _grid.Row("", setPos, px, py);
                var setSize = new CheckBox { Text = "Set window size", AutoSize = true, Checked = wf.Width is not null && wf.Height is not null };
                var sw = FieldGrid.NewInt(wf.Width ?? 800, 1, 100_000, 70);
                var sh = FieldGrid.NewInt(wf.Height ?? 600, 1, 100_000, 70);
                _grid.Row("", setSize, sw, sh);
                var then = TargetField("Go to", wf.Then, includeStop: true, allowEmpty: true, "Where to go once the window is in front.");
                _grid.Header("If the window cannot be focused");
                var (timeout, onTimeout) = TimeoutFields(wf.TimeoutMs, wf.OnTimeout, "the window has not appeared in time");
                _tips.SetToolTip(program, "File name of the program, for example notepad.exe. Leave empty for any program.");
                _tips.SetToolTip(capture, "Point at a window and press Enter to fill in its program, title, position and size.");
                void Sync()
                {
                    px.Enabled = py.Enabled = setPos.Checked;
                    sw.Enabled = sh.Enabled = setSize.Checked;
                    title.Enabled = match.SelectedIndex != (int)TitleMatch.Any;
                }
                setPos.CheckedChanged += (_, _) => Sync();
                setSize.CheckedChanged += (_, _) => Sync();
                match.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                capture.Click += (_, _) => PixelUi.PickWindow(this, (handle, windowTitle) =>
                {
                    program.Text = Native.ProgramOf(handle) ?? "";
                    title.Text = windowTitle;
                    if (Native.GetWindowRect(handle, out var rect))
                    {
                        px.Value = rect.Left; py.Value = rect.Top;
                        sw.Value = rect.Right - rect.Left; sh.Value = rect.Bottom - rect.Top;
                    }
                });
                _validators.Add(() => match.SelectedIndex != (int)TitleMatch.Any && string.IsNullOrWhiteSpace(title.Text) && string.IsNullOrWhiteSpace(program.Text)
                    ? "Enter a program or a window title (or use Capture)." : null);
                _apply.Add(() =>
                {
                    wf.Program = NullIfBlank(program.Text);
                    wf.TitleMatch = (TitleMatch)match.SelectedIndex;
                    wf.Title = NullIfBlank(title.Text);
                    wf.Left = setPos.Checked ? px.Value : null; wf.Top = setPos.Checked ? py.Value : null;
                    wf.Width = setSize.Checked ? sw.Value : null; wf.Height = setSize.Checked ? sh.Value : null;
                    wf.Then = Branch(then);
                    wf.TimeoutMs = timeout.Value; wf.OnTimeout = onTimeout.Text.Trim();
                });
                break;
            }
            default:
                BuildMoreFields();
                break;
        }
    }

    /// <summary>X, Y, Pick (with "relative to window" option) and Window fields.</summary>
    private (IntBox X, IntBox Y, TextBox Window) PointFields(PointAction p, ColorField? pixel, string caption = "X, Y")
    {
        var x = FieldGrid.NewInt(p.X, -100_000, 100_000, 84);
        var y = FieldGrid.NewInt(p.Y, -100_000, 100_000, 84);
        var pick = new RoundButton { Text = "Pick on screen…", AutoSize = true };
        var relative = new CheckBox { Text = "relative to window", AutoSize = true, Checked = !string.IsNullOrWhiteSpace(p.Window) };
        _grid.Row(caption, x, y, pick, relative);
        var window = _grid.TextField("Window", p.Window, 240);
        _tips.SetToolTip(pick, pixel is null
            ? "Follows the mouse: point at the spot and press Enter to save it (Esc cancels). Nothing is clicked. You can also type the numbers."
            : "Follows the mouse: point at the pixel and press Enter to save its position and color (Esc cancels). Nothing is clicked. You can also type the numbers.");
        _tips.SetToolTip(relative, "Pick stores coordinates relative to the client area of the window under the cursor, and its title.");
        _tips.SetToolTip(window, "Optional window title (exact, or part of it). When set, X and Y are relative to its client area.");
        pick.Click += (_, _) =>
            PixelUi.Pick(this, relative.Checked, window.Text, (px, py, color, title) =>
            {
                x.Value = px;
                y.Value = py;
                window.Text = title ?? "";
                if (pixel is not null) pixel.Value = color.ToHex();
            });
        return (x, y, window);
    }

    private (IntBox Tolerance, ComboBox Match) ToleranceFields(PixelActionBase p)
    {
        var tol = FieldGrid.NewInt(p.Tolerance, 0, Rgb.MaxTolerance(MatchMode.Distance), 70);
        var match = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        match.Items.AddRange(["per channel (0–255)", "color distance (0–442)"]);
        match.SelectedIndex = (int)p.Match;
        _grid.Row("Tolerance", tol, match);
        _tips.SetToolTip(match,
            "Per channel: each of R, G, B may differ by the tolerance. Color distance: Euclidean RGB distance, better for gradients.");
        _validators.Add(() => tol.Value > Rgb.MaxTolerance((MatchMode)match.SelectedIndex)
            ? $"Tolerance can be at most {Rgb.MaxTolerance((MatchMode)match.SelectedIndex)} for this match mode." : null);
        return (tol, match);
    }

    private void AddTestRow(IntBox x, IntBox y, TextBox window, ColorField? color, IntBox tol, ComboBox match)
    {
        var test = new RoundButton { Text = "Test", AutoSize = true };
        var result = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Text = "" };
        _grid.Row("", test, result);
        test.Click += (_, _) => result.Text = PixelUi.Test(x.Value, y.Value, window.Text,
            color?.Value, tol.Value, (MatchMode)match.SelectedIndex);
    }

    /// <summary>Lets the existing "timeout.Value" assignments read a duration field.</summary>
    private sealed class TimeoutValue(DurationField input)
    {
        public int Value => (int)Math.Min(int.MaxValue, input.Ms ?? 0);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
