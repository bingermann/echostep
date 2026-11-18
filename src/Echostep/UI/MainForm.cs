using System.Diagnostics;
using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

internal sealed class MainForm : Form
{
    private enum AppState { Idle, Recording, Playing }
    private enum PlayKind { All, IgnoreBreakpoints, FromSelected, UntilSelected, SelectedOnly, File }
    private enum RecordMode { Insert, Append, Replace }

    private static readonly string[] AfterPlaybackNames =
        ["No action", "Shutdown computer", "Restart computer", "Lock computer", "Log out user", "Standby"];

    private const string FileFilter = "Echostep files (*.estep;*.omr;*.orec)|*.estep;*.omr;*.orec|JSON files (*.json)|*.json|All files|*.*";

    private readonly ActionListEditor _editor;
    private readonly RulesPanel _rulesPanel;
    private readonly RuleEngine _engine;
    private readonly HotkeyManager _hotkeys = new();
    private readonly Panel _content, _macroPage, _rulesPage, _settingsPage, _startPanel;
    private readonly FlowLayoutPanel _startBox, _recentLinks;
    private readonly List<(Panel Page, RibbonButton? Button)> _pages = [];
    private readonly TextBox _logBox;
    private readonly TextBox _setStop, _setPicker, _setWatch, _setStopRecording, _setPause;
    private readonly CheckBox _setRecordMoves, _setWatchOnStartup, _setCrosshair, _setDownUp, _setFocus, _setAbortKey, _setAbortMove,
        _setDeleteConfirm, _setHideRecording, _setStartMinimized, _setStartOnLogin;
    private readonly IntBox _setMinMove, _setMagnifier, _setSensitivity;
    private readonly DurationField _setWaitThreshold;
    private readonly CrosshairOverlay _crosshair = new();
    private readonly PlaybackOverlay _overlay = new();
    private readonly ComboBox _setTheme, _setAccent, _setTextSize;
    private static readonly double[] Speeds = [0.25, 0.5, 1, 1.5, 2, 3, 5, 10];
    private readonly bool _playOnStart, _exitAfterPlay;
    private string _playName = "";
    private int _playTotal;
    private bool _playFromEditor = true;
    private readonly List<RibbonButton> _playBtns = [], _stopBtns = [], _idleOnly = [];
    private readonly RibbonButton _stepBtn, _watchBtn;
    private readonly RibbonTabRow _tabRow;
    private readonly ContextMenuStrip _fileMenu;
    private readonly ToolStripMenuItem _recentMenu;
    private readonly ToolStripStatusLabel _countLabel, _stateLabel, _timeLabel, _durationLabel, _cursorLabel, _swatchLabel, _colorLabel, _hotkeyLabel;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 50 };
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private readonly Stopwatch _playWatch = new();

    private AppState _state = AppState.Idle;
    private Panel _ribbon = null!;
    private RibbonPanel _ribbonPanel = null!;
    private int _ribbonHeight; // the ribbon's height without a scrollbar, after DPI scaling
    private string? _filePath;
    private string? _screenLayout; // the monitors the macro was recorded on, saved with it
    private bool _dirty;
    private HookRecorder? _recorder;
    private RecordMode _recordMode;
    private int _recordInsertAt;
    private bool _restoreAfterRecording;
    private Player? _player;
    private PlaybackMonitor? _monitor;
    private PlayKind _playKind;
    private volatile bool _abortedByInput;
    private CancellationTokenSource? _playCts;
    private string _lastStatus = "Idle";
    private (int X, int Y) _lastCursor = (int.MinValue, 0);

    /// <param name="playOnStart">Play the file as soon as the window is shown (command line --play).</param>
    /// <param name="exitAfterPlay">Close once that run has ended (command line --exit, used by schedules).</param>
    public MainForm(string? fileToOpen, bool playOnStart = false, bool exitAfterPlay = false)
    {
        _playOnStart = playOnStart && fileToOpen is not null;
        _exitAfterPlay = _playOnStart && exitAfterPlay;
        // A power action chosen last time must never carry over to a new session.
        Program.Settings.AfterPlayback = 0;

        _engine = new RuleEngine(WinScreen.Instance, WinInput.Instance, rulesDirectory: () => AppPaths.DataDir, host: WinHost.Instance);
        _engine.WatchingChanged += watching => BeginInvoke(() => OnWatchingChanged(watching));

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.Prepare(this);
        Size = new Size(1104, 820);
        MinimumSize = new Size(588, 336);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // ---- File menu (opened by the File tab) ------------------------------------------------
        _fileMenu = new ContextMenuStrip { Font = Theme.UiFont };
        ToolStripMenuItem FileItem(string text, Action click, string? shortcut = null)
        {
            var mi = new ToolStripMenuItem(text, null, (_, _) => click());
            if (shortcut is not null) mi.ShortcutKeyDisplayString = shortcut;
            _fileMenu.Items.Add(mi);
            return mi;
        }
        FileItem("&New", NewFile, "Ctrl+N");
        FileItem("&Open…", OpenFile, "Ctrl+O");
        _recentMenu = FileItem("Open &Recent", () => { });
        _fileMenu.Items.Add(new ToolStripSeparator());
        FileItem("&Save", () => Save(), "Ctrl+S");
        FileItem("Save &As…", () => SaveAs());
        _fileMenu.Items.Add(new ToolStripSeparator());
        FileItem("Se&ttings", () => ShowPage(_settingsPage!));
        _fileMenu.Items.Add(new ToolStripSeparator());
        FileItem("E&xit", Close);
        _fileMenu.Opening += (_, _) => BuildRecentMenu();

        // ---- Ribbon: tab row (22 px) over the selected tab's panel (100 px) ---------------------
        // Positions and sizes are the spec's, in 96-dpi pixels; the form's DPI scaling does the rest.
        var s0 = Program.Settings;
        var ribbonFont = new Font("Segoe UI", 9f);
        var px = (int)Math.Round(32 * this.DeviceDpi / 96.0); // icon size on big buttons
        _tabRow = new RibbonTabRow(("File", 0, 80), ("Record and Edit", 80, 168), ("Playback", 248, 100), ("View", 348, 80))
        {
            Dock = DockStyle.Top, Height = 22, Font = ribbonFont,
        };
        var ribbon = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Theme.Surface, Font = ribbonFont };
        _ribbon = ribbon;
        RibbonPanel editTab = new(), playTab = new(), viewTab = new();
        ribbon.Controls.AddRange([editTab, playTab, viewTab]);
        var tabPanels = new[] { editTab, playTab, viewTab };
        _tabRow.TabSelected += index =>
        {
            for (var i = 0; i < tabPanels.Length; i++) tabPanels[i].Visible = i == index - 1;
            _ribbonPanel = tabPanels[index - 1];
            UpdateRibbonScroll();
        };
        _tabRow.FileClicked += () => { if (_state == AppState.Idle) _fileMenu.Show(_tabRow, new Point(0, _tabRow.Height)); };
        editTab.Visible = true;
        _ribbonPanel = editTab;
        ribbon.SizeChanged += (_, _) => UpdateRibbonScroll();

        ContextMenuStrip MenuOf(params (string? Text, Action? Click)[] items)
        {
            var m = new ContextMenuStrip { Font = Theme.UiFont };
            foreach (var (text, click) in items)
            {
                if (text is null) m.Items.Add(new ToolStripSeparator());
                else m.Items.Add(text, null, (_, _) => click!());
            }
            return m;
        }
        RibbonButton Big(RibbonPanel panel, string caption, Bitmap icon, int x, int width, string tip, Action? click = null, int height = 72)
        {
            var b = panel.Put(new RibbonButton(caption, icon, RibbonStyle.Big), x, 8, width, height, tip);
            if (click is not null) b.Click += (_, _) => click();
            return b;
        }
        (string?, Action?) Insert(string name)
        {
            var create = ActionListEditor.Creator(name);
            return (name, () => InsertAction(create()));
        }

        // Play, Record (each with its ▾ strip) and Stop sit at the same place on both tabs.
        void Transport(RibbonPanel panel)
        {
            _playBtns.Add(Big(panel, "Play", Icons.Play(px), 8, 64, "Play from the top. While playing: pause and resume.", PlayOrPause, height: 54));
            var playStrip = panel.Put(new RibbonButton("", null, RibbonStyle.Strip), 8, 62, 64, 16, "More ways to play");
            playStrip.SetMenu(MenuOf(
                ("Play ignoring breakpoints", () => Play(PlayKind.IgnoreBreakpoints)),
                ("Play until selected", () => Play(PlayKind.UntilSelected)),
                ("Play from selected", () => Play(PlayKind.FromSelected)),
                ("Play selected", () => Play(PlayKind.SelectedOnly))));
            var record = Big(panel, "Record", Icons.Record(px), 72, 72, "New recording (replaces the list after asking)",
                () => StartRecording(RecordMode.Replace), height: 54);
            var recordStrip = panel.Put(new RibbonButton("", null, RibbonStyle.Strip), 72, 62, 72, 16, "More ways to record");
            recordStrip.SetMenu(MenuOf(
                ("Append recording", () => StartRecording(RecordMode.Append)),
                ("Insert recording", () => StartRecording(RecordMode.Insert))));
            _stopBtns.Add(Big(panel, "Stop", Icons.Stop(px), 144, 48, "Stop recording or playback", StopRecordingOrPlayback));
            _idleOnly.AddRange([playStrip, record, recordStrip]);
            panel.Separator(196);
        }

        // Record and Edit tab
        Transport(editTab);
        var mouse = Big(editTab, "Mouse", Icons.Mouse(px), 201, 48, "Insert a mouse action below the selected row");
        mouse.SetMenu(MenuOf(Insert("Click"), Insert("Move"), Insert("Wheel")));
        var keyPress = Big(editTab, "Key press", Icons.Key(px), 249, 75, "Insert a Key press below the selected row", () => InsertAction(new KeyAction()));
        var wait = Big(editTab, "Wait", Icons.Clock(px), 324, 47, "Insert a wait below the selected row");
        wait.SetMenu(MenuOf(Insert("Wait"), Insert("Wait for pixel"), Insert("Wait for screen change"), (null, null),
            Insert("Wait until time"), Insert("Wait for pixel change")));
        var flow = Big(editTab, "Flow", Icons.Flow(px), 371, 75, "Insert a flow action below the selected row");
        flow.SetMenu(MenuOf(Insert("Goto"), Insert("Repeat"), (null, null), Insert("If time / day / window"), Insert("If pixel"), (null, null), Insert("Window focus")));
        editTab.Caption("Add Action", 208, 238);
        editTab.Separator(452);
        var editBtn = editTab.Put(new RibbonButton("Edit", null, RibbonStyle.Small), 464, 8, 142, 25, "Open the selected action's dialog");
        editBtn.Click += (_, _) => _editor!.EditSelected();
        var deleteBtn = editTab.Put(new RibbonButton("Delete", null, RibbonStyle.Small), 464, 33, 142, 25, "Delete the selection");
        deleteBtn.Click += (_, _) => _editor!.DeleteSelected();
        var searchBtn = editTab.Put(new RibbonButton("Find", null, RibbonStyle.Small), 464, 58, 142, 25, "Find rows (Ctrl+F); tick Replace in it to replace (Ctrl+H)");
        searchBtn.Click += (_, _) => _editor!.OpenFind(false);
        var expertSep = editTab.Put(new Panel { BackColor = Theme.Border }, 612, 4, 1, 90);
        var more = Big(editTab, "More", Icons.Spark(px), 620, 56, "More actions: Find image, Type text, Set variable, Run / open, Message / sound");
        more.SetMenu(MenuOf(ActionListEditor.MoreActionNames.Select(Insert).ToArray()));
        var moreCaption = editTab.Put(new Label { Text = "More", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false }, 614, 80, 68, 16);
        _idleOnly.AddRange([mouse, keyPress, wait, flow, editBtn, deleteBtn, more]);

        // Playback tab
        Transport(playTab);
        ComboBox Choice(string[] items, int index, Action<int> changed)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(items);
            box.SelectedIndex = Math.Clamp(index, 0, items.Length - 1);
            box.SelectedIndexChanged += (_, _) => changed(box.SelectedIndex);
            return box;
        }
        playTab.Note("Mouse path:", 204, 8);
        playTab.Put(Choice(["As recorded", "Linear", "Spline", "Natural"], s0.MousePath, i => Program.Settings.MousePath = i), 286, 8, 146, 22,
            "How mouse moves are replayed: as recorded, or smoothed into straight lines, splines or natural curves");
        playTab.Note("Repeat:", 204, 37);
        var repeat = playTab.Put(new IntBox(Math.Max(1, s0.RepeatCount), 1, 999_999, 70), 286, 37, 70, 22, "How many times Play runs the whole macro (1–999,999)");
        repeat.TextChanged += (_, _) => Program.Settings.RepeatCount = repeat.Value;
        playTab.Caption("Playback Properties", 204, 226);
        playTab.Separator(439);
        playTab.Note("After playback:", 447, 8);
        playTab.Put(Choice(AfterPlaybackNames, 0, i => Program.Settings.AfterPlayback = i), 447, 32, 138, 22,
            "What to do when Play finishes. Always starts at No action when Echostep opens.");
        var countdown = playTab.Put(new CheckBox { Text = "Show countdown", Checked = s0.ShowCountdown }, 447, 58, 138, 19,
            "Show a countdown you can cancel before the after-playback action");
        countdown.CheckedChanged += (_, _) => Program.Settings.ShowCountdown = countdown.Checked;
        playTab.Separator(591);
        foreach (var (text, flag, x, y) in new[]
                 {
                     ("Mouse moves", PlaybackFilter.MouseMoves, 600, 11), ("Mouse clicks", PlaybackFilter.MouseClicks, 600, 36),
                     ("Key presses", PlaybackFilter.KeyPresses, 600, 61), ("Wait times", PlaybackFilter.WaitTimes, 740, 11),
                     ("Focus changes", PlaybackFilter.FocusChanges, 740, 36),
                 })
        {
            var box = playTab.Put(new CheckBox { Text = text, Checked = ((PlaybackFilter)s0.PlaybackFilter).HasFlag(flag) }, x, y, x == 600 ? 132 : 146, 19,
                "Unticked types are hidden in the list and skipped when playing");
            box.CheckedChanged += (_, _) =>
            {
                var f = (PlaybackFilter)Program.Settings.PlaybackFilter;
                f = box.Checked ? f | flag : f & ~flag;
                Program.Settings.PlaybackFilter = (int)f;
                _editor!.SetFilter(f);
            };
        }
        playTab.Caption("Playback filter", 600, 286);
        playTab.Separator(892);
        _stepBtn = Big(playTab, "Step", Icons.StepIcon(px), 900, 56, "While paused: run one row, then pause again", () => _player?.Step());
        var playExpertSep = playTab.Put(new Panel { BackColor = Theme.Border }, 962, 4, 1, 90);
        var speedNote = playTab.Note("Speed:", 970, 8);
        var speedIndex = Array.FindIndex(Speeds, v => Math.Abs(v - s0.PlaybackSpeed) < 0.001);
        var speed = playTab.Put(Choice(Speeds.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×").ToArray(),
            speedIndex < 0 ? 2 : speedIndex, i => Program.Settings.PlaybackSpeed = Speeds[i]), 1020, 8, 80, 22,
            "Plays Wait and mouse-move times faster or slower. Timeouts and Wait until time are not changed.");
        var overlay = playTab.Put(new CheckBox { Text = "Progress overlay", Checked = s0.ShowOverlay }, 970, 37, 132, 19,
            "Show a small box in the corner while playing: the step, time and passes left");
        overlay.CheckedChanged += (_, _) => Program.Settings.ShowOverlay = overlay.Checked;
        var scheduleBtn = playTab.Put(new RibbonButton("Schedule…", null, RibbonStyle.Small), 1110, 8, 130, 25, "Play a macro at set times with Windows Task Scheduler");
        scheduleBtn.Click += (_, _) => OpenSchedules();
        var hotkeysBtn = playTab.Put(new RibbonButton("Macro hotkeys…", null, RibbonStyle.Small), 1110, 33, 130, 25, "Hotkeys that play a macro file straight away");
        hotkeysBtn.Click += (_, _) => OpenMacroHotkeys();
        var playExpertCaption = playTab.Put(new Label { Text = "Extras", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false }, 970, 80, 270, 16);
        _idleOnly.AddRange([scheduleBtn, hotkeysBtn]);

        // View tab
        var lineNumbers = Big(viewTab, "Show line numbers", Icons.Lines(px, numbered: true), 8, 70, "Show or hide the line-number column");
        lineNumbers.SetChecked(s0.ShowLineNumbers);
        lineNumbers.Click += (_, _) =>
        {
            lineNumbers.SetChecked(!lineNumbers.IsChecked);
            Program.Settings.ShowLineNumbers = lineNumbers.IsChecked;
            _editor!.SetShowLineNumbers(lineNumbers.IsChecked);
        };
        viewTab.Separator(84);
        var showMacro = Big(viewTab, "Macro", Icons.Lines(px, numbered: false), 92, 56, "The action list");
        var showRules = Big(viewTab, "Rules", Icons.Watch(px), 148, 56, "Background pixel rules");
        var showLog = Big(viewTab, "Log", Icons.Lines(px, numbered: false), 204, 56, "What happened: playback results, rule firings, timeouts");
        var showSettings = Big(viewTab, "Settings", Icons.Gear(px), 260, 62, "Recording, playback, hotkeys and user interface settings");
        viewTab.Caption("Show", 92, 230);
        viewTab.Separator(328);
        _watchBtn = Big(viewTab, "Watch", Icons.Watch(px), 336, 64, "Run background pixel rules", ToggleWatch);
        Big(viewTab, "Picker", Icons.Picker(px), 400, 56, "Live color picker: Enter adds a pixel rule, Esc closes", OpenPicker);
        viewTab.Caption("Pixel rules", 336, 120);

        // ---- Pages under the ribbon ------------------------------------------------------------
        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        Panel Page(RibbonButton button, Control body)
        {
            var page = new Panel { Dock = DockStyle.Fill, Visible = false };
            var card = new Card { Dock = DockStyle.Fill };
            card.Controls.Add(body);
            page.Controls.Add(card);
            _content.Controls.Add(page);
            button.Click += (_, _) => ShowPage(page);
            _pages.Add((page, button));
            return page;
        }

        _editor = new ActionListEditor(showInsertButton: false, isMain: true) { Dock = DockStyle.Fill };
        _editor.SetFilter((PlaybackFilter)s0.PlaybackFilter);
        _editor.SetShowLineNumbers(s0.ShowLineNumbers);
        _editor.Changed += (_, _) => { SetDirty(true); UpdateStartPanel(); UpdateDuration(); };
        _editor.SelectionInfoChanged += (_, _) => UpdateCount();
        _editor.CommandRequested += OnEditorCommand;

        // Start panel: shown instead of the empty list until there is a macro.
        _startPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _startBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        var startRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        var startRecord = new RoundButton { Text = "Record a new macro", Kind = ButtonKind.Primary, AutoSize = true, Padding = new Padding(20, 10, 20, 10) };
        startRecord.Click += (_, _) => StartRecording(RecordMode.Insert);
        var startOpen = new RoundButton { Text = "Load a file…", AutoSize = true, Padding = new Padding(20, 10, 20, 10) };
        startOpen.Click += (_, _) => OpenFile();
        startRow.Controls.AddRange([startRecord, new Label { Text = "or", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 14, 10, 0) }, startOpen]);
        _recentLinks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        _startBox.Controls.AddRange([
            new Label { Text = "Start a macro", AutoSize = true, Font = new Font("Segoe UI", 16f), Margin = new Padding(3, 0, 3, 14) },
            startRow, _recentLinks,
        ]);
        _startPanel.Controls.Add(_startBox);
        _startPanel.Resize += (_, _) => CentreStartBox();

        var macroHost = new Panel { Dock = DockStyle.Fill };
        macroHost.Controls.Add(_editor);
        macroHost.Controls.Add(_startPanel);
        _macroPage = Page(showMacro, macroHost);

        _rulesPanel = new RulesPanel(_engine) { Dock = DockStyle.Fill };
        _rulesPage = Page(showRules, _rulesPanel);

        var logBody = new Panel { Dock = DockStyle.Fill };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font("Consolas", 10f),
        };
        var logButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var clearLog = new RoundButton { Text = "Clear", AutoSize = true, MinimumSize = new Size(100, 0) };
        clearLog.Click += (_, _) => { Log.Clear(); _logBox.Clear(); };
        var copyLog = new RoundButton { Text = "Copy all", AutoSize = true, MinimumSize = new Size(100, 0) };
        copyLog.Click += (_, _) => { if (_logBox.TextLength > 0) Clipboard.SetText(_logBox.Text); };
        logButtons.Controls.AddRange([clearLog, copyLog]);
        logBody.Controls.Add(_logBox);
        logBody.Controls.Add(logButtons);
        Page(showLog, logBody);

        // ---- Settings page ---------------------------------------------------------------------
        var settingsBody = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var sg = new FieldGrid { Dock = DockStyle.Top };
        CheckBox Tick(string text) => sg.Add("", new CheckBox { Text = text, AutoSize = true });
        sg.Header("Appearance");
        _setTheme = sg.Combo("Theme", Theme.ThemeNames, null, editable: false, width: 180);
        _setAccent = sg.Combo("Accent color", Theme.Accents.Select(a => a.Name), null, editable: false, width: 180);
        _setAccent.DrawMode = DrawMode.OwnerDrawFixed;
        _setAccent.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            var k = e.Bounds.Height - 8;
            using (var b = new SolidBrush(Theme.Accents[e.Index].Color)) e.Graphics.FillRectangle(b, e.Bounds.X + 4, e.Bounds.Y + 4, k, k);
            TextRenderer.DrawText(e.Graphics, Theme.Accents[e.Index].Name, e.Font ?? Theme.UiFont,
                new Point(e.Bounds.X + k + 12, e.Bounds.Y + 1), (e.State & DrawItemState.Selected) != 0 ? Theme.SelectionText : Theme.Text);
        };
        _setTextSize = sg.Combo("Text size", Theme.TextSizes, null, editable: false, width: 180);
        sg.Add("", new Label { AutoSize = true, ForeColor = Theme.Muted, Text = "A new theme, accent color or text size takes effect when Echostep restarts." });
        sg.Header("Recording");
        _setRecordMoves = Tick("Record mouse paths");
        _setMinMove = sg.Int("Minimum mouse movement (px)", 8, 0, 500);
        _setSensitivity = sg.Int("Mouse sensitivity (1 low – 40 high)", 40, 1, 40);
        _setWaitThreshold = sg.Duration("Pauses longer than this become Wait", 50, 0, 60_000);
        _setDownUp = Tick("Record buttons and keys as separate Down and Up rows (captures drags and held keys)");
        _setFocus = Tick("Record a Window focus action when another window comes to the front");
        sg.Header("Playback");
        _setAbortKey = Tick("Abort playback on key press");
        _setAbortMove = Tick("Abort playback on mouse move");
        _setCrosshair = Tick("Show a crosshair on the pixel a playing macro is watching");
        sg.Header("Hotkeys");
        _setStopRecording = sg.TextField("Stop recording", "", 200);
        _setPause = sg.TextField("Pause / resume playback", "", 200);
        _setStop = sg.TextField("Stop everything", "", 200);
        _setPicker = sg.TextField("Color picker", "", 200);
        _setWatch = sg.TextField("Pause / resume rules", "", 200);
        sg.Add("", new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(560, 0),
            Text = "While recording, the stop key is not recorded. To put that key in a macro, add it with Keypress, or pick a different stop key. " +
                   "The pause key only reacts to real key presses, never to keys a macro sends.",
        });
        sg.Header("User interface");
        _setDeleteConfirm = Tick("Show delete confirmation");
        _setHideRecording = Tick("Hide Echostep when recording");
        _setStartMinimized = Tick("Start minimized");
        _setStartOnLogin = Tick("Start on login");
        sg.Header("Pixels and rules");
        _setWatchOnStartup = Tick("Start watching rules when Echostep opens");
        _setMagnifier = sg.Int("Picker magnifier size (pixels across)", 15, 7, 41);
        var apply = new RoundButton { Text = "Save settings", Kind = ButtonKind.Primary, AutoSize = true };
        apply.Click += (_, _) => ApplySettings();
        var openFolder = new RoundButton { Text = "Open data folder", AutoSize = true };
        openFolder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        sg.Header("");
        sg.Row("", apply, openFolder);
        sg.Add("", new Label { AutoSize = true, ForeColor = Theme.Muted, Text = $"settings.json and rules.json live in {AppPaths.DataDir}" });
        var tips = new ToolTip();
        tips.SetToolTip(_setStopRecording, "One key (or a combination) that ends a recording. Default: Enter.");
        tips.SetToolTip(_setPause, "One key (or a combination) that pauses and resumes playback. Default: Escape.");
        tips.SetToolTip(_setStop, "Stops recording and playback, halts rule actions and turns watching off. Written like Ctrl+Alt+Escape.");
        tips.SetToolTip(_setPicker, "Opens the live color picker. Written like Ctrl+Alt+P.");
        tips.SetToolTip(_setWatch, "Turns background pixel rules on and off. Written like Ctrl+Alt+W.");
        settingsBody.Controls.Add(sg);
        _settingsPage = Page(showSettings, settingsBody);
        LoadSettingsFields();

        // ---- Status bar ------------------------------------------------------------------------
        var status = new StatusStrip { Font = Theme.UiFont, Padding = new Padding(10, 3, 14, 3) };
        _countLabel = new ToolStripStatusLabel("0 actions") { AutoSize = false, Width = 190, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted };
        _stateLabel = new ToolStripStatusLabel("Idle") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _timeLabel = new ToolStripStatusLabel("") { AutoSize = false, Width = 84, TextAlign = ContentAlignment.MiddleRight, ToolTipText = "Elapsed playback time" };
        _durationLabel = new ToolStripStatusLabel("") { AutoSize = false, Width = 84, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted, ToolTipText = "Macro duration: its fixed waits and move times" };
        _cursorLabel = new ToolStripStatusLabel("") { AutoSize = false, Width = 110, TextAlign = ContentAlignment.MiddleRight };
        _swatchLabel = new ToolStripStatusLabel("") { AutoSize = false, Width = 20, Margin = new Padding(4, 5, 4, 4) };
        _colorLabel = new ToolStripStatusLabel("") { AutoSize = false, Width = 78, TextAlign = ContentAlignment.MiddleLeft };
        _hotkeyLabel = new ToolStripStatusLabel("") { ForeColor = Theme.Muted };
        status.Items.AddRange([_countLabel, _stateLabel, _timeLabel, _durationLabel, _cursorLabel, _swatchLabel, _colorLabel, _hotkeyLabel]);

        Controls.Add(_content);
        Controls.Add(ribbon);
        Controls.Add(_tabRow);
        Controls.Add(status);
        Theme.Apply(this);
        ShowPage(_macroPage);
        ResumeLayout(false);
        PerformLayout();

        _uiTimer.Tick += (_, _) => UiTick();
        Log.LineWritten += line => _pendingLog.Enqueue(line);
        foreach (var line in Log.Snapshot()) _pendingLog.Enqueue(line);

        _editor.Actions = [];
        if (fileToOpen is not null) LoadFile(fileToOpen);
        UpdateTitle();
        UpdateControls();
        UpdateStartPanel();
        UpdateDuration();
        UpdateCount();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _ribbonHeight = _ribbon.Height;
        UpdateRibbonScroll();
        RegisterHotkeys();
        _rulesPanel.Reload();
        _uiTimer.Start();
        if (Program.Settings.WatchOnStartup) _engine.Start();
        if (Program.Settings.StartMinimized || _playOnStart) WindowState = FormWindowState.Minimized;
        _editor.FocusGrid();
        if (_playOnStart) BeginInvoke(() => Play(PlayKind.All));
    }

    // ===========================================================================================
    // Hotkeys
    // ===========================================================================================

    private Hotkey _stopHotkey, _pickerHotkey, _watchHotkey, _stopRecordingKey, _pauseKey;

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var problems = new List<string>();
        Hotkey Reg(string text, string what, Action action)
        {
            if (!Hotkey.TryParse(text, out var hk)) { problems.Add($"{what} hotkey \"{text}\" is not valid"); return default; }
            if (hk.IsEmpty) return hk;
            if (_hotkeys.Register(hk, action) == 0) problems.Add($"{hk} ({what}) is in use by another program");
            return hk;
        }
        var s = Program.Settings;
        _stopHotkey = Reg(s.StopHotkey, "stop", EmergencyStop);
        _pickerHotkey = Reg(s.PickerHotkey, "picker", OpenPicker);
        _watchHotkey = Reg(s.WatchHotkey, "rules", ToggleWatch);
        // These two are plain keys by default (Enter, Esc), so they are only watched while recording / playing.
        Hotkey.TryParse(s.StopRecordingKey, out _stopRecordingKey);
        Hotkey.TryParse(s.PauseKey, out _pauseKey);
        foreach (var mh in s.MacroHotkeys)
            {
                var file = mh.File;
                Reg(mh.Hotkey, Path.GetFileName(file), () => PlayFile(file));
            }
        foreach (var p in problems) Log.Write("Hotkey: " + p + ".");
        _hotkeyLabel.Text = _stopHotkey.IsEmpty ? "No stop hotkey" : $"Stop: {_stopHotkey}";
        _hotkeyLabel.ForeColor = problems.Count > 0 ? Theme.Danger : Theme.Muted;
        _hotkeyLabel.ToolTipText = problems.Count > 0 ? string.Join("\n", problems)
            : $"Stop recording: {_stopRecordingKey}   Pause/resume playback: {_pauseKey}   Picker: {_pickerHotkey}   Rules on/off: {_watchHotkey}";
    }

    /// <summary>Stop hotkey: stops recording and playback, halts running rule actions and turns watching off.</summary>
    private void EmergencyStop()
    {
        var hadRules = _engine.Watching || _engine.RunningCount > 0;
        StopRecordingOrPlayback();
        _engine.CancelRunningActions();
        _engine.Stop();
        if (hadRules) Log.Write("Stop hotkey: rule actions halted and watching turned off.");
    }

    // ===========================================================================================
    // Record
    // ===========================================================================================

    private void StartRecording(RecordMode mode)
    {
        if (_state != AppState.Idle) return;
        ShowPage(_macroPage);
        if (mode == RecordMode.Replace && _editor.Actions.Count > 0 &&
            MessageBox.Show(this, "Replace the current actions?", "New recording", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _recordMode = mode;
        _recordInsertAt = mode == RecordMode.Append ? _editor.Actions.Count : _editor.InsertIndex;
        var recorder = new HookRecorder([_stopHotkey, _pickerHotkey, _watchHotkey], _stopRecordingKey);
        recorder.StopRequested += () => BeginInvoke(() => { if (_state == AppState.Recording) StopRecordingOrPlayback(); });
        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            recorder.Dispose();
            MessageBox.Show(this, ex.Message, "Record", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _recorder = recorder;
        Log.Write("Recording started.");
        SetState(AppState.Recording);
        _startPanel.Visible = false;
        _editor.Visible = true;
        _restoreAfterRecording = Program.Settings.HideWhenRecording && WindowState != FormWindowState.Minimized;
        if (_restoreAfterRecording) WindowState = FormWindowState.Minimized;
    }

    private void StopRecordingOrPlayback()
    {
        switch (_state)
        {
            case AppState.Recording when _recorder is not null:
            {
                var events = _recorder.Stop();
                _recorder.Dispose();
                _recorder = null;
                var actions = RecorderLogic.BuildActions(events, Program.Settings.Recording);
                Log.Write($"Recording stopped: {events.Count} events → {actions.Count} actions.");
                SetState(AppState.Idle);
                if (_restoreAfterRecording)
                {
                    WindowState = FormWindowState.Normal;
                    Activate();
                }
                if (actions.Count > 0)
                {
                    if (_recordMode == RecordMode.Replace) _editor.ReplaceAll(actions);
                    else _editor.InsertRange(_recordInsertAt, actions);
                    _screenLayout = Native.ScreenLayout(); // stored with the macro, checked before playing
                }
                UpdateStartPanel();
                break;
            }
            case AppState.Playing:
                _playCts?.Cancel();
                break;
        }
    }

    // ===========================================================================================
    // Play
    // ===========================================================================================

    private void PlayOrPause()
    {
        if (_state == AppState.Playing) _player?.TogglePause();
        else Play(PlayKind.All);
    }

    private void OnEditorCommand(EditorCommand command)
    {
        switch (command)
        {
            case EditorCommand.PlayFromSelected: Play(PlayKind.FromSelected); break;
            case EditorCommand.PlayUntilSelected: Play(PlayKind.UntilSelected); break;
            case EditorCommand.PlaySelected: Play(PlayKind.SelectedOnly); break;
            case EditorCommand.InsertRecording: StartRecording(RecordMode.Insert); break;
        }
    }

    private void Play(PlayKind kind)
    {
        if (_state != AppState.Idle) return;
        ShowPage(_macroPage);
        var actions = _editor.Actions;
        if (actions.Count == 0)
        {
            SetStatus("Nothing to play: record or insert some actions first.");
            return;
        }
        var selected = _editor.FirstSelected;
        if (kind is PlayKind.FromSelected or PlayKind.UntilSelected or PlayKind.SelectedOnly && selected < 0)
        {
            SetStatus("Select a row first.");
            return;
        }
        var problems = MacroValidator.Validate(actions);
        if (problems.Count > 0)
        {
            var row = problems.Keys.Min();
            _editor.SelectRow(row);
            MessageBox.Show(this, $"Play cannot start. Row {row + 1}: {problems[row][0]}", "Play",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var layoutNow = Native.ScreenLayout();
        if (_screenLayout is not null && _screenLayout != layoutNow && _playOnStart)
            Log.Write($"Screen layout differs from the recording ({_screenLayout} → {layoutNow}); playing anyway.");
        else if (_screenLayout is not null && _screenLayout != layoutNow &&
            MessageBox.Show(this, "The screen layout differs from the recording. Playback may not work properly.\n\n" +
                                  $"Recorded on:  {_screenLayout}\nNow:  {layoutNow}\n\nContinue?",
                "Play", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        var s = Program.Settings;
        var options = BaseOptions();
        options.IgnoreBreakpoints = kind is PlayKind.IgnoreBreakpoints or PlayKind.SelectedOnly;
        switch (kind)
        {
            case PlayKind.All or PlayKind.IgnoreBreakpoints: options.RepeatCount = Math.Clamp(s.RepeatCount, 1, 999_999); break;
            case PlayKind.FromSelected: options.FirstRow = selected; break;
            case PlayKind.UntilSelected: options.LastRow = selected; break;
            case PlayKind.SelectedOnly: options.FirstRow = selected; options.LastRow = selected; options.StopOutsideRange = true; break;
        }

        StartPlayback(actions, options, kind, DisplayName, fromEditor: true);
    }

    private static PlayOptions BaseOptions()
    {
        var s = Program.Settings;
        return new PlayOptions
        {
            Filter = (PlaybackFilter)s.PlaybackFilter,
            MousePath = (MousePathMode)Math.Clamp(s.MousePath, 0, 3),
            Speed = s.PlaybackSpeed > 0 ? s.PlaybackSpeed : 1,
        };
    }

    /// <summary>Macro hotkey: plays a macro file without opening it in the window.</summary>
    private void PlayFile(string path)
    {
        if (_state != AppState.Idle) return;
        List<MacroAction> actions;
        try
        {
            actions = MacroFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Write($"Macro hotkey: could not open {path}: {ex.Message}");
            SetStatus($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        var why = actions.Count == 0 ? "it has no actions" : MacroValidator.FirstProblem(actions);
        if (why is not null)
        {
            Log.Write($"Macro hotkey: {Path.GetFileName(path)} cannot play: {why}");
            SetStatus($"{Path.GetFileName(path)} cannot play: {why}");
            return;
        }
        var options = BaseOptions();
        options.IgnoreBreakpoints = true;
        StartPlayback(actions, options, PlayKind.File, Path.GetFileName(path), fromEditor: false);
    }

    private void StartPlayback(IReadOnlyList<MacroAction> actions, PlayOptions options, PlayKind kind, string name, bool fromEditor)
    {
        var s = Program.Settings;
        _playKind = kind;
        _playName = name;
        _playTotal = actions.Count;
        _playFromEditor = fromEditor;
        _abortedByInput = false;
        _playCts = new CancellationTokenSource();
        var cts = _playCts;
        var player = new Player(actions, WinInput.Instance, WinScreen.Instance, log: Log.Write, options: options, host: WinHost.Instance);
        _player = player;

        // Real key presses and mouse moves (never the macro's own input): the pause key, and abort on user input.
        var monitor = new PlaybackMonitor(_pauseKey, s.AbortOnKeyPress, s.AbortOnMouseMove);
        if (monitor.Needed)
        {
            monitor.PauseToggled += player.TogglePause;
            monitor.UserInput += () =>
            {
                _abortedByInput = true;
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* playback already ended */ }
            };
            try
            {
                monitor.Start();
                _monitor = monitor;
            }
            catch (Exception ex)
            {
                monitor.Dispose();
                Log.Write("Pause key and abort-on-input are off for this run: " + ex.Message);
            }
        }
        else monitor.Dispose();

        var token = cts.Token;
        SetState(AppState.Playing);
        _playWatch.Restart();
        Log.Write($"Playing {name}{(options.Speed != 1 ? $" at {options.Speed:0.##}× speed" : "")}.");
        Native.AcquireHighResTimer();
        Task.Run(() =>
        {
            PlayResult result;
            try
            {
                result = player.Run(token);
            }
            catch (Exception ex)
            {
                result = new PlayResult(PlayOutcome.Failed, ex.Message, player.CurrentRow);
            }
            finally
            {
                Native.ReleaseHighResTimer();
            }
            BeginInvoke(() => PlaybackEnded(result));
        });
    }

    private void PlaybackEnded(PlayResult result)
    {
        _playWatch.Stop();
        _monitor?.Stop();
        _monitor?.Dispose();
        _monitor = null;
        _player = null;
        _playCts?.Dispose();
        _playCts = null;
        _editor.SetPlayingRow(-1);
        _editor.SetLiveValues(null); // every row shows its normal value again
        _crosshair.HideOverlay();
        _overlay.HideOverlay();
        SetState(AppState.Idle);
        if (_exitAfterPlay)
        {
            Log.Write($"Playback: {result.Message}");
            Close();
            return;
        }
        var message = result.Outcome == PlayOutcome.Cancelled && _abortedByInput ? "Playback interrupted by user input." : result.Message;
        Log.Write($"Playback: {message}");
        SetStatus(result.Outcome switch
        {
            PlayOutcome.Finished => "Idle – playback finished",
            PlayOutcome.Cancelled => _abortedByInput ? "Idle – playback interrupted by user input" : "Idle – playback stopped",
            _ => "Idle – " + message,
        });
        if (result.Outcome == PlayOutcome.Failed)
        {
            if (result.Row >= 0 && _playFromEditor) _editor.SelectRow(result.Row);
            MessageBox.Show(this, message, "Playback stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // After-playback action: only after a whole run that finished by itself.
        var after = Program.Settings.AfterPlayback;
        if (result.Outcome != PlayOutcome.Finished || after <= 0 || after >= AfterPlaybackNames.Length ||
            _playKind is not (PlayKind.All or PlayKind.IgnoreBreakpoints)) return;
        var name = AfterPlaybackNames[after];
        if (Program.Settings.ShowCountdown && !Dialogs.Countdown(this, name, 15))
        {
            Log.Write($"After playback: {name} cancelled.");
            return;
        }
        Log.Write($"After playback: {name}.");
        try
        {
            if (after is 1 or 2 or 4) Program.Settings.Save(AppPaths.SettingsFile);
            Native.PowerAction(after);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{name} failed: {ex.Message}", "After playback", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void InsertAction(MacroAction action)
    {
        if (_state != AppState.Idle) return;
        ShowPage(_macroPage);
        _editor.InsertNew(action);
    }

    // ===========================================================================================
    // Rules and picker
    // ===========================================================================================

    private void ToggleWatch()
    {
        if (_engine.Watching)
        {
            _engine.Stop();
            return;
        }
        if (_rulesPanel.Rules.Count == 0)
        {
            ShowPage(_rulesPage);
            SetStatus("No pixel rules yet: add one on the Rules tab (or use the Picker).");
            return;
        }
        _engine.Start();
    }

    private void OnWatchingChanged(bool watching)
    {
        if (watching) Native.AcquireHighResTimer();
        else Native.ReleaseHighResTimer();
        UpdateControls();
    }

    private void OpenPicker()
    {
        if (PickerForm.IsOpen || _state == AppState.Recording) return;
        PickerForm.Open("Enter: new pixel rule here   Esc: close", Program.Settings.MagnifierPixels, result =>
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            ShowPage(_rulesPage);
            _rulesPanel.AddRule(new PixelRule
            {
                Name = $"Pixel {result.X}, {result.Y}",
                X = result.X,
                Y = result.Y,
                Color = result.Color.ToHex(),
            });
        });
    }

    // ===========================================================================================
    // File tab
    // ===========================================================================================

    private string DisplayName => _filePath is null ? "Untitled" : Path.GetFileName(_filePath);

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        UpdateTitle();
    }

    private void UpdateTitle() => Text = $"Echostep – {DisplayName}{(_dirty ? "*" : "")}";

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Do you want to save changes to {DisplayName}?", "Echostep",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => Save(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void NewFile()
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        _filePath = null;
        _screenLayout = null;
        _editor.Actions = [];
        SetDirty(false);
        UpdateStartPanel();
        UpdateDuration();
    }

    private void OpenFile()
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = FileFilter,
            InitialDirectory = _filePath is null ? null : Path.GetDirectoryName(_filePath),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        try
        {
            var (actions, screen) = MacroFile.LoadWithScreen(path);
            _filePath = Path.GetFullPath(path);
            _screenLayout = screen;
            _editor.Actions = actions;
            SetDirty(false);
            if (!_playOnStart)
            {
                Program.Settings.AddRecent(_filePath);
                Program.Settings.Save(AppPaths.SettingsFile);
            }
            Log.Write($"Opened {_filePath} ({actions.Count} actions).");
            var problem = MacroValidator.FirstProblem(actions);
            if (problem is not null) SetStatus("Opened with problems (red rows): " + problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var detail = ex is InvalidDataException ? "The file seems to be corrupt!\n\n" : "";
            MessageBox.Show(this, $"Could not open {path}:\n\n{detail}{ex.Message}", "Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Program.Settings.RecentFiles.Remove(path);
        }
        UpdateStartPanel();
        UpdateDuration();
    }

    private bool Save() => _filePath is null ? SaveAs() : SaveTo(_filePath);

    /// <summary>The File menu's shortcuts (the menu itself only opens from the File tab).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_state == AppState.Idle && ActiveForm == this)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.N: NewFile(); return true;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.S: Save(); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Echostep files (*.estep)|*.estep|All files|*.*",
            DefaultExt = "estep",
            AddExtension = true,
            FileName = _filePath is null ? $"Macro_{DateTime.Now:yyyy-MM-dd}.estep" : Path.GetFileName(_filePath),
            InitialDirectory = _filePath is null ? null : Path.GetDirectoryName(_filePath),
        };
        return dlg.ShowDialog(this) == DialogResult.OK && SaveTo(dlg.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            MacroFile.Save(path, _editor.Actions, _screenLayout);
            _filePath = Path.GetFullPath(path);
            SetDirty(false);
            Program.Settings.AddRecent(_filePath);
            Program.Settings.Save(AppPaths.SettingsFile);
            SetStatus($"Saved {DisplayName}");
            UpdateStartPanel();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not save {path}:\n\n{ex.Message}", "Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void OpenRecent(string path)
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        LoadFile(path);
    }

    private void BuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        var recent = Program.Settings.RecentFiles;
        if (recent.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            return;
        }
        for (var i = 0; i < recent.Count; i++)
        {
            var path = recent[i];
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem($"&{i + 1}  {path}", null, (_, _) => OpenRecent(path)));
        }
    }

    // ===========================================================================================
    // Start panel, count and duration
    // ===========================================================================================

    /// <summary>Shows the start panel instead of the list while there is no macro at all.</summary>
    /// <summary>
    /// When the window is narrower than the ribbon's buttons, the ribbon scrolls sideways; it grows by the
    /// scrollbar's height so the buttons are never cut off at the bottom.
    /// </summary>
    private void UpdateRibbonScroll()
    {
        if (_ribbonHeight == 0 || _ribbonPanel is null) return;
        var needed = 0;
        foreach (Control c in _ribbonPanel.Controls)
            needed = Math.Max(needed, c.Right - _ribbonPanel.AutoScrollPosition.X);
        var scroll = _ribbon.ClientSize.Width < needed + 8;
        var height = _ribbonHeight + (scroll ? SystemInformation.HorizontalScrollBarHeight : 0);
        if (_ribbon.Height != height) _ribbon.Height = height;
    }

    private void UpdateStartPanel()
    {
        var show = _state == AppState.Idle && _filePath is null && _editor.Actions.Count == 0;
        if (show && !_startPanel.Visible)
        {
            _recentLinks.Controls.Clear();
            var recent = Program.Settings.RecentFiles.Take(5).ToList();
            if (recent.Count > 0)
                _recentLinks.Controls.Add(new Label { Text = "Recent files:", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 6, 3, 6) });
            foreach (var path in recent)
            {
                var link = new LinkLabel
                {
                    Text = path, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover, VisitedLinkColor = Theme.Accent,
                    LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(3, 3, 3, 5),
                };
                link.LinkClicked += (_, _) => OpenRecent(path);
                _recentLinks.Controls.Add(link);
            }
        }
        _startPanel.Visible = show;
        _editor.Visible = !show;
        if (show) CentreStartBox();
    }

    private void CentreStartBox() => _startBox.Location = new Point(
        Math.Max(0, (_startPanel.ClientSize.Width - _startBox.Width) / 2),
        Math.Max(0, (_startPanel.ClientSize.Height - _startBox.Height) / 3));

    private void UpdateCount()
    {
        int n = _editor.Actions.Count, sel = _editor.SelectedCount;
        var text = sel > 1 ? $"{n} action{(n == 1 ? "" : "s")} ({sel} selected)" : $"{n} action{(n == 1 ? "" : "s")}";
        if (_countLabel.Text != text) _countLabel.Text = text;
    }

    private void UpdateDuration()
    {
        var d = MacroEditing.Duration(_editor.Actions);
        _durationLabel.Text = _editor.Actions.Count == 0 ? "" : $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";
        UpdateCount();
    }

    // ===========================================================================================
    // Settings
    // ===========================================================================================

    private void LoadSettingsFields()
    {
        var s = Program.Settings;
        _setStop.Text = s.StopHotkey;
        _setPicker.Text = s.PickerHotkey;
        _setWatch.Text = s.WatchHotkey;
        _setStopRecording.Text = s.StopRecordingKey;
        _setPause.Text = s.PauseKey;
        _setRecordMoves.Checked = s.RecordMouseMoves;
        _setMinMove.Value = s.MinMoveDistance;
        _setSensitivity.Value = s.MouseSensitivity;
        _setWaitThreshold.SetMs(s.WaitThresholdMs);
        _setDownUp.Checked = s.RecordDownUp;
        _setFocus.Checked = s.RecordFocusChanges;
        _setAbortKey.Checked = s.AbortOnKeyPress;
        _setAbortMove.Checked = s.AbortOnMouseMove;
        _setCrosshair.Checked = s.ShowCrosshair;
        _setDeleteConfirm.Checked = s.ShowDeleteConfirmation;
        _setHideRecording.Checked = s.HideWhenRecording;
        _setStartMinimized.Checked = s.StartMinimized;
        _setStartOnLogin.Checked = s.StartOnLogin;
        _setTheme.SelectedIndex = Math.Clamp(s.Theme, 0, Theme.ThemeNames.Length - 1);
        var accent = Array.FindIndex(Theme.Accents, a => string.Equals(a.Name, s.AccentColor, StringComparison.OrdinalIgnoreCase));
        _setAccent.SelectedIndex = accent < 0 ? 0 : accent;
        _setTextSize.SelectedIndex = Math.Clamp(s.TextSize, 0, Theme.TextSizes.Length - 1);
        _setWatchOnStartup.Checked = s.WatchOnStartup;
        _setMagnifier.Value = s.MagnifierPixels;
    }

    private void ApplySettings()
    {
        var boxes = new[]
        {
            (_setStopRecording, "Stop recording"), (_setPause, "Pause / resume playback"), (_setStop, "Stop everything"),
            (_setPicker, "Color picker"), (_setWatch, "Pause / resume rules"),
        };
        foreach (var (box, what) in boxes)
        {
            if (Hotkey.TryParse(box.Text, out _)) continue;
            MessageBox.Show(this, $"\"{box.Text}\" is not a valid {what} hotkey. Use a key name like Enter or F8, or a combination like Ctrl+Alt+Escape.",
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            box.Focus();
            return;
        }
        if (_setWaitThreshold.Problem("the wait threshold") is { } waitProblem)
        {
            MessageBox.Show(this, waitProblem, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Hotkey.TryParse(_setStopRecording.Text, out var stopRec);
        Hotkey.TryParse(_setPause.Text, out var pause);
        if (!stopRec.IsEmpty && stopRec == pause)
        {
            MessageBox.Show(this, "This key is already used for the other hotkey.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _setPause.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_setStop.Text) &&
            MessageBox.Show(this, "Without a stop hotkey, an endless Goto loop can only be stopped from the window. Continue?",
                "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var s = Program.Settings;
        var loginChanged = s.StartOnLogin != _setStartOnLogin.Checked;
        s.StopHotkey = _setStop.Text.Trim();
        s.PickerHotkey = _setPicker.Text.Trim();
        s.WatchHotkey = _setWatch.Text.Trim();
        s.StopRecordingKey = _setStopRecording.Text.Trim();
        s.PauseKey = _setPause.Text.Trim();
        s.RecordMouseMoves = _setRecordMoves.Checked;
        s.MinMoveDistance = _setMinMove.Value;
        s.MouseSensitivity = _setSensitivity.Value;
        s.WaitThresholdMs = (int)(_setWaitThreshold.Ms ?? s.WaitThresholdMs);
        s.RecordDownUp = _setDownUp.Checked;
        s.RecordFocusChanges = _setFocus.Checked;
        s.AbortOnKeyPress = _setAbortKey.Checked;
        s.AbortOnMouseMove = _setAbortMove.Checked;
        s.ShowCrosshair = _setCrosshair.Checked;
        s.ShowDeleteConfirmation = _setDeleteConfirm.Checked;
        s.HideWhenRecording = _setHideRecording.Checked;
        s.StartMinimized = _setStartMinimized.Checked;
        s.StartOnLogin = _setStartOnLogin.Checked;
        s.WatchOnStartup = _setWatchOnStartup.Checked;
        s.MagnifierPixels = _setMagnifier.Value;
        var lookChanged = s.Theme != _setTheme.SelectedIndex || s.TextSize != _setTextSize.SelectedIndex ||
                          !string.Equals(s.AccentColor, Theme.Accents[_setAccent.SelectedIndex].Name, StringComparison.OrdinalIgnoreCase);
        s.Theme = _setTheme.SelectedIndex;
        s.AccentColor = Theme.Accents[_setAccent.SelectedIndex].Name;
        s.TextSize = _setTextSize.SelectedIndex;
        if (loginChanged) Native.SetStartOnLogin(s.StartOnLogin);
        s.Save(AppPaths.SettingsFile);
        LoadSettingsFields();
        RegisterHotkeys();
        SetStatus("Settings saved.");
        if (lookChanged && MessageBox.Show(this, "Restart Echostep now to use the new look?", "Settings",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Restart();
    }

    /// <summary>Starts a new Echostep (with the same macro open) and closes this one.</summary>
    private void Restart()
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (_filePath is not null) psi.ArgumentList.Add(_filePath);
        psi.ArgumentList.Add("--after");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not restart: " + ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetDirty(false); // already saved or discarded above
        Close();
    }

    private void UpdateOverlay(Player p, string elapsed)
    {
        if (!Program.Settings.ShowOverlay)
        {
            _overlay.HideOverlay();
            return;
        }
        var row = p.CurrentRow;
        var actions = _playFromEditor ? _editor.Actions : null;
        var what = actions is not null && row >= 0 && row < actions.Count ? actions[row].DisplayName : "";
        string line1 = p.IsPaused ? $"Paused at row {row + 1} of {_playTotal}" : $"Row {row + 1} of {_playTotal}{(what.Length > 0 ? " · " + what : "")}";
        string line2 = p.Status.Length > 0 ? p.Status
            : actions is not null && p.LiveValues().TryGetValue(row, out var live) ? live : "";
        var passes = Program.Settings.RepeatCount > 1 && _playKind is PlayKind.All or PlayKind.IgnoreBreakpoints
            ? $"Pass {p.Pass} of {Program.Settings.RepeatCount} · " : "";
        var speed = Program.Settings.PlaybackSpeed is var sp && Math.Abs(sp - 1) > 0.001 ? $" · {sp:0.##}×" : "";
        var stop = _stopHotkey.IsEmpty ? "" : $" · stop: {_stopHotkey}";
        _overlay.ShowProgress($"▶  {_playName}", line1, line2, $"{passes}{elapsed}{speed}{stop}");
    }

    private void OpenSchedules()
    {
        if (_state != AppState.Idle) return;
        if (_filePath is null && _editor.Actions.Count > 0)
            SetStatus("Tip: save the macro first, then schedule the file.");
        using var dlg = new ScheduleDialog(_filePath);
        dlg.ShowDialog(this);
    }

    private void OpenMacroHotkeys()
    {
        if (_state != AppState.Idle) return;
        using var dlg = new MacroHotkeysDialog(Program.Settings.MacroHotkeys, _filePath);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Program.Settings.MacroHotkeys = dlg.Result;
        Program.Settings.Save(AppPaths.SettingsFile);
        RegisterHotkeys();
        SetStatus($"{dlg.Result.Count} macro hotkey{(dlg.Result.Count == 1 ? "" : "s")} set.");
    }

    private void ShowPage(Panel page)
    {
        foreach (var (p, button) in _pages)
        {
            p.Visible = p == page;
            button?.SetChecked(p == page);
        }
        if (page == _settingsPage) LoadSettingsFields(); // e.g. "Do not show again" may have changed a setting
    }

    // ===========================================================================================
    // State, status bar and periodic UI updates
    // ===========================================================================================

    private void SetState(AppState state)
    {
        _state = state;
        if (state == AppState.Idle) _lastStatus = "Idle";
        UpdateControls();
    }

    private void SetStatus(string text)
    {
        _lastStatus = text;
        UpdateStateLabel();
    }

    private void UpdateControls()
    {
        var idle = _state == AppState.Idle;
        var playing = _state == AppState.Playing;
        foreach (var b in _playBtns) b.Enabled = idle || playing;
        foreach (var b in _stopBtns) b.Enabled = !idle;
        _stepBtn.Enabled = playing && _player is { IsPaused: true };
        foreach (var b in _idleOnly) b.Enabled = idle;
        _editor.ReadOnlyMode = !idle;
        _watchBtn.SetChecked(_engine.Watching);
        _watchBtn.SetCaption(_engine.Watching ? "Watching" : "Watch");
        UpdatePlayButton();
        UpdateStateLabel();
    }

    private void UpdatePlayButton()
    {
        var paused = _player is { IsPaused: true };
        var text = _state != AppState.Playing ? "Play" : paused ? "Resume" : "Pause";
        foreach (var b in _playBtns) b.SetCaption(text);
        var step = _state == AppState.Playing && paused;
        if (_stepBtn.Enabled != step) _stepBtn.Enabled = step;
    }

    private void UpdateStateLabel()
    {
        string text;
        switch (_state)
        {
            case AppState.Recording:
                var keys = string.Join(" or ", new[] { _stopRecordingKey, _stopHotkey }.Where(k => !k.IsEmpty).Select(k => k.ToString()));
                text = $"Recording… ({_recorder?.EventCount ?? 0} events)  press {(keys.Length > 0 ? keys : "Stop")} to stop";
                break;
            case AppState.Playing when _player is { } p:
                var total = _playTotal;
                text = p.IsPaused ? $"Playback paused at row {p.CurrentRow + 1}… press Play{(_pauseKey.IsEmpty ? "" : $" or {_pauseKey}")} to resume"
                    : p.Status.Length > 0 ? p.Status
                    : $"Playing {(_playFromEditor ? "" : _playName + ": ")}{p.CurrentRow + 1} of {total}…";
                if (Program.Settings.RepeatCount > 1 && _playKind is PlayKind.All or PlayKind.IgnoreBreakpoints)
                    text += $"   (pass {p.Pass} of {Program.Settings.RepeatCount})";
                break;
            default:
                text = _lastStatus;
                break;
        }
        if (_engine.Watching)
        {
            var on = _rulesPanel.Rules.Count(r => r.Enabled);
            text += $"   ·   Watching {on} rule{(on == 1 ? "" : "s")}";
            if (_engine.RunningCount > 0) text += $" ({_engine.RunningCount} running)";
        }
        if (_stateLabel.Text != text) _stateLabel.Text = text;
        var color = _state switch
        {
            AppState.Recording => Theme.Danger,
            AppState.Playing => Theme.Success,
            _ => Theme.Text,
        };
        if (_stateLabel.ForeColor != color) _stateLabel.ForeColor = color;
    }

    private void UiTick()
    {
        // Cursor position and color under it.
        var (x, y) = Native.CursorPosition();
        if ((x, y) != _lastCursor)
        {
            _lastCursor = (x, y);
            _cursorLabel.Text = $"{x}, {y}";
        }
        // Read even when the cursor is still: the screen under it may change.
        var c = Native.ReadPixel(x, y);
        var swatch = c is { } col ? PixelUi.ToColor(col) : Theme.Surface;
        if (_swatchLabel.BackColor != swatch) _swatchLabel.BackColor = swatch;
        var hex = c?.ToHex() ?? "—";
        if (_colorLabel.Text != hex) _colorLabel.Text = hex;

        if (_state == AppState.Playing && _player is { } p)
        {
            if (_playFromEditor)
            {
                _editor.SetPlayingRow(p.CurrentRow);
                _editor.SetLiveValues(p.LiveValues());
            }
            // Crosshair on the pixel the macro is watching (Wait for pixel, Wait for pixel change, If pixel).
            if (Program.Settings.ShowCrosshair && p.Tracked is { } t) _crosshair.ShowAt(t.X, t.Y);
            else _crosshair.HideOverlay();
            var e = _playWatch.Elapsed;
            var time = $"{(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
            if (_timeLabel.Text != time) _timeLabel.Text = time;
            UpdateOverlay(p, time);
        }
        UpdatePlayButton();
        UpdateStateLabel();

        if (!_pendingLog.IsEmpty)
        {
            var sb = new System.Text.StringBuilder();
            while (_pendingLog.TryDequeue(out var line)) sb.Append(line).Append("\r\n");
            if (_logBox.TextLength > 400_000) _logBox.Text = _logBox.Text[^200_000..];
            _logBox.AppendText(sb.ToString());
        }
    }

    // ===========================================================================================
    // Closing
    // ===========================================================================================

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_state == AppState.Recording) StopRecordingOrPlayback();
        if (_state == AppState.Playing) _playCts?.Cancel();
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _monitor?.Dispose();
        _recorder?.Dispose();
        _crosshair.Dispose();
        _overlay.Dispose();
        _engine.Dispose();
        _hotkeys.Dispose();
        Program.Settings.AfterPlayback = 0;
        // A scheduled run (--play) must not overwrite settings changed meanwhile in another Echostep window.
        if (!_playOnStart) Program.Settings.Save(AppPaths.SettingsFile);
        base.OnFormClosed(e);
    }
}
