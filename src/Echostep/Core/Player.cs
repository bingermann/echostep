using System.Collections.Concurrent;
using System.Diagnostics;

namespace Echostep.Core;

/// <summary>Sends input. The Windows implementation uses SendInput.</summary>
public interface IInputDriver
{
    /// <summary>Current pointer position in screen pixels.</summary>
    (int X, int Y) Position { get; }
    void MoveTo(int x, int y);
    void Click(int x, int y, MouseButton button);
    /// <summary>Presses (down = true) or releases a mouse button where the pointer is.</summary>
    void ButtonEvent(MouseButton button, bool down);
    void Wheel(int amount, bool horizontal);
    void KeyPress(int vk, Modifiers modifiers);
    /// <summary>Presses (down = true) or releases one key.</summary>
    void KeyEvent(int vk, bool down);
    /// <summary>Types text (any characters, not just key names).</summary>
    void TypeText(string text) => throw new PlaybackException("typing text is not supported here");
}

/// <summary>A captured piece of the screen: 0x00RRGGBB pixels, row by row.</summary>
public sealed record ScreenImage(int X, int Y, int Width, int Height, int[] Pixels);

/// <summary>Reads the screen and finds windows. The Windows implementation uses GDI and user32.</summary>
public interface IScreenReader
{
    /// <summary>Color at a physical screen pixel, or null if it cannot be read (off-screen, desktop locked).</summary>
    Rgb? GetPixel(int x, int y);

    /// <summary>Screen position of the client-area origin of the first visible window whose title matches, or null.</summary>
    (int X, int Y)? FindWindowClientOrigin(string title);

    /// <summary>Captures a zone, or the entire screen when width or height is 0. Null if the screen cannot be read.</summary>
    ScreenImage? Capture(int x, int y, int width, int height);

    /// <summary>Finds the window an action describes, brings it to the front and moves/resizes it. False if not found.</summary>
    bool FocusWindow(WindowFocusAction action);

    /// <summary>Titles of the open top-level windows (minimized ones included), for the If action.</summary>
    IEnumerable<string> WindowTitles() => [];

    /// <summary>Title of the window in front, or null.</summary>
    string? ForegroundTitle() => null;
}

public interface IClock
{
    long NowMs { get; }
    /// <summary>Sleeps, throwing <see cref="OperationCanceledException"/> as soon as the token is cancelled.</summary>
    void Sleep(int ms, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long NowMs => _sw.ElapsedMilliseconds;

    public void Sleep(int ms, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException(ct);
    }
}

public enum PlayOutcome
{
    /// <summary>Ran past the last row or jumped to end.</summary>
    Finished,
    /// <summary>Stopped by the user (Stop button or hotkey).</summary>
    Cancelled,
    /// <summary>The macro itself stopped: a timeout or jump with target "stop".</summary>
    StoppedByMacro,
    /// <summary>An action could not run (missing window, invalid macro).</summary>
    Failed,
}

public sealed record PlayResult(PlayOutcome Outcome, string Message, int Row);

/// <summary>How a run differs from "play everything once".</summary>
public sealed class PlayOptions
{
    /// <summary>Row to start at (0-based). Jumps may still go to rows above it.</summary>
    public int FirstRow { get; set; }
    /// <summary>Last row to run (inclusive); null runs to the end. Moving past it finishes the run.</summary>
    public int? LastRow { get; set; }
    /// <summary>Finish as soon as the row pointer leaves FirstRow…LastRow in either direction (Play selected).</summary>
    public bool StopOutsideRange { get; set; }
    /// <summary>How many times the whole macro runs.</summary>
    public int RepeatCount { get; set; } = 1;
    public bool IgnoreBreakpoints { get; set; }
    /// <summary>Action types that run; others are skipped.</summary>
    public PlaybackFilter Filter { get; set; } = PlaybackFilter.All;
    public MousePathMode MousePath { get; set; } = MousePathMode.AsRecorded;
    /// <summary>Playback speed: 2 halves every Wait and mouse-move time, 0.5 doubles them. Timeouts are not changed.</summary>
    public double Speed { get; set; } = 1;
}

/// <summary>A physical screen pixel being watched by a pixel action.</summary>
public sealed record TrackedPixel(int X, int Y);

/// <summary>
/// Runs a list of actions with a row pointer. After each action the pointer moves to the next row
/// unless the action jumps. Call <see cref="Run"/> on a background thread.
/// </summary>
public sealed class Player
{
    public const int PixelPollMs = 25;
    public const int ScreenPollMs = 100;
    public const int ImagePollMs = 200;
    /// <summary>Pause between wheel notches when one Wheel row turns it several notches.</summary>
    public const int WheelStepMs = 15;

    private readonly List<MacroAction> _actions;
    private readonly IInputDriver _input;
    private readonly IScreenReader _screen;
    private readonly IClock _clock;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _now;
    private readonly PlayOptions _options;
    private readonly Labels _labels;
    private readonly Random _random = new();
    private readonly Dictionary<int, int> _repeatCounters = new();
    private readonly Dictionary<int, long> _repeatStarted = new();
    private readonly Dictionary<int, DateTime> _repeatUntil = new();
    // Live Value-column state, read by the UI thread. Timers belong to the row that is running and are
    // dropped when it finishes; counters (Repeat) last while their loop is going.
    private readonly ConcurrentDictionary<int, LiveState> _timers = new();
    private readonly ConcurrentDictionary<int, LiveState> _counters = new();
    private readonly IHostServices? _host;
    private readonly ConcurrentDictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingWindowsLogged = new(StringComparer.OrdinalIgnoreCase);
    private (int X, int Y)? _previousPoint;
    private volatile int _currentRow = -1;
    private volatile TrackedPixel? _tracked;
    private long _trackedTick;
    private volatile bool _paused, _stepOnce;
    private volatile string _status = "";
    private volatile int _pass = 1;

    public Player(IReadOnlyList<MacroAction> actions, IInputDriver input, IScreenReader screen,
        IClock? clock = null, Action<string>? log = null, Func<DateTime>? now = null, PlayOptions? options = null,
        IHostServices? host = null)
    {
        _host = host;
        // Snapshot: edits made elsewhere while playing cannot change what runs.
        _actions = MacroFile.CloneAll(actions);
        _input = input;
        _screen = screen;
        _clock = clock ?? SystemClock.Instance;
        _log = log;
        _now = now ?? (() => DateTime.Now);
        _options = options ?? new PlayOptions();
        _labels = Labels.Build(_actions);
    }

    /// <summary>0-based row being run, or -1 when not running. Safe to read from the UI thread.</summary>
    public int CurrentRow => _currentRow;

    /// <summary>Which pass of the whole macro is running (1-based), when a repeat count is set.</summary>
    public int Pass => _pass;

    /// <summary>What a waiting action is doing ("Waiting for pixel color (12 s left)"), or empty.</summary>
    public string Status => _status;

    /// <summary>True while playback is halted before a row (pause key, Pause button or a breakpoint).</summary>
    public bool IsPaused => _paused;

    public void Pause() => _paused = true;

    public void Resume() { _stepOnce = false; _paused = false; }

    public void TogglePause() { if (_paused) Resume(); else Pause(); }

    /// <summary>While paused: runs one row and pauses again.</summary>
    public void Step() { _stepOnce = true; _paused = false; }

    /// <summary>
    /// The screen pixel a pixel action is reading now (or read within the last 400 ms), or null.
    /// The UI draws a crosshair there. Safe to read from the UI thread.
    /// </summary>
    public TrackedPixel? Tracked =>
        _tracked is { } t && Environment.TickCount64 - Volatile.Read(ref _trackedTick) < 400 ? t : null;

    /// <summary>
    /// Rows whose Value column is counting right now (a wait counting down, a repeat's runs left…),
    /// with the text to show. Every other row shows its normal value. Safe to read from the UI thread.
    /// </summary>
    public IReadOnlyDictionary<int, string> LiveValues()
    {
        var now = _clock.NowMs;
        var result = new Dictionary<int, string>();
        foreach (var (row, state) in _counters)
            if (LiveText.Describe(_actions[row], state, now) is { } text) result[row] = text;
        foreach (var (row, state) in _timers)
            if (LiveText.Describe(_actions[row], state, now) is { } text) result[row] = text;
        return result;
    }

    private void StartTimer(int row, long ms) => _timers[row] = new LiveState(EndMs: _clock.NowMs + ms);

    /// <summary>The macro's variables right now (name → value). Safe to read from the UI thread.</summary>
    public IReadOnlyDictionary<string, string> VariableValues => new Dictionary<string, string>(_vars, StringComparer.OrdinalIgnoreCase);

    /// <summary>A variable's value, or a built-in one ({date}, {time}, {clipboard}); null if unknown.</summary>
    public string? Lookup(string name)
    {
        if (_vars.TryGetValue(name, out var v)) return v;
        return name.ToLowerInvariant() switch
        {
            "date" => _now().ToString("yyyy-MM-dd"),
            "time" => _now().ToString("HH:mm:ss"),
            "clipboard" => _host?.GetClipboard(),
            _ => null,
        };
    }

    private string Expand(string? text) => Variables.Expand(text, Lookup);

    private IHostServices Host => _host ?? throw new PlaybackException("this action is not available here");

    /// <summary>A Wait or move time at the chosen playback speed.</summary>
    private int Scaled(int ms)
    {
        var speed = _options.Speed is > 0 and < 1000 ? _options.Speed : 1;
        return speed == 1 ? ms : (int)Math.Min(int.MaxValue, Math.Round(ms / speed));
    }

    /// <summary>Total number of actions executed in this run (for tests and diagnostics).</summary>
    public long StepsExecuted { get; private set; }

    public PlayResult Run(CancellationToken ct)
    {
        var problem = MacroValidator.FirstProblem(_actions);
        if (problem is not null) return new PlayResult(PlayOutcome.Failed, problem, -1);

        var first = Math.Clamp(_options.FirstRow, 0, Math.Max(0, _actions.Count - 1));
        var last = Math.Min(_options.LastRow ?? int.MaxValue, _actions.Count - 1);
        var passes = Math.Max(1, _options.RepeatCount);
        var stepsWithoutSleep = 0;
        var breakpointDone = -1;
        _vars.Clear();
        try
        {
            for (var pass = 1; pass <= passes; pass++)
            {
                _pass = pass;
                _repeatCounters.Clear();
                _repeatStarted.Clear();
                _repeatUntil.Clear();
                _counters.Clear();
                var pc = first;
                // Play from selected only chooses where to start: a jump back above that row (for example a
                // Repeat or Goto to an earlier label) keeps playing. Only Play selected is held to its rows.
                var floor = _options.StopOutsideRange ? first : 0;
                while (pc >= floor && pc <= last)
                {
                    ct.ThrowIfCancellationRequested();
                    var action = _actions[pc];
                    _currentRow = pc;

                    if (action.Disabled || (action.Category != PlaybackFilter.None && !_options.Filter.HasFlag(action.Category)))
                    {
                        pc++;
                        continue;
                    }

                    if (action.Breakpoint && !_options.IgnoreBreakpoints && breakpointDone != pc && !_paused)
                    {
                        _paused = true;
                        _log?.Invoke($"Row {pc + 1}: paused at breakpoint.");
                    }
                    if (_paused)
                    {
                        _status = "";
                        while (_paused)
                        {
                            if (ct.WaitHandle.WaitOne(20)) throw new OperationCanceledException(ct);
                        }
                    }
                    breakpointDone = action.Breakpoint ? pc : -1;
                    if (_stepOnce) { _stepOnce = false; _paused = true; } // run this row, then halt again

                    StepsExecuted++;
                    int next;
                    try
                    {
                        next = Execute(action, pc, ct);
                    }
                    catch (PlaybackException ex)
                    {
                        return new PlayResult(PlayOutcome.Failed, $"Row {pc + 1}: {ex.Message}", pc);
                    }
                    finally
                    {
                        _status = "";
                        _timers.TryRemove(pc, out _); // the row's own value comes back when it is done
                    }

                    if (next == Labels.StopIndex)
                        return new PlayResult(PlayOutcome.StoppedByMacro, StopReason(action, pc), pc);
                    if (next != pc) breakpointDone = -1;

                    // A tight loop of instant rows (Goto, If, Set variable…) would otherwise spin a core flat out.
                    if (action is GotoAction or RepeatAction or IfPixelAction or IfAction or SetVariableAction)
                    {
                        if (++stepsWithoutSleep >= 10_000) { _clock.Sleep(1, ct); stepsWithoutSleep = 0; }
                    }
                    else stepsWithoutSleep = 0;

                    pc = next;
                }
            }
            return new PlayResult(PlayOutcome.Finished, "Finished", _actions.Count);
        }
        catch (OperationCanceledException)
        {
            return new PlayResult(PlayOutcome.Cancelled, "Stopped", _currentRow);
        }
        finally
        {
            _currentRow = -1;
            _paused = false;
            _status = "";
            _timers.Clear();
            _counters.Clear();
        }
    }

    private static string StopReason(MacroAction a, int row) => a switch
    {
        GotoAction or RepeatAction or IfPixelAction or IfAction or MessageAction or SetVariableAction or TypeTextAction or RunAction
            => $"Row {row + 1}: stop.",
        FindImageAction { Once: true } => $"Row {row + 1}: image not found; stopping.",
        _ => $"Row {row + 1}: {a.DisplayName} timed out; stopping.",
    };

    /// <summary>Runs one action and returns the next row index (or <see cref="Labels.StopIndex"/>).</summary>
    private int Execute(MacroAction action, int pc, CancellationToken ct)
    {
        switch (action)
        {
            case ClickAction c:
            {
                var (x, y) = c.Relative ? Offset(_input.Position, c.X, c.Y) : ScreenPoint(c);
                if (c.Wiggle) { x += _random.Next(-2, 3); y += _random.Next(-2, 3); }
                switch (c.Kind)
                {
                    case ClickKind.Double:
                        _input.Click(x, y, c.Button);
                        _clock.Sleep(80, ct);
                        _input.Click(x, y, c.Button);
                        break;
                    case ClickKind.Down:
                        _input.MoveTo(x, y);
                        _input.ButtonEvent(c.Button, true);
                        break;
                    case ClickKind.Up:
                        _input.MoveTo(x, y);
                        _input.ButtonEvent(c.Button, false);
                        break;
                    default:
                        _input.Click(x, y, c.Button);
                        break;
                }
                _previousPoint = (x, y);
                return pc + 1;
            }

            case MoveAction m:
                Move(m, ct);
                return pc + 1;

            case WheelAction w:
            {
                // One notch at a time, like a real wheel: many programs scroll only a few lines per wheel message
                // however big it is, so one huge message (or a merged recording) would barely move.
                var left = w.Value;
                while (left != 0)
                {
                    var step = Math.Sign(left) * Math.Min(Math.Abs(left), WheelAction.Notch);
                    _input.Wheel(step, w.Horizontal);
                    left -= step;
                    if (left != 0) _clock.Sleep(WheelStepMs, ct);
                }
                return pc + 1;
            }

            case KeyAction k:
                if (!KeyNames.TryGetVk(k.Key, out var vk)) throw new PlaybackException($"unknown key \"{k.Key}\"");
                switch (k.Option)
                {
                    case KeyOption.Down: _input.KeyEvent(vk, true); break;
                    case KeyOption.Up: _input.KeyEvent(vk, false); break;
                    default:
                        for (var i = 0; i < k.Count; i++)
                        {
                            if (k.Count > 1) _timers[pc] = new LiveState(Count: k.Count - i);
                            if (i > 0) _clock.Sleep(20, ct);
                            _input.KeyPress(vk, k.Modifiers);
                        }
                        break;
                }
                return pc + 1;

            case WaitAction w:
            {
                var ms = Scaled(w.Ms);
                StartTimer(pc, ms);
                SleepWithStatus(ms, ct);
                return pc + 1;
            }

            case WaitUntilAction wu:
            {
                if (!WaitUntilAction.TryParseTime(wu.Time, out var time)) throw new PlaybackException($"\"{wu.Time}\" is not a time of day");
                var start = _now();
                var target = WaitUntilAction.NextOccurrence(start, time);
                _log?.Invoke($"Row {pc + 1}: waiting until {WaitUntilAction.Format(time)}{(target.Date > start.Date ? " tomorrow" : "")}.");
                _status = $"Waiting until {WaitUntilAction.Format(time)}…";
                // Re-check the wall clock at least every second, so sleep/resume and clock changes are handled.
                while (true)
                {
                    var left = (target - _now()).TotalMilliseconds;
                    if (left <= 0) return pc + 1;
                    _clock.Sleep((int)Math.Min(left, 1000) + (left < 1000 ? 1 : 0), ct);
                }
            }

            case WaitPixelAction wp:
            {
                var target = Rgb.Parse(wp.Color);
                var deadline = _clock.NowMs + wp.TimeoutMs;
                if (wp.TimeoutMs > 0) StartTimer(pc, wp.TimeoutMs);
                while (true)
                {
                    var c = ReadPixel(wp);
                    if (c is { } col && Rgb.Matches(col, target, wp.Tolerance, wp.Match)) return JumpOrNext(wp.Then, pc);
                    if (TimedOut(wp.TimeoutMs, deadline, "Waiting for pixel color")) return Timeout(wp, wp.TimeoutMs, wp.OnTimeout, pc);
                    _clock.Sleep(PixelPollMs, ct);
                }
            }

            case WaitPixelChangeAction wc:
            {
                var deadline = _clock.NowMs + wc.TimeoutMs;
                if (wc.TimeoutMs > 0) StartTimer(pc, wc.TimeoutMs);
                Rgb? baseline = null;
                while (true)
                {
                    var c = ReadPixel(wc);
                    if (c is { } col)
                    {
                        if (baseline is null) baseline = col;
                        else if (!Rgb.Matches(col, baseline.Value, wc.Tolerance, wc.Match)) return JumpOrNext(wc.Then, pc);
                    }
                    if (TimedOut(wc.TimeoutMs, deadline, "Waiting for pixel change")) return Timeout(wc, wc.TimeoutMs, wc.OnTimeout, pc);
                    _clock.Sleep(PixelPollMs, ct);
                }
            }

            case WaitScreenChangeAction ws:
            {
                var deadline = _clock.NowMs + ws.TimeoutMs;
                if (ws.TimeoutMs > 0) StartTimer(pc, ws.TimeoutMs);
                ScreenImage? baseline = null;
                while (true)
                {
                    var frame = ws.EntireScreen ? _screen.Capture(0, 0, 0, 0) : _screen.Capture(ws.X, ws.Y, ws.Width, ws.Height);
                    if (frame is not null)
                    {
                        if (baseline is null) baseline = frame;
                        else if (ScreenDiff.ChangedBounds(baseline, frame, ws.Tolerance) is { } box)
                        {
                            MouseOnBox(ws.Mouse, ws.Anchor, box, ct);
                            return JumpOrNext(ws.Then, pc);
                        }
                    }
                    if (TimedOut(ws.TimeoutMs, deadline, "Waiting for screen changes")) return Timeout(ws, ws.TimeoutMs, ws.OnTimeout, pc);
                    _clock.Sleep(ScreenPollMs, ct);
                }
            }

            case IfPixelAction ip:
            {
                var target = Rgb.Parse(ip.Color);
                var c = ReadPixel(ip);
                var match = c is { } col && Rgb.Matches(col, target, ip.Tolerance, ip.Match);
                return JumpOrNext(match ? ip.Then : ip.Else, pc);
            }

            case RepeatAction r:
                return Repeat(r, pc);

            case IfAction cond:
            {
                var yes = cond.Evaluate(_now(), _screen, Lookup);
                _log?.Invoke($"Row {pc + 1}: {cond.ConditionText}: {(yes ? "yes" : "no")}.");
                return JumpOrNext(yes ? cond.Then : cond.Else, pc);
            }

            case GotoAction g:
                return Jump(g.Target, pc);

            case WindowFocusAction wf:
            {
                var deadline = _clock.NowMs + wf.TimeoutMs;
                if (wf.TimeoutMs > 0) StartTimer(pc, wf.TimeoutMs);
                while (true)
                {
                    if (_screen.FocusWindow(wf)) return JumpOrNext(wf.Then, pc);
                    if (TimedOut(wf.TimeoutMs, deadline, $"Waiting for window {wf.Describe}")) return Timeout(wf, wf.TimeoutMs, wf.OnTimeout, pc);
                    _clock.Sleep(ScreenPollMs, ct);
                }
            }

            case FindImageAction fi:
                return FindImage(fi, pc, ct);

            case TypeTextAction tt:
            {
                var text = Expand(tt.Text).Replace("\r\n", "\n");
                if (tt.CharDelayMs <= 0)
                {
                    _input.TypeText(text);
                    return pc + 1;
                }
                var delay = Scaled(tt.CharDelayMs);
                var chars = System.Globalization.StringInfo.GetTextElementEnumerator(text);
                var first = true;
                while (chars.MoveNext())
                {
                    if (!first) _clock.Sleep(delay, ct);
                    first = false;
                    _input.TypeText((string)chars.Current);
                }
                return pc + 1;
            }

            case SetVariableAction sv:
            {
                var name = sv.Name.Trim();
                var value = Expand(sv.Value);
                switch (sv.Operation)
                {
                    case VariableOperation.Add or VariableOperation.Subtract:
                    {
                        var current = Lookup(name);
                        if (!Variables.TryNumber(string.IsNullOrWhiteSpace(current) ? "0" : current, out var a))
                            throw new PlaybackException($"{name} is \"{current}\", not a number");
                        if (!Variables.TryNumber(value, out var b)) throw new PlaybackException($"\"{value}\" is not a number");
                        _vars[name] = Variables.FormatNumber(sv.Operation == VariableOperation.Add ? a + b : a - b);
                        break;
                    }
                    case VariableOperation.FromClipboard:
                        _vars[name] = Host.GetClipboard() ?? "";
                        break;
                    case VariableOperation.ToClipboard:
                        Host.SetClipboard(value);
                        break;
                    default:
                        _vars[name] = value;
                        break;
                }
                return pc + 1;
            }

            case RunAction run:
            {
                var target = Expand(run.Target).Trim();
                _log?.Invoke($"Row {pc + 1}: opening {target}.");
                if (run.Wait) _status = $"Waiting for {Path.GetFileName(target)} to close…";
                try
                {
                    Host.Run(target, string.IsNullOrWhiteSpace(run.Arguments) ? null : Expand(run.Arguments), run.Wait, ct);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or PlaybackException))
                {
                    throw new PlaybackException($"could not open \"{target}\": {ex.Message}");
                }
                return pc + 1;
            }

            case MessageAction msg:
            {
                if (msg.Sound != MessageSound.None) Host.PlaySound(msg.Sound);
                if (msg.Box == MessageBoxKind.None) return JumpOrNext(msg.Then, pc);
                _status = msg.Box == MessageBoxKind.YesNo ? "Waiting for an answer…" : "Waiting for the message to be closed…";
                var yes = Host.ShowMessage(Expand(msg.Text), msg.Box == MessageBoxKind.YesNo);
                ct.ThrowIfCancellationRequested();
                return JumpOrNext(msg.Box == MessageBoxKind.YesNo && !yes ? msg.Else : msg.Then, pc);
            }

            default:
                throw new PlaybackException($"{action.DisplayName} is not supported by this build");
        }
    }

    private int FindImage(FindImageAction fi, int pc, CancellationToken ct)
    {
        if (!ImagePattern.TryDecode(fi.Image, out var pattern)) throw new PlaybackException("the image is missing or damaged; capture it again");
        var deadline = _clock.NowMs + fi.TimeoutMs;
        if (!fi.Once && fi.TimeoutMs > 0) StartTimer(pc, fi.TimeoutMs);
        while (true)
        {
            var frame = fi.EntireScreen ? _screen.Capture(0, 0, 0, 0) : _screen.Capture(fi.X, fi.Y, fi.Width, fi.Height);
            if (frame is not null && ImageSearch.Find(frame, pattern!, fi.Tolerance) is { } at)
            {
                var box = (at.X, at.Y, pattern!.Width, pattern.Height);
                _vars["foundX"] = (at.X + pattern.Width / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _vars["foundY"] = (at.Y + pattern.Height / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _log?.Invoke($"Row {pc + 1}: image found at {at.X}, {at.Y}.");
                MouseOnBox(fi.Mouse, fi.Anchor, box, ct);
                if (fi.Mouse != ChangeMouseAction.None) _previousPoint = _input.Position;
                return JumpOrNext(fi.Then, pc);
            }
            if (fi.Once)
            {
                _log?.Invoke($"Row {pc + 1}: image not found.");
                return JumpOrNext(fi.OnTimeout, pc);
            }
            if (TimedOut(fi.TimeoutMs, deadline, "Looking for the image")) return Timeout(fi, fi.TimeoutMs, fi.OnTimeout, pc);
            _clock.Sleep(ImagePollMs, ct);
        }
    }

    /// <summary>Updates the countdown status; true when a timeout (0 = none) has expired.</summary>
    private bool TimedOut(int timeoutMs, long deadline, string what)
    {
        if (timeoutMs <= 0)
        {
            _status = what + "…";
            return false;
        }
        var left = deadline - _clock.NowMs;
        _status = $"{what} ({Math.Max(0, (left + 999) / 1000)} s left)";
        return left <= 0;
    }

    private int Timeout(MacroAction a, int timeoutMs, string? onTimeout, int pc)
    {
        var target = MacroAction.OnTimeoutOrStop(onTimeout);
        _log?.Invoke($"Row {pc + 1}: {a.DisplayName} timed out after {Durations.Format(timeoutMs)} → {target}");
        return Jump(target, pc);
    }

    private void SleepWithStatus(int ms, CancellationToken ct)
    {
        if (ms < 2000)
        {
            _clock.Sleep(ms, ct);
            return;
        }
        var end = _clock.NowMs + ms;
        while (true)
        {
            var left = end - _clock.NowMs;
            if (left <= 0) return;
            _status = $"Waiting {(left + 999) / 1000} seconds…";
            _clock.Sleep((int)Math.Min(left, 250), ct);
        }
    }

    private int Repeat(RepeatAction r, int pc)
    {
        bool again;
        switch (r.Mode)
        {
            case RepeatMode.Infinite:
                again = true;
                _counters[pc] = new LiveState(Count: (_counters.TryGetValue(pc, out var runs) ? runs.Count ?? 1 : 1) + 1);
                break;
            case RepeatMode.Seconds:
                if (!_repeatStarted.TryGetValue(pc, out var started)) _repeatStarted[pc] = started = _clock.NowMs;
                var total = (r.Seconds ?? 60) * 1000L;
                again = _clock.NowMs - started < total;
                if (again) _counters[pc] = new LiveState(EndMs: started + total);
                else
                {
                    _repeatStarted.Remove(pc);
                    _counters.TryRemove(pc, out _);
                }
                break;
            case RepeatMode.Until:
                if (!_repeatUntil.TryGetValue(pc, out var until))
                {
                    if (!WaitUntilAction.TryParseTime(r.Until, out var t)) throw new PlaybackException($"\"{r.Until}\" is not a time of day");
                    _repeatUntil[pc] = until = WaitUntilAction.NextOccurrence(_now(), t);
                }
                again = _now() < until;
                if (!again) _repeatUntil.Remove(pc);
                break;
            default:
                _repeatCounters.TryGetValue(pc, out var n);
                again = n < r.Count - 1;
                _repeatCounters[pc] = again ? n + 1 : 0;
                // Runs left, counting the one that starts now; the row shows its full count again once done.
                if (again) _counters[pc] = new LiveState(Count: r.Count - (n + 1));
                else _counters.TryRemove(pc, out _);
                break;
        }
        return again ? Jump(r.Target, pc) : JumpOrNext(r.After, pc);
    }

    private void Move(MoveAction m, CancellationToken ct)
    {
        (int X, int Y) end;
        if (m.Relative) end = Offset(_input.Position, m.X, m.Y);
        else
        {
            end = ScreenPoint(m);
            if (m.FromX is { } fx && m.FromY is { } fy)
            {
                var origin = string.IsNullOrWhiteSpace(m.Window) ? (0, 0) : (end.X - m.X, end.Y - m.Y);
                _input.MoveTo(origin.Item1 + fx, origin.Item2 + fy);
            }
        }

        var start = _input.Position;
        var mode = _options.MousePath;
        var duration = Scaled(m.TravelMs);
        if (duration > 0) StartTimer(_currentRow, duration);
        if (duration <= 0 && mode != MousePathMode.AsRecorded)
        {
            // Smoothing modes glide between recorded points; the glide time depends on the distance.
            var distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            duration = Scaled((int)Math.Clamp(distance * 0.5, 0, 150));
        }
        if (duration <= 0 || start == end)
        {
            _input.MoveTo(end.X, end.Y);
        }
        else
        {
            var steps = Math.Clamp(duration / 10, 1, 400);
            var points = MousePath.Build(start, end, mode, _previousPoint, steps, _random);
            var each = Math.Max(1, duration / steps);
            foreach (var p in points)
            {
                _input.MoveTo(p.X, p.Y);
                _clock.Sleep(each, ct);
            }
        }
        _previousPoint = start;
    }

    private void MouseOnBox(ChangeMouseAction mouse, ChangeAnchor anchor, (int X, int Y, int Width, int Height) box, CancellationToken ct)
    {
        if (mouse == ChangeMouseAction.None) return;
        var (x, y) = anchor switch
        {
            ChangeAnchor.TopLeft => (box.X, box.Y),
            ChangeAnchor.TopRight => (box.X + box.Width - 1, box.Y),
            ChangeAnchor.BottomLeft => (box.X, box.Y + box.Height - 1),
            ChangeAnchor.BottomRight => (box.X + box.Width - 1, box.Y + box.Height - 1),
            _ => (box.X + box.Width / 2, box.Y + box.Height / 2),
        };
        switch (mouse)
        {
            case ChangeMouseAction.Move: _input.MoveTo(x, y); break;
            case ChangeMouseAction.RightClick: _input.Click(x, y, MouseButton.Right); break;
            case ChangeMouseAction.MiddleClick: _input.Click(x, y, MouseButton.Middle); break;
            case ChangeMouseAction.DoubleClick:
                _input.Click(x, y, MouseButton.Left);
                _clock.Sleep(80, ct);
                _input.Click(x, y, MouseButton.Left);
                break;
            default: _input.Click(x, y, MouseButton.Left); break;
        }
    }

    private static (int X, int Y) Offset((int X, int Y) p, int dx, int dy) => (p.X + dx, p.Y + dy);

    private int Jump(string target, int pc) =>
        _labels.Resolve(target, pc) ?? throw new PlaybackException($"Target label not found! (\"{target}\")");

    private int JumpOrNext(string? target, int pc) => string.IsNullOrWhiteSpace(target) ? pc + 1 : Jump(target, pc);

    private (int X, int Y) ScreenPoint(PointAction p)
    {
        if (string.IsNullOrWhiteSpace(p.Window)) return (p.X, p.Y);
        var origin = _screen.FindWindowClientOrigin(p.Window)
                     ?? throw new PlaybackException($"window \"{p.Window}\" not found");
        return (origin.X + p.X, origin.Y + p.Y);
    }

    /// <summary>Reads an action's pixel; null (no match) if the window is missing or the read fails.</summary>
    private Rgb? ReadPixel(PixelActionBase p)
    {
        int x = p.X, y = p.Y;
        if (!string.IsNullOrWhiteSpace(p.Window))
        {
            var origin = _screen.FindWindowClientOrigin(p.Window);
            if (origin is null)
            {
                if (_missingWindowsLogged.Add(p.Window))
                    _log?.Invoke($"Window \"{p.Window}\" not found; its pixel reads as no match.");
                return null;
            }
            x += origin.Value.X;
            y += origin.Value.Y;
        }
        if (_tracked is not { } t || t.X != x || t.Y != y) _tracked = new TrackedPixel(x, y);
        Volatile.Write(ref _trackedTick, Environment.TickCount64);
        return _screen.GetPixel(x, y);
    }
}

public sealed class PlaybackException(string message) : Exception(message);

/// <summary>How recorded mouse moves are replayed.</summary>
public enum MousePathMode { AsRecorded, Linear, Spline, Natural }

/// <summary>Builds the points the pointer passes through on its way from one place to another.</summary>
public static class MousePath
{
    /// <param name="before">Where the pointer was before <paramref name="from"/>; gives splines their direction.</param>
    public static List<(int X, int Y)> Build((int X, int Y) from, (int X, int Y) to, MousePathMode mode,
        (int X, int Y)? before, int steps, Random random)
    {
        steps = Math.Max(1, steps);
        var result = new List<(int X, int Y)>(steps);
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        // Natural: a gentle curve to one side with a slow start and finish, like a hand.
        double bend = 0, nx = 0, ny = 0;
        if (mode == MousePathMode.Natural && length > 1)
        {
            nx = -dy / length;
            ny = dx / length;
            bend = length * (0.04 + random.NextDouble() * 0.08) * (random.Next(2) == 0 ? 1 : -1);
        }

        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            double x, y;
            switch (mode)
            {
                case MousePathMode.Spline:
                {
                    // Catmull-Rom through: before → from → to → (to, continued).
                    var p0 = before ?? from;
                    (double X, double Y) p3 = (to.X + dx * 0.5, to.Y + dy * 0.5);
                    x = CatmullRom(p0.X, from.X, to.X, p3.X, t);
                    y = CatmullRom(p0.Y, from.Y, to.Y, p3.Y, t);
                    break;
                }
                case MousePathMode.Natural:
                {
                    var eased = t * t * (3 - 2 * t);
                    var arc = Math.Sin(Math.PI * eased) * bend;
                    x = from.X + dx * eased + nx * arc;
                    y = from.Y + dy * eased + ny * arc;
                    break;
                }
                default:
                    x = from.X + dx * t;
                    y = from.Y + dy * t;
                    break;
            }
            result.Add(i == steps ? to : ((int)Math.Round(x), (int)Math.Round(y)));
        }
        return result;
    }

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }
}

/// <summary>Compares two captures of the same area.</summary>
public static class ScreenDiff
{
    /// <summary>
    /// Screen-coordinate bounding box of the pixels that differ by more than <paramref name="tolerance"/>
    /// on any channel, or null if nothing changed.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? ChangedBounds(ScreenImage before, ScreenImage after, int tolerance)
    {
        if (before.Width != after.Width || before.Height != after.Height || before.Pixels.Length != after.Pixels.Length)
            return (after.X, after.Y, Math.Max(1, after.Width), Math.Max(1, after.Height));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        int w = before.Width, h = before.Height;
        int[] a = before.Pixels, b = after.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                int p = a[row + x], q = b[row + x];
                if (p == q) continue;
                if (tolerance > 0 &&
                    Math.Abs(((p >> 16) & 255) - ((q >> 16) & 255)) <= tolerance &&
                    Math.Abs(((p >> 8) & 255) - ((q >> 8) & 255)) <= tolerance &&
                    Math.Abs((p & 255) - (q & 255)) <= tolerance) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : (after.X + minX, after.Y + minY, maxX - minX + 1, maxY - minY + 1);
    }
}
