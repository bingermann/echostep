// Self-contained test runner (no NuGet packages needed): dotnet run --project tests/CoreTests
using System.Runtime.CompilerServices;
using Echostep.Core;

var failures = 0;
var passed = 0;

void Check(bool condition, string what, [CallerLineNumber] int line = 0)
{
    if (condition) { passed++; return; }
    failures++;
    Console.WriteLine($"  FAIL (line {line}): {what}");
}

void Eq<T>(T expected, T actual, string what, [CallerLineNumber] int line = 0) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected <{expected}>, got <{actual}>", line);

void Test(string name, Action body)
{
    Console.WriteLine(name);
    try { body(); }
    catch (Exception ex) { failures++; Console.WriteLine($"  FAIL: threw {ex.GetType().Name}: {ex.Message}"); }
}

const string SpecExample = """
{
  "format": "Echostep",
  "version": 1,
  "actions": [
    { "type": "click", "x": 400, "y": 300, "button": "left" },
    { "type": "waitPixel", "x": 960, "y": 640, "color": "#2D7DD2",
"tolerance": 12, "timeoutMs": 5000, "onTimeout": "retry" },
    { "type": "key", "key": "Enter" },
    { "type": "repeat", "target": "start", "count": 10 },
    { "type": "goto", "target": "end" },
    { "label": "retry", "type": "key", "key": "Escape" },
    { "type": "goto", "target": "start" }
  ]
}
""";

var blue = Rgb.Parse("#2D7DD2");
var grey = Rgb.Parse("#808080");

Test("Spec example parses (label before type, multi-line object)", () =>
{
    var a = MacroFile.Parse(SpecExample);
    Eq(7, a.Count, "action count");
    Check(a[0] is ClickAction { X: 400, Y: 300, Button: MouseButton.Left }, "row 1 click");
    Check(a[1] is WaitPixelAction { X: 960, Y: 640, Color: "#2D7DD2", Tolerance: 12, TimeoutMs: 5000, OnTimeout: "retry" }, "row 2 waitPixel");
    Check(a[2] is KeyAction { Key: "Enter" }, "row 3 key");
    Check(a[3] is RepeatAction { Target: "start", Count: 10 }, "row 4 repeat");
    Check(a[4] is GotoAction { Target: "end" }, "row 5 goto");
    Check(a[5] is KeyAction { Key: "Escape", Label: "retry" }, "row 6 labelled key");
    Eq<string?>(null, MacroValidator.FirstProblem(a), "valid");
    Eq("960, 640 = #2D7DD2 ±12, timeout 5 s → retry", a[1].Parameters, "waitPixel parameters text");
    Eq("→ start, 10 times", a[3].Parameters, "repeat parameters text");
});

Test("Save/load round trip keeps version 1 for v1-only macros", () =>
{
    var a = MacroFile.Parse(SpecExample);
    var json = MacroFile.Serialize(a);
    Check(json.Contains("\"version\": 1"), "version 1 written");
    Check(!json.Contains("displayName") && !json.Contains("parameters"), "display-only properties are not saved");
    Check(json.Contains("\"label\":\"retry\""), "label written");
    Check(!json.Contains("\"ctrl\""), "false modifiers omitted");
    Check(!json.Contains("\"window\""), "null window omitted");
    Check(!json.Contains("\"match\""), "default match mode omitted");
    var b = MacroFile.Parse(json);
    Eq(MacroFile.Serialize(b), json, "stable round trip");
    a.Add(new IfPixelAction { X = 1, Y = 2, Color = "#FFFFFF", Then = "retry", Match = MatchMode.Distance });
    var v2 = MacroFile.Serialize(a);
    Check(v2.Contains("\"version\": 2"), "Phase 2 action bumps version");
    var c = MacroFile.Parse(v2);
    Check(c[^1] is IfPixelAction { Then: "retry", Else: null, Match: MatchMode.Distance }, "ifPixel round trip");
    Check(v2.Contains("\"match\":\"distance\""), "distance written");
});

Test("Keypress with modifiers round trip", () =>
{
    var k = new KeyAction { Key = "S", Ctrl = true, Shift = true };
    var json = MacroFile.Serialize([k]);
    var back = (KeyAction)MacroFile.Parse(json)[0];
    Eq(Modifiers.Ctrl | Modifiers.Shift, back.Modifiers, "modifiers");
    Eq("Ctrl+Shift+S", back.Parameters, "parameters text");
    Eq("Escape", ((KeyAction)MacroFile.Parse("""{"actions":[{"type":"key","key":"esc"}]}""")[0]).Key, "alias normalised");
});

Test("Bad files are rejected with readable errors", () =>
{
    void Throws(string json, string contains)
    {
        try { MacroFile.Parse(json); Check(false, $"expected error containing '{contains}'"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains(contains), $"message '{ex.Message}' contains '{contains}'"); }
    }
    Throws("""{"format":"Echostep","version":99,"actions":[]}""", "newer");
    Throws("""{"format":"Other","version":1,"actions":[]}""", "not an Echostep");
    Throws("""{"actions":[{"type":"teleport"}]}""", "Not a valid");
    Throws("not json", "Not a valid");
});

Test("Labels: built-ins, row numbers, custom labels", () =>
{
    var a = MacroFile.Parse(SpecExample);
    var l = Labels.Build(a);
    Eq<int?>(0, l.Resolve("start"), "start");
    Eq<int?>(0, l.Resolve("START"), "case-insensitive start");
    Eq<int?>(7, l.Resolve("end"), "end = past last row");
    Eq<int?>(Labels.StopIndex, l.Resolve("stop"), "stop");
    Eq<int?>(2, l.Resolve("3"), "row number 3");
    Eq<int?>(null, l.Resolve("8"), "row number past end");
    Eq<int?>(null, l.Resolve("0"), "row 0");
    Eq<int?>(5, l.Resolve("Retry"), "custom label case-insensitive");
    Eq<int?>(null, l.Resolve("nope"), "missing");
    Check(Labels.IsReserved("End") && Labels.IsReserved("12") && Labels.IsReserved("stop"), "reserved");
    Check(Labels.CheckCustomLabel("RETRY", a, -1) is not null, "duplicate rejected");
    Check(Labels.CheckCustomLabel("retry", a, 5) is null, "own label allowed");
    Check(Labels.CheckCustomLabel("start", a, -1) is not null, "start rejected");
    Check(Labels.CheckCustomLabel("42", a, -1) is not null, "number rejected");
});

Test("Validator flags missing targets and duplicate labels, naming the row", () =>
{
    var a = MacroFile.Parse(SpecExample);
    ((GotoAction)a[6]).Target = "retry2";
    a[0].Label = "Retry";
    var p = MacroValidator.Validate(a);
    Check(p.ContainsKey(6), "row 7 flagged");
    Check(p.ContainsKey(5), "duplicate on row 6 flagged");
    Eq("Row 6: Label \"retry\" is already used on row 1.", MacroValidator.FirstProblem(a), "first problem");
    var player = new Player(a, new FakeInput(), new FakeScreen(), new FakeClock());
    var r = player.Run(CancellationToken.None);
    Eq(PlayOutcome.Failed, r.Outcome, "Play refuses to start");
});

Test("Player: spec example, dialog always appears → 10 clicks, 10 Enters", () =>
{
    var screen = new FakeScreen();
    var input = new FakeInput();
    input.OnClick = (_, _) => screen.Set(960, 640, blue);
    input.OnKey = (_, _) => screen.Set(960, 640, grey);
    var clock = new FakeClock();
    var r = new Player(MacroFile.Parse(SpecExample), input, screen, clock).Run(CancellationToken.None);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Eq(10, input.Clicks.Count, "clicks");
    Eq(10, input.Keys.Count(k => k.Vk == 0x0D), "Enters");
    Eq(0, input.Keys.Count(k => k.Vk == 0x1B), "Escapes");
});

Test("Player: dialog fails to appear every 3rd click → Escape and start over", () =>
{
    var screen = new FakeScreen();
    var input = new FakeInput();
    var clicks = 0;
    input.OnClick = (_, _) => { if (++clicks % 3 != 0) screen.Set(960, 640, blue); };
    input.OnKey = (_, _) => screen.Set(960, 640, grey);
    var clock = new FakeClock();
    var log = new List<string>();
    var r = new Player(MacroFile.Parse(SpecExample), input, screen, clock, log.Add).Run(CancellationToken.None);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Eq(10, input.Keys.Count(k => k.Vk == 0x0D), "still exactly 10 Enters");
    Eq(4, input.Keys.Count(k => k.Vk == 0x1B), "Escapes for clicks 3, 6, 9, 12");
    Check(clock.NowMs >= 4 * 5000, "timeouts took simulated time");
    Check(log.Any(l => l.Contains("timed out")), "timeout logged");
});

Test("Player: tolerance and distance matching", () =>
{
    var screen = new FakeScreen();
    screen.Set(5, 5, new Rgb(0x2D + 12, 0x7D - 12, 0xD2));
    var a = new List<MacroAction>
    {
        new IfPixelAction { X = 5, Y = 5, Color = "#2D7DD2", Tolerance = 12, Then = "end", Else = "stop" },
    };
    Eq(PlayOutcome.Finished, new Player(a, new FakeInput(), screen, new FakeClock()).Run(default).Outcome, "±12 matches");
    ((IfPixelAction)a[0]).Tolerance = 11;
    Eq(PlayOutcome.StoppedByMacro, new Player(a, new FakeInput(), screen, new FakeClock()).Run(default).Outcome, "±11 does not");
    ((IfPixelAction)a[0]).Match = MatchMode.Distance;
    ((IfPixelAction)a[0]).Tolerance = 17; // sqrt(144+144) ≈ 16.97
    Eq(PlayOutcome.Finished, new Player(a, new FakeInput(), screen, new FakeClock()).Run(default).Outcome, "distance 17 matches");
    ((IfPixelAction)a[0]).Tolerance = 16;
    Eq(PlayOutcome.StoppedByMacro, new Player(a, new FakeInput(), screen, new FakeClock()).Run(default).Outcome, "distance 16 does not");
});

Test("Player: If pixel with empty targets falls through", () =>
{
    var input = new FakeInput();
    var a = new List<MacroAction>
    {
        new IfPixelAction { X = 0, Y = 0, Color = "#FFFFFF", Then = "white" },
        new KeyAction { Key = "A" },
        new GotoAction { Target = "end" },
        new KeyAction { Key = "B", Label = "white" },
    };
    new Player(a, input, new FakeScreen(), new FakeClock()).Run(default);
    Eq("A", string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk))), "black pixel → next row");
    var screen = new FakeScreen();
    screen.Set(0, 0, new Rgb(255, 255, 255));
    input = new FakeInput();
    new Player(a, input, screen, new FakeClock()).Run(default);
    Eq("B", string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk))), "white pixel → Then");
});

Test("Player: Wait for pixel change", () =>
{
    var screen = new FakeScreen();
    screen.Set(1, 1, grey);
    var clock = new FakeClock();
    clock.At(300, () => screen.Set(1, 1, new Rgb(0x81, 0x80, 0x80))); // within tolerance 2
    clock.At(600, () => screen.Set(1, 1, blue));
    var a = new List<MacroAction>
    {
        new WaitPixelChangeAction { X = 1, Y = 1, Tolerance = 2, TimeoutMs = 2000 },
        new KeyAction { Key = "A" },
    };
    var input = new FakeInput();
    var r = new Player(a, input, screen, clock).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Check(clock.NowMs is >= 600 and < 700, $"continued right after the change (t={clock.NowMs})");
    Eq(1, input.Keys.Count, "key pressed");

    var still = new FakeScreen();
    var clock2 = new FakeClock();
    var r2 = new Player([new WaitPixelChangeAction { X = 1, Y = 1, TimeoutMs = 1000 }], new FakeInput(), still, clock2).Run(default);
    Eq(PlayOutcome.StoppedByMacro, r2.Outcome, "timeout → stop");
});

Test("Player: nested Repeat loops", () =>
{
    var input = new FakeInput();
    var a = new List<MacroAction>
    {
        new KeyAction { Key = "A", Label = "outer" },
        new KeyAction { Key = "B", Label = "inner" },
        new RepeatAction { Target = "inner", Count = 3 },
        new RepeatAction { Target = "outer", Count = 2 },
    };
    new Player(a, input, new FakeScreen(), new FakeClock()).Run(default);
    Eq("A,B,B,B,A,B,B,B", string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk))), "sequence");
    // Counters reset when Play starts.
    input.Keys.Clear();
    var p = new Player(a, input, new FakeScreen(), new FakeClock());
    p.Run(default); p.Run(default);
    Eq(16, input.Keys.Count, "two runs, same length each");
});

Test("Player: row-number targets are looked up at play time", () =>
{
    var input = new FakeInput();
    var a = new List<MacroAction>
    {
        new GotoAction { Target = "3" },
        new KeyAction { Key = "A" },
        new KeyAction { Key = "B" },
    };
    new Player(a, input, new FakeScreen(), new FakeClock()).Run(default);
    Eq("B", string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk))), "goto 3 → B");
    a.Insert(1, new KeyAction { Key = "C" });
    input.Keys.Clear();
    new Player(a, input, new FakeScreen(), new FakeClock()).Run(default);
    Eq("A,B", string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk))), "after insert, row 3 is A");
});

Test("Player: endless Goto loop stops on cancel", () =>
{
    var a = new List<MacroAction> { new GotoAction { Target = "start" } };
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var p = new Player(a, new FakeInput(), new FakeScreen(), SystemClock.Instance);
    var r = p.Run(cts.Token);
    Eq(PlayOutcome.Cancelled, r.Outcome, "cancelled");
    Check(p.StepsExecuted > 10_000, "it was looping");
    Eq(-1, p.CurrentRow, "current row reset");
});

Test("Player: window-relative coordinates", () =>
{
    var screen = new FakeScreen();
    screen.Windows["Notepad"] = (100, 200);
    screen.Set(110, 220, blue);
    var input = new FakeInput();
    var a = new List<MacroAction>
    {
        new WaitPixelAction { X = 10, Y = 20, Window = "Notepad", Color = blue.ToHex(), TimeoutMs = 100 },
        new ClickAction { X = 1, Y = 2, Window = "Notepad" },
    };
    var r = new Player(a, input, screen, new FakeClock()).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Eq((101, 202), input.Clicks[0], "click offset by client origin");
    screen.Windows.Clear();
    var log = new List<string>();
    var r2 = new Player(a, input, screen, new FakeClock(), log.Add).Run(default);
    Eq(PlayOutcome.StoppedByMacro, r2.Outcome, "missing window → pixel never matches → timeout");
    Eq(1, log.Count(l => l.Contains("not found")), "missing window logged once");
    var r3 = new Player([a[1]], input, screen, new FakeClock()).Run(default);
    Eq(PlayOutcome.Failed, r3.Outcome, "click in missing window fails");
    Check(r3.Message.StartsWith("Row 1:"), "failure names the row");
});

Test("Recorder: clicks, thinned moves, waits, modifiers", () =>
{
    var ev = new List<RecEvent>
    {
        new(RecEventKind.MouseMove, 0, 10, 10),
        new(RecEventKind.MouseMove, 10, 13, 13),   // < 8 px from last kept → dropped
        new(RecEventKind.MouseMove, 20, 30, 10),   // kept
        new(RecEventKind.MouseDown, 40, 30, 10, MouseButton.Left),
        new(RecEventKind.MouseUp, 90, 30, 10, MouseButton.Left),
        new(RecEventKind.KeyDown, 500, Vk: KeyNames.VK_LCONTROL),
        new(RecEventKind.KeyDown, 520, Vk: 'S'),
        new(RecEventKind.KeyUp, 560, Vk: 'S'),
        new(RecEventKind.KeyUp, 580, Vk: KeyNames.VK_LCONTROL),
        new(RecEventKind.KeyDown, 600, Vk: KeyNames.VK_LMENU),  // lone Alt
        new(RecEventKind.KeyUp, 640, Vk: KeyNames.VK_LMENU),
        new(RecEventKind.MouseMove, 700, 400, 400),   // trip to the Stop button: trimmed
        new(RecEventKind.MouseMove, 720, 500, 500),
    };
    var a = RecorderLogic.BuildActions(ev, new RecordingOptions());
    var text = string.Join(" | ", a.Select(x => x.ToString()));
    Eq("Move 10, 10 | Move 30, 10 | Click 30, 10 | Wait 480 ms | Keypress Ctrl+S | Wait 120 ms | Keypress Alt", text, "actions");
    var noMoves = RecorderLogic.BuildActions(ev, new RecordingOptions { RecordMouseMoves = false });
    Check(noMoves.All(x => x is not MoveAction), "moves off");
});

Test("Rules: onMatch fires on the edge, after hold, respecting cooldown", () =>
{
    var rule = new PixelRule { Color = blue.ToHex(), Tolerance = 12, HoldForMs = 100, CooldownMs = 1000 };
    var s = new RuleState();
    Check(!s.Update(rule, grey, 0), "no match");
    Check(!s.Update(rule, blue, 50), "match starts, hold not met");
    Check(!s.Update(rule, blue, 100), "99 < hold");
    Check(s.Update(rule, blue, 150), "hold met → fire");
    Check(!s.Update(rule, blue, 200), "still matching: no refire");
    Check(!s.Update(rule, grey, 250), "leaves");
    Check(!s.Update(rule, blue, 300), "re-enters");
    Check(!s.Update(rule, blue, 450), "hold met but in cooldown");
    Check(s.Update(rule, blue, 1150), "cooldown over while still matching → fire once");
    Check(!s.Update(rule, blue, 1200), "no refire");
    Check(!s.Update(rule, blue, 1210) && s.FireCount == 2, "fire count");
    Check(!s.Update(rule, null, 1300), "unreadable = no match");
    Check(!s.Update(rule, blue, 1400), "re-enter after unreadable, hold");
});

Test("Rules: hold resets when the match flickers", () =>
{
    var rule = new PixelRule { Color = blue.ToHex(), HoldForMs = 100, CooldownMs = 0 };
    var s = new RuleState();
    s.Update(rule, blue, 0);
    s.Update(rule, grey, 60);
    Check(!s.Update(rule, blue, 110), "hold restarted");
    Check(s.Update(rule, blue, 210), "fires after a full hold");
});

Test("Rules: onMismatch and onChange", () =>
{
    var mm = new PixelRule { Color = blue.ToHex(), Mode = RuleMode.OnMismatch, HoldForMs = 0, CooldownMs = 0 };
    var s = new RuleState();
    Check(!s.Update(mm, blue, 0), "matching");
    Check(s.Update(mm, grey, 10), "stops matching → fire");
    Check(!s.Update(mm, null, 20), "locked desktop never fires onMismatch");
    Check(!s.Update(mm, blue, 30), "back");

    var ch = new PixelRule { Mode = RuleMode.OnChange, Tolerance = 5, HoldForMs = 50, CooldownMs = 0 };
    var c = new RuleState();
    Check(!c.Update(ch, grey, 0), "baseline");
    Check(!c.Update(ch, new Rgb(0x83, 0x80, 0x80), 10), "within tolerance");
    Check(!c.Update(ch, blue, 20), "new color, hold");
    Check(c.Update(ch, blue, 70), "held → fire");
    Check(!c.Update(ch, blue, 120), "blue is the new reference");
    Check(!c.Update(ch, grey, 130), "changes back, hold");
    Check(c.Update(ch, grey, 180), "fires again");
});

Test("rules.json example parses", () =>
{
    var rules = RulesFile.Parse("""
    {
        "rules": [
          {
            "name": "Dialog OK appears",
            "x": 960, "y": 640,
            "color": "#2D7DD2",
            "tolerance": 12,
            "mode": "onMatch",
            "intervalMs": 50,
            "holdForMs": 100,
            "cooldownMs": 1000,
            "enabled": true,
            "actions": [
              { "type": "click", "x": 960, "y": 640 },
              { "type": "wait", "ms": 250 },
              { "type": "key", "key": "Enter" }
            ]
          }
        ]
    }
    """);
    Eq(1, rules.Count, "count");
    var r = rules[0];
    Eq("Dialog OK appears", r.Name, "name");
    Eq(RuleMode.OnMatch, r.Mode, "mode");
    Eq(3, r.Actions.Count, "actions");
    Check(r.Actions[0] is ClickAction { Button: MouseButton.Left }, "click defaults to left");
    Eq(0, r.Validate().Count, "valid");
    var back = RulesFile.Parse(RulesFile.Serialize(rules));
    Eq(RulesFile.Serialize(rules), RulesFile.Serialize(back), "round trip");
    Check(RulesFile.Serialize(rules).Contains("\"mode\": \"onMatch\""), "mode written camelCase");
    Check(!RulesFile.Serialize(rules).Contains("pixelText") && !RulesFile.Serialize(rules).Contains("displayName"), "display-only properties are not saved");
});

Test("Colors, hotkeys and key names", () =>
{
    Check(Rgb.TryParse("2d7dd2", out var c) && c == blue, "hex without #");
    Check(Rgb.TryParse("#FFF", out var w) && w == new Rgb(255, 255, 255), "short hex");
    Check(!Rgb.TryParse("#12345", out _), "bad length");
    Eq(new Rgb(0x11, 0x22, 0x33), Rgb.FromColorRef(0x00332211), "COLORREF red is low byte");
    Check(Hotkey.TryParse("Ctrl+Alt+Escape", out var h) && h.Modifiers == (Modifiers.Ctrl | Modifiers.Alt) && h.Vk == 0x1B, "hotkey");
    Eq("Ctrl+Alt+Escape", h.ToString(), "hotkey text");
    Check(!Hotkey.TryParse("Ctrl+Alt", out _), "modifier-only rejected");
    Check(Hotkey.TryParse("", out var none) && none.IsEmpty, "empty = none");
    Check(KeyNames.TryGetVk("pgdn", out var pg) && pg == 0x22 && KeyNames.IsExtended(pg), "alias + extended");
    Eq("D5", KeyNames.GetName(0x35), "digit name");
});

Test("Recorder: swallowed hotkey leaves no lone modifiers", () =>
{
    var ev = new List<RecEvent>
    {
        new(RecEventKind.KeyDown, 0, Vk: KeyNames.VK_LCONTROL),
        new(RecEventKind.KeyDown, 10, Vk: KeyNames.VK_LMENU),
        new(RecEventKind.Suppressed, 20, Vk: 0x1B),
        new(RecEventKind.KeyUp, 30, Vk: KeyNames.VK_LMENU),
        new(RecEventKind.KeyUp, 40, Vk: KeyNames.VK_LCONTROL),
    };
    Eq(0, RecorderLogic.BuildActions(ev, new RecordingOptions()).Count, "nothing recorded");
});

Test("RuleEngine: fires once per appearance and runs actions off the polling thread", () =>
{
    var screen = new FakeScreen();
    var input = new FakeInput();
    using var engine = new RuleEngine(screen, input);
    engine.SetRules([new PixelRule
    {
        Name = "t", X = 3, Y = 3, Color = blue.ToHex(), IntervalMs = 10, HoldForMs = 20, CooldownMs = 0,
        Actions = [new WaitAction { Ms = 50 }, new KeyAction { Key = "Enter" }],
    }]);
    engine.Start();
    Thread.Sleep(150);
    Eq(0, input.Keys.Count, "nothing while not matching");
    screen.Set(3, 3, blue);
    Thread.Sleep(300);
    Eq(1, input.Keys.Count, "fired once");
    screen.Set(3, 3, grey);
    Thread.Sleep(100);
    screen.Set(3, 3, blue);
    Thread.Sleep(300);
    Eq(2, input.Keys.Count, "fired again after re-appearing");
    Eq(2, engine.GetState(0)!.FireCount, "fire count");
    engine.SetRules([new PixelRule { Name = "slow", Color = "#000000", IntervalMs = 10, HoldForMs = 0,
        Actions = [new WaitAction { Ms = 10_000 }, new KeyAction { Key = "A" }] }]);
    engine.Stop(); engine.Start();
    Thread.Sleep(150);
    Eq(1, engine.RunningCount, "slow action running");
    engine.CancelRunningActions();
    Thread.Sleep(100);
    Eq(0, engine.RunningCount, "emergency stop halted it");
    Check(!input.Keys.Any(k => k.Vk == 'A'), "cancelled before its key");
    engine.Stop();
});

Test("Example files in examples/ load", () =>
{
    var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples"));
    var macro = MacroFile.Load(Path.Combine(dir, "dialog-loop.estep"));
    Eq(7, macro.Count, "macro rows");
    Eq<string?>(null, MacroValidator.FirstProblem(macro), "macro valid");
    var rules = RulesFile.Load(Path.Combine(dir, "rules.json"));
    Eq(1, rules.Count, "rules");
    Eq(0, rules[0].Validate().Count, "rule valid");
});

Test("Player: reports the pixel being watched (for the crosshair)", () =>
{
    var screen = new FakeScreen();
    screen.Windows["Notepad"] = (100, 200);
    var p = new Player([new IfPixelAction { X = 10, Y = 20, Window = "Notepad", Color = "#FFFFFF" }], new FakeInput(), screen, new FakeClock());
    Check(p.Tracked is null, "nothing tracked before playing");
    p.Run(default);
    Eq(new TrackedPixel(110, 220), p.Tracked, "tracked pixel is in screen coordinates");
    Thread.Sleep(450);
    Check(p.Tracked is null, "crosshair expires shortly after the last read");
});

Test("Wait until time of day", () =>
{
    var start = new DateTime(2026, 11, 13, 22, 0, 0);
    var clock = new FakeClock();
    DateTime Now() => start.AddMilliseconds(clock.NowMs);
    var input = new FakeInput();
    var a = new List<MacroAction> { new WaitUntilAction { Time = "22:30" }, new KeyAction { Key = "A" } };
    var r = new Player(a, input, new FakeScreen(), clock, now: Now).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Check(Now() >= new DateTime(2026, 11, 13, 22, 30, 0) && Now() < new DateTime(2026, 11, 13, 22, 30, 2), $"woke at 22:30 (was {Now():T})");
    Eq(1, input.Keys.Count, "continued");

    clock = new FakeClock();
    var log = new List<string>();
    new Player([new WaitUntilAction { Time = "9:00:30" }], input, new FakeScreen(), clock, log.Add, Now).Run(default);
    Check(Now() >= new DateTime(2026, 11, 14, 9, 0, 30) && Now() < new DateTime(2026, 11, 14, 9, 0, 32), $"passed time waits until tomorrow (was {Now():g})");
    Check(log.Any(l => l.Contains("09:00:30 tomorrow")), "logged");

    Eq(new DateTime(2026, 11, 14, 22, 0, 0), WaitUntilAction.NextOccurrence(start, new TimeOnly(22, 0)), "exactly now → tomorrow");
    var json = MacroFile.Serialize(a);
    Check(json.Contains("{\"type\":\"waitUntil\",\"time\":\"22:30\"}") && json.Contains("\"version\": 2"), "file format: " + json);
    Check(MacroFile.Parse(json)[0] is WaitUntilAction { Time: "22:30" }, "round trip");
    Eq("Wait until time 22:30", a[0].ToString(), "display");
    ((WaitUntilAction)a[0]).Time = "25:00";
    Check(MacroValidator.FirstProblem(a)?.Contains("not a time") == true, "bad time flagged");
    using var cts = new CancellationTokenSource(200);
    var c = new Player([new WaitUntilAction { Time = DateTime.Now.AddHours(2).ToString("HH:mm") }], input, new FakeScreen()).Run(cts.Token);
    Eq(PlayOutcome.Cancelled, c.Outcome, "Stop interrupts the wait");
});

Test("Copy and paste actions", () =>
{
    var a = MacroFile.Parse(SpecExample);
    var text = MacroClipboard.ToText([a[5], a[6], a[1]]);   // "retry" row, a goto, and a wait that times out to retry
    var pasted = MacroClipboard.FromText(text)!;
    Eq(3, pasted.Count, "three actions on the clipboard");
    Check(!ReferenceEquals(pasted[0], a[5]), "pasted actions are copies");
    MacroClipboard.PrepareForPaste(a, pasted);
    Eq("retry2", pasted[0].Label, "duplicate label renamed");
    Eq("retry2", ((WaitPixelAction)pasted[2]).OnTimeout, "jump inside the pasted block follows the rename");
    Eq("start", ((GotoAction)pasted[1]).Target, "other targets untouched");
    a.InsertRange(7, pasted);
    Eq<string?>(null, MacroValidator.FirstProblem(a), "macro still valid after paste");
    var again = MacroClipboard.FromText(text)!;
    MacroClipboard.PrepareForPaste(a, again);
    Eq("retry3", again[0].Label, "pasting twice keeps labels unique");
    var plain = MacroClipboard.FromText(MacroClipboard.ToText([a[0]]))!;
    MacroClipboard.PrepareForPaste(a, plain);
    Check(plain[0] is ClickAction { Label: null, X: 400 }, "unlabelled action pastes unchanged");
    Check(MacroClipboard.FromText("hello") is null && MacroClipboard.FromText("") is null && MacroClipboard.FromText("{\"x\":1}") is null, "other clipboard text is ignored");
});

Test("Undo and redo", () =>
{
    string Show(List<MacroAction> l) => string.Join(",", l.Select(x => ((KeyAction)x).Key));
    var list = new List<MacroAction> { new KeyAction { Key = "A" } };
    var h = new UndoHistory(list);
    Check(!h.Undo(list), "nothing to undo at first");
    list.Add(new KeyAction { Key = "B" }); h.Record(list);
    ((KeyAction)list[0]).Key = "X"; h.Record(list);                 // an edit in place
    list.RemoveAt(1); h.Record(list);
    Eq("X", Show(list), "after three changes");
    Check(h.Undo(list), "undo delete"); Eq("X,B", Show(list), "delete undone");
    Check(h.Undo(list), "undo edit"); Eq("A,B", Show(list), "edit undone");
    Check(h.Redo(list), "redo edit"); Eq("X,B", Show(list), "edit redone");
    h.Undo(list); h.Undo(list); Eq("A", Show(list), "back to the start");
    Check(!h.Undo(list) && h.CanRedo, "start reached");
    list.Add(new KeyAction { Key = "C" }); h.Record(list);
    Check(!h.CanRedo, "a new change clears redo");
    h.Undo(list); Eq("A", Show(list), "new change undone");
});

Test("Wait units", () =>
{
    Eq<int?>(1500, WaitAction.ToMs("1.5", WaitUnit.Seconds), "1.5 s");
    Eq<int?>(90_000, WaitAction.ToMs("1,5", WaitUnit.Minutes), "comma decimal");
    Eq<int?>(7_200_000, WaitAction.ToMs("2", WaitUnit.Hours), "2 h");
    Eq<int?>(250, WaitAction.ToMs("250", WaitUnit.Milliseconds), "ms");
    Check(WaitAction.ToMs("-1", WaitUnit.Seconds) is null && WaitAction.ToMs("abc", WaitUnit.Seconds) is null
          && WaitAction.ToMs("1000", WaitUnit.Hours) is null, "bad amounts rejected");
    var w = new WaitAction { Ms = 90_000, Unit = WaitUnit.Minutes };
    Eq("Wait 1.5 min", w.ToString(), "display");
    Eq("Wait 500 ms", new WaitAction().ToString(), "default display unchanged");
    var json = MacroFile.Serialize([w, new WaitAction { Ms = 250 }]);
    Check(json.Contains("{\"type\":\"wait\",\"ms\":90000,\"unit\":\"min\"}") && json.Contains("{\"type\":\"wait\",\"ms\":250}"), "file format: " + json);
    Check(json.Contains("\"version\": 1"), "still a version 1 file");
    Check(MacroFile.Parse(json)[0] is WaitAction { Ms: 90_000, Unit: WaitUnit.Minutes }, "round trip");
    var clock = new FakeClock();
    new Player([w], new FakeInput(), new FakeScreen(), clock).Run(default);
    Eq(90_000L, clock.NowMs, "plays for 90 seconds");
});

Test("Next label, success branches and Repeat modes", () =>
{
    var input = new FakeInput();
    string Keys() => string.Join(",", input.Keys.Select(k => KeyNames.GetName(k.Vk)));
    var a = new List<MacroAction>
    {
        new GotoAction { Target = "next" },
        new WaitPixelAction { X = 1, Y = 1, Color = "#000000", Then = "found", TimeoutMs = 100 },
        new KeyAction { Key = "A" },
        new KeyAction { Key = "B", Label = "found" },
    };
    Eq(PlayOutcome.Finished, new Player(a, input, new FakeScreen(), new FakeClock()).Run(default).Outcome, "finished");
    Eq("B", Keys(), "goto next continues; pixel match jumps to Then");
    Check(Labels.IsReserved("Next") && Labels.CheckCustomLabel("next", a, -1) is not null, "next is reserved");
    Check(MacroFile.Serialize(a).Contains("\"version\": 2"), "next target needs version 2");

    input.Keys.Clear();
    var clock = new FakeClock();
    var secs = new List<MacroAction>
    {
        new KeyAction { Key = "A" }, new WaitAction { Ms = 1000 },
        new RepeatAction { Target = "start", Mode = RepeatMode.Seconds, Seconds = 3, After = "done" },
        new KeyAction { Key = "X" },
        new KeyAction { Key = "B", Label = "done" },
    };
    new Player(secs, input, new FakeScreen(), clock).Run(default);
    Eq("A,A,A,A,B", Keys(), "repeat for 3 seconds, then jump to After");

    input.Keys.Clear();
    clock = new FakeClock();
    var start = new DateTime(2026, 11, 14, 10, 0, 0);
    var until = new List<MacroAction>
    {
        new KeyAction { Key = "A" }, new WaitAction { Ms = 60_000 },
        new RepeatAction { Target = "start", Mode = RepeatMode.Until, Until = "10:03" },
    };
    new Player(until, input, new FakeScreen(), clock, now: () => start.AddMilliseconds(clock.NowMs)).Run(default);
    Eq("A,A,A", Keys(), "repeat until 10:03");
    Eq("→ start, until 10:03", until[2].Parameters, "display");

    using var cts = new CancellationTokenSource(150);
    var forever = new Player([new RepeatAction { Target = "start", Mode = RepeatMode.Infinite }], input, new FakeScreen()).Run(cts.Token);
    Eq(PlayOutcome.Cancelled, forever.Outcome, "infinite repeat runs until Stop");
});

Test("Click kinds, wheel, key options, move paths", () =>
{
    var input = new FakeInput { Cursor = (100, 100) };
    var a = new List<MacroAction>
    {
        new ClickAction { X = 10, Y = 5, Relative = true },
        new ClickAction { X = 200, Y = 200, Kind = ClickKind.Double, Button = MouseButton.Right },
        new ClickAction { X = 300, Y = 300, Kind = ClickKind.Down, Button = MouseButton.X1 },
        new ClickAction { X = 320, Y = 300, Kind = ClickKind.Up, Button = MouseButton.X1 },
        new WheelAction { Value = -240 },
        new WheelAction { Value = 120, Horizontal = true },
        new KeyAction { Key = "Shift", Option = KeyOption.Down },
        new KeyAction { Key = "A", Count = 3 },
        new KeyAction { Key = "Shift", Option = KeyOption.Up },
    };
    Eq(PlayOutcome.Finished, new Player(a, input, new FakeScreen(), new FakeClock()).Run(default).Outcome, "finished");
    Eq("click Left 110,105|click Right 200,200|click Right 200,200|X1 down 300,300|X1 up 320,300|wheel -240|wheel 120 h|key Shift down|press A|press A|press A|key Shift up",
        string.Join("|", input.Events), "events");
    var json = MacroFile.Serialize(a);
    Check(json.Contains("{\"type\":\"key\",\"key\":\"A\",\"count\":3}") && json.Contains("\"action\":\"double\"") && json.Contains("{\"type\":\"wheel\",\"value\":-240}"), "file format: " + json);
    Eq(json, MacroFile.Serialize(MacroFile.Parse(json)), "round trip");
    var wiggle = new FakeInput();
    new Player([new ClickAction { X = 50, Y = 50, Wiggle = true }], wiggle, new FakeScreen(), new FakeClock()).Run(default);
    Check(Math.Abs(wiggle.Clicks[0].X - 50) <= 2 && Math.Abs(wiggle.Clicks[0].Y - 50) <= 2, "wiggle stays within 2 px");

    foreach (var mode in Enum.GetValues<MousePathMode>())
    {
        var m = new FakeInput { Cursor = (0, 0) };
        var clock = new FakeClock();
        new Player([new MoveAction { X = 400, Y = 300, TravelMs = 250 }], m, new FakeScreen(), clock, options: new PlayOptions { MousePath = mode }).Run(default);
        Eq((400, 300), m.Cursor, $"{mode}: ends on the target");
        Check(m.Moves.Count >= 20 && clock.NowMs is >= 200 and <= 300, $"{mode}: glides over about 250 ms ({m.Moves.Count} points, {clock.NowMs} ms)");
    }
    var from = new FakeInput();
    new Player([new MoveAction { FromX = 10, FromY = 10, X = 20, Y = 20 }], from, new FakeScreen(), new FakeClock()).Run(default);
    Eq("(10, 10),(20, 20)", string.Join(",", from.Moves), "move with a start point");
    var smooth = new FakeInput();
    new Player([new MoveAction { X = 200, Y = 0 }], smooth, new FakeScreen(), new FakeClock(), options: new PlayOptions { MousePath = MousePathMode.Linear }).Run(default);
    Check(smooth.Moves.Count > 3, "smoothing modes glide between recorded points");
});

Test("Disabled rows, filter, ranges, repeat count, breakpoints and pause", () =>
{
    string Run(List<MacroAction> list, PlayOptions o, out Player p)
    {
        var input = new FakeInput();
        p = new Player(list, input, new FakeScreen(), new FakeClock(), options: o);
        p.Run(default);
        return string.Join(",", input.Events.Select(e => e.Replace("press ", "").Replace("click Left ", "c")));
    }
    var a = new List<MacroAction>
    {
        new KeyAction { Key = "A" }, new KeyAction { Key = "B", Disabled = true }, new ClickAction { X = 1, Y = 1 },
        new WaitAction { Ms = 500 }, new KeyAction { Key = "C" }, new KeyAction { Key = "D" },
    };
    Eq("A,c1,1,C,D", Run(a, new PlayOptions(), out _), "disabled row skipped");
    Eq("c1,1", Run(a, new PlayOptions { Filter = PlaybackFilter.All & ~PlaybackFilter.KeyPresses }, out _), "key presses filtered out");
    Eq("c1,1,C", Run(a, new PlayOptions { FirstRow = 2, LastRow = 4 }, out _), "play selected range");
    Eq("C", Run(a, new PlayOptions { FirstRow = 4, LastRow = 4 }, out _), "single row");
    Eq("A,c1,1", Run(a, new PlayOptions { LastRow = 3 }, out _), "play until");
    Eq("C,D,C,D,C,D", Run(a, new PlayOptions { FirstRow = 4, RepeatCount = 3 }, out _), "repeat count");
    var clock = new FakeClock();
    new Player(a, new FakeInput(), new FakeScreen(), clock, options: new PlayOptions { Filter = PlaybackFilter.All & ~PlaybackFilter.WaitTimes }).Run(default);
    Eq(0L, clock.NowMs, "wait times filtered out");

    a[4].Breakpoint = true;
    Eq("A,c1,1,C,D", Run(a, new PlayOptions { IgnoreBreakpoints = true }, out _), "play ignoring breakpoints");
    var input2 = new FakeInput();
    var player = new Player(a, input2, new FakeScreen(), new FakeClock());
    var task = Task.Run(() => player.Run(default));
    SpinWait.SpinUntil(() => player.IsPaused, 2000);
    Check(player.IsPaused && player.CurrentRow == 4, "paused before the breakpoint row");
    Eq(1, input2.Keys.Count, "row with the breakpoint has not run yet");
    player.Step();
    SpinWait.SpinUntil(() => player.IsPaused && player.CurrentRow == 5, 2000);
    Check(player.IsPaused && input2.Keys.Count == 2, "step runs one row and pauses again");
    player.Resume();
    Check(task.Wait(2000) && task.Result.Outcome == PlayOutcome.Finished && input2.Keys.Count == 3, "resume finishes");
    Check(MacroFile.Serialize(a).Contains("\"disabled\":true") && MacroFile.Serialize(a).Contains("\"breakpoint\":true"), "flags are saved");
});

Test("Wait for screen changes and Window focus", () =>
{
    var screen = new FakeScreen();
    var input = new FakeInput();
    var clock = new FakeClock();
    var pixels = new int[100 * 50];
    screen.Frame = () => new ScreenImage(500, 400, 100, 50, (int[])pixels.Clone());
    clock.At(250, () => { pixels[10 * 100 + 20] = 0xFFFFFF; pixels[30 * 100 + 60] = 0x808080; });
    var ws = new WaitScreenChangeAction { X = 500, Y = 400, Width = 100, Height = 50, Mouse = ChangeMouseAction.LeftClick, Anchor = ChangeAnchor.Center, Then = "end" };
    var r = new Player([ws, new KeyAction { Key = "A" }], input, screen, clock).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Eq("click Left 540,420", string.Join("|", input.Events), "clicked the centre of the changed area, then jumped to end");
    Eq((520, 410, 41, 21), ScreenDiff.ChangedBounds(new ScreenImage(500, 400, 100, 50, new int[5000]), screen.Frame()!, 8)!.Value, "bounding box");
    var noise = new int[5000]; noise[7] = 0x050505;
    Check(ScreenDiff.ChangedBounds(new ScreenImage(0, 0, 100, 50, new int[5000]), new ScreenImage(0, 0, 100, 50, noise), 8) is null, "small differences are ignored");

    var still = new FakeScreen { Frame = () => new ScreenImage(0, 0, 2, 2, new int[4]) };
    var input2 = new FakeInput();
    new Player([new WaitScreenChangeAction { TimeoutMs = 1000, OnTimeout = "t" }, new KeyAction { Key = "A" }, new KeyAction { Key = "B", Label = "t" }], input2, still, new FakeClock()).Run(default);
    Eq("press B", string.Join("|", input2.Events), "no change → timeout branch");

    var fs = new FakeScreen();
    var c2 = new FakeClock();
    WindowFocusAction? seen = null;
    c2.At(300, () => fs.Focus = a => { seen = a; return true; });
    var wf = new WindowFocusAction { Program = "notepad.exe", Title = "Untitled", TitleMatch = TitleMatch.Begins, Left = 0, Top = 0, Width = 800, Height = 600 };
    var input3 = new FakeInput();
    Eq(PlayOutcome.Finished, new Player([wf, new KeyAction { Key = "A" }], input3, fs, c2).Run(default).Outcome, "window appeared");
    Check(seen is { Width: 800 } && c2.NowMs >= 300 && input3.Keys.Count == 1, "waited for the window, then continued");
    Check(wf.TitleMatches("Untitled - Notepad") && !wf.TitleMatches("My Untitled") && wf.ProgramMatches("NOTEPAD") && !wf.ProgramMatches("calc.exe"), "matching");
    var gone = new Player([new WindowFocusAction { Title = "Nope", TimeoutMs = 500, OnTimeout = "stop" }], input3, new FakeScreen(), new FakeClock()).Run(default);
    Eq(PlayOutcome.StoppedByMacro, gone.Outcome, "missing window → timeout");
    var json = MacroFile.Serialize([wf, ws]);
    Eq(json, MacroFile.Serialize(MacroFile.Parse(json)), "round trip");
});

Test("Recorder: wheel, focus changes, Down/Up mode, sensitivity", () =>
{
    var ev = new List<RecEvent>
    {
        new(RecEventKind.FocusChange, 0, Program: "notepad.exe", Title: "Untitled - Notepad"),
        new(RecEventKind.MouseWheel, 100, Delta: -120),
        new(RecEventKind.MouseWheel, 120, Delta: -120),
        new(RecEventKind.MouseWheel, 400, Delta: 120, Horizontal: true),
        new(RecEventKind.MouseDown, 600, 10, 10, MouseButton.Left),
        new(RecEventKind.MouseUp, 900, 60, 10, MouseButton.Left),
        new(RecEventKind.KeyDown, 1000, Vk: 'A'),
        new(RecEventKind.KeyDown, 1030, Vk: 'A'),
        new(RecEventKind.KeyUp, 1060, Vk: 'A'),
    };
    var a = RecorderLogic.BuildActions(ev, new RecordingOptions());
    Eq("Window focus|Wait|Wheel -240 vertical|Wait|Wheel 120 horizontal|Wait|Click 10, 10|Wait|Keypress A|Keypress A",
        string.Join("|", a.Select(x => x is WindowFocusAction or WaitAction ? x.DisplayName : x.ToString())), "combined mode");
    var du = RecorderLogic.BuildActions(ev, new RecordingOptions { SeparateDownUp = true, RecordFocusChanges = false });
    Eq("Wheel|Wheel|Mouse button down 10, 10|Mouse button up 60, 10|Key down A|Key up A",
        string.Join("|", du.Where(x => x is not WaitAction).Select(x => x is WheelAction ? "Wheel" : x.ToString())), "Down/Up mode keeps the drag and ignores auto-repeat");
    var moves = Enumerable.Range(0, 50).Select(i => new RecEvent(RecEventKind.MouseMove, i * 10, i * 20, 0)).Append(new RecEvent(RecEventKind.MouseDown, 600, 1000, 0)).ToList();
    var all = RecorderLogic.BuildActions(moves, new RecordingOptions()).Count(x => x is MoveAction);
    var few = RecorderLogic.BuildActions(moves, new RecordingOptions { MinMoveIntervalMs = 100 }).Count(x => x is MoveAction);
    Check(all == 50 && few is >= 4 and <= 6, $"lower sensitivity keeps fewer points ({all} → {few})");
});

Test("Editing: edit all, search & replace, combine, groups, comments", () =>
{
    var before = new WaitPixelAction { X = 1, Y = 2, Color = "#111111", TimeoutMs = 5000 };
    var after = (WaitPixelAction)before.Clone();
    after.TimeoutMs = 9000; after.Then = "ok"; after.Label = "mine";
    var other = new WaitPixelAction { X = 50, Y = 60, Color = "#222222", TimeoutMs = 100, Label = "keep", Comment = "c" };
    var applied = (WaitPixelAction)MacroEditing.ApplyChanges(before, after, other);
    Check(applied is { X: 50, Color: "#222222", TimeoutMs: 9000, Then: "ok", Label: "keep", Comment: "c" }, "only the changed fields are applied; label kept");
    Check(ReferenceEquals(MacroEditing.ApplyChanges(before, after, new GotoAction()), null) == false, "other types untouched");

    var click = new ClickAction { X = 640, Y = 312, Window = "Notepad", Comment = "open Notepad" };
    Check(MacroEditing.Matches(click, FindField.All, "notepad") && MacroEditing.Matches(click, FindField.X, "64") && !MacroEditing.Matches(click, FindField.Y, "64"), "find");
    var replaced = (ClickAction)MacroEditing.Replace(click, FindField.WindowTitle, "Notepad", "WordPad", out var n)!;
    Check(n == 1 && replaced.Window == "WordPad" && replaced.Comment == "open Notepad", "replace in one parameter only");
    Check(((ClickAction)MacroEditing.Replace(click, FindField.X, "640", "700", out _)!).X == 700, "replace a number");
    Check(MacroEditing.Replace(click, FindField.X, "640", "abc", out _) is null, "a number cannot become text");
    var move = new MoveAction { FromX = 5, FromY = 6, X = 70, Y = 80, TravelMs = 250 };
    Check(MacroEditing.Matches(move, FindField.EndX, "70") && MacroEditing.Matches(move, FindField.X, "5") && MacroEditing.Matches(move, FindField.MoveDuration, "250"), "move fields");
    Check(((GotoAction)MacroEditing.Replace(new GotoAction { Target = "retry" }, FindField.TargetLabel, "retry", "again", out _)!).Target == "again", "target label");

    var list = new List<MacroAction>
    {
        new ClickAction { X = 1, Y = 1, Kind = ClickKind.Down }, new WaitAction { Ms = 80 }, new ClickAction { X = 1, Y = 1, Kind = ClickKind.Up },
        new KeyAction { Key = "A", Option = KeyOption.Down }, new KeyAction { Key = "A", Option = KeyOption.Up },
        new KeyAction { Key = "B", Option = KeyOption.Down }, new KeyAction { Key = "C", Option = KeyOption.Up },
    };
    Eq(2, MacroEditing.CombineDownUp(list, Enumerable.Range(0, list.Count)), "two pairs merged");
    Eq("Click 1, 1|Keypress A|Key down B|Key up C", string.Join("|", list.Select(x => x.ToString())), "combined list");

    var g = new List<MacroAction> { new WaitAction(), new WaitAction { Group = "g1" }, new WaitAction { Group = "g1" }, new WaitAction { Ms = 1500, Comment = "note", RowColor = "#263F66" } };
    Eq((1, 2), MacroEditing.GroupRange(g, 2)!.Value, "group range");
    Check(MacroEditing.GroupRange(g, 0) is null && MacroEditing.NewGroupId(g) == "g2", "group ids");
    Eq(TimeSpan.FromSeconds(3), MacroEditing.Duration(g), "macro duration");
    var json = MacroFile.Serialize(g, "1920x1080");
    var back = MacroFile.Parse(json, out var screen);
    Check(screen == "1920x1080" && back[3] is { Comment: "note", RowColor: "#263F66" } && back[1].Group == "g1", "comment, color, group and screen layout are saved");
    Check(MacroFile.Parse(SpecExample, out var none).Count == 7 && none is null, "old files have no screen layout");
});

Test("OpenMR files still open after the Echostep rename", () =>
{
    var old = MacroFile.Parse("""{"format":"OpenMR","version":2,"actions":[{"type":"wait","ms":250}]}""");
    Check(old.Count == 1, "OpenMR format accepted");
});

Test("Files saved before the rename (format OpenRecorder) still open", () =>
{
    var old = MacroFile.Parse("""{"format":"OpenRecorder","version":1,"actions":[{"type":"wait","ms":250}]}""");
    Check(old is [WaitAction { Ms: 250 }], "old format name accepted");
    Check(MacroFile.Serialize(old).Contains("\"format\": \"Echostep\""), "saved with the new name");
    Check(MacroClipboard.FromText("""{"format":"OpenRecorder","version":1,"actions":[{"type":"wait","ms":1}]}""") is { Count: 1 }, "old clipboard text pastes");
});

Test("Time amounts in units", () =>
{
    Eq(WaitUnit.Seconds, Durations.BestUnit(5000), "5000 ms → seconds");
    Eq(WaitUnit.Milliseconds, Durations.BestUnit(1500), "1500 ms stays ms");
    Eq(WaitUnit.Minutes, Durations.BestUnit(120_000), "2 min");
    Eq(WaitUnit.Hours, Durations.BestUnit(7_200_000), "2 h");
    Eq(WaitUnit.Milliseconds, Durations.BestUnit(0), "0 → ms");
    Eq("30 s", Durations.Format(30_000), "format best unit");
    Eq("1.5 min", Durations.Format(90_000, WaitUnit.Minutes), "format in a unit");
    Eq<long?>(90_000, Durations.ToMs("1,5", WaitUnit.Minutes), "comma decimal");
    Check(Durations.ToMs("x", WaitUnit.Seconds) is null && Durations.ToMs("-2", WaitUnit.Seconds) is null, "bad input");
    Eq("2, 3 in 250 ms", new MoveAction { X = 2, Y = 3, TravelMs = 250 }.Parameters, "move duration text");
    Eq("→ start, for 2 min", new RepeatAction { Mode = RepeatMode.Seconds, Seconds = 120 }.Parameters, "repeat seconds text");
});

Test("Value column counts down while playing", () =>
{
    var clock = new FakeClock();
    var seen = new Dictionary<string, string?>();
    Player? p = null;
    void At(long ms, string name, int row) => clock.At(ms, () => seen[name] = p!.LiveValues().GetValueOrDefault(row));
    var a = new List<MacroAction>
    {
        new WaitAction { Ms = 15_000, Unit = WaitUnit.Seconds, Label = "loop" },
        new RepeatAction { Target = "loop", Count = 3 },
        new WaitPixelAction { X = 1, Y = 2, Color = "#FFFFFF", TimeoutMs = 5000, OnTimeout = "next" },
        new WaitUntilAction { Time = "23:00" },
    };
    At(5_000, "wait", 0);
    At(20_000, "repeat after 1st run", 1);
    At(35_000, "repeat after 2nd run", 1);
    At(16_000, "wait restarts", 0);
    At(47_000, "pixel timeout", 2);
    At(51_000, "pixel done", 2);
    var start = new DateTime(2026, 11, 15, 22, 59, 0);
    p = new Player(a, new FakeInput(), new FakeScreen(), clock, now: () => start.AddMilliseconds(clock.NowMs));
    At(52_000, "time of day", 3);
    var r = p.Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "finished");
    Eq<string?>("10 s", seen["wait"], "Wait 15 s counts down");
    Eq<string?>("14 s", seen["wait restarts"], "the next run starts from the full 15 s again");
    Eq<string?>("→ loop, 2 times", seen["repeat after 1st run"], "Repeat: runs left after the 1st run");
    Eq<string?>("→ loop, 1 time", seen["repeat after 2nd run"], "Repeat: runs left after the 2nd run");
    Eq<string?>("3 s", seen["pixel timeout"], "Wait for pixel timeout counts down (shown on its color swatch)");
    Eq<string?>(null, seen["pixel done"], "a row that is done shows its normal value");
    Eq<string?>(null, seen["time of day"], "Wait until time is not counted down");
    Eq(0, p.LiveValues().Count, "everything back to normal after playing");
    Eq("1, 2", LiveText.ValueText(a[2]), "Wait for pixel shows just its coordinates");
    Eq("1, 2 in \"Notepad\"", LiveText.ValueText(new WaitPixelAction { X = 1, Y = 2, Window = "Notepad" }), "…and its window, if any");
    Eq(a[0].Parameters, LiveText.ValueText(a[0]), "other actions unchanged");
});

Test("Play from selected follows jumps back above the starting row", () =>
{
    // The macro from the bug report: after the 5th "Claim green", Repeat's "after" jumps back to Init Refresh.
    var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Samples"));
    var macro = MacroFile.Load(Path.Combine(dir, "better_macro.estep"));
    var screen = new FakeScreen();
    screen.Set(433, 945, Rgb.Parse("#EB0400"));   // live
    screen.Set(1620, 975, Rgb.Parse("#00AD69"));  // green
    var input = new FakeInput();
    using var cts = new CancellationTokenSource();
    var refreshes = 0;
    input.OnClick = (x, y) => { if (x == 140 && y == 71 && ++refreshes == 3) cts.Cancel(); };
    var clock = new FakeClock();
    foreach (var first in new[] { 1, 6 }) // from "Init Refresh", and from "Check live" (below the jump target)
    {
        refreshes = 0;
        input.Clicks.Clear();
        using var run = new CancellationTokenSource();
        input.OnClick = (x, y) => { if (x == 140 && y == 71 && ++refreshes == 3) run.Cancel(); };
        var r = new Player(macro, input, screen, clock, options: new PlayOptions { FirstRow = first }).Run(run.Token);
        Eq(PlayOutcome.Cancelled, r.Outcome, $"from row {first + 1}: still playing after the repeats (it looped back)");
        Check(input.Clicks.Count(c => c == (1624, 980)) >= 10, $"from row {first + 1}: claimed green 5 times per round");
    }
    // Play selected still runs only its row.
    var a = new List<MacroAction> { new KeyAction { Key = "A", Label = "top" }, new GotoAction { Target = "top" } };
    var keys = new FakeInput();
    var single = new Player(a, keys, new FakeScreen(), new FakeClock(), options: new PlayOptions { FirstRow = 1, LastRow = 1, StopOutsideRange = true }).Run(default);
    Eq(PlayOutcome.Finished, single.Outcome, "play selected finishes after its row");
    Eq(0, keys.Keys.Count, "the row above was not run");
});

Test("If: time of day, day of week, window", () =>
{
    var screen = new FakeScreen();
    var now = new DateTime(2026, 11, 15, 17, 30, 0); // a Sunday
    string Go(IfAction cond)
    {
        cond.Then = "yes"; cond.Else = "no";
        var list = new List<MacroAction> { cond, new GotoAction { Target = "end", Label = "yes" }, new GotoAction { Target = "end", Label = "no" } };
        var log = new List<string>();
        var p = new Player(list, new FakeInput(), screen, new FakeClock(), log.Add, () => now);
        var r = p.Run(default);
        Eq(PlayOutcome.Finished, r.Outcome, "finished: " + r.Message);
        return log.Last().EndsWith("yes.") ? "yes" : "no";
    }
    Eq("yes", Go(new IfAction { Compare = TimeCompare.After, Time = "17:00" }), "after 17:00");
    Eq("no", Go(new IfAction { Compare = TimeCompare.Before, Time = "17:00" }), "before 17:00");
    Eq("yes", Go(new IfAction { Compare = TimeCompare.After, Time = "17:30" }), "after includes the exact time");
    Eq("yes", Go(new IfAction { Compare = TimeCompare.Between, Time = "09:00", Time2 = "18:00" }), "between 09:00 and 18:00");
    Eq("no", Go(new IfAction { Compare = TimeCompare.Between, Time = "09:00", Time2 = "17:30" }), "between excludes the end time");
    Eq("yes", Go(new IfAction { Compare = TimeCompare.Between, Time = "17:00", Time2 = "06:00" }), "range past midnight (evening)");
    now = new DateTime(2026, 11, 16, 3, 0, 0); // Monday 03:00
    Eq("yes", Go(new IfAction { Compare = TimeCompare.Between, Time = "17:00", Time2 = "06:00" }), "range past midnight (early morning)");
    Eq("yes", Go(new IfAction { Condition = IfCondition.Day, Days = ["mon", "tue", "wed", "thu", "fri"] }), "Monday is a weekday");
    now = new DateTime(2026, 11, 15, 12, 0, 0); // Sunday
    Eq("no", Go(new IfAction { Condition = IfCondition.Day, Days = ["mon", "tue", "wed", "thu", "fri"] }), "Sunday is not");
    Eq("yes", Go(new IfAction { Condition = IfCondition.Day, Days = ["sat", "sun"] }), "Sunday is weekend");
    screen.Titles.Add("Twitch - Google Chrome");
    screen.Front = "Notepad";
    Eq("yes", Go(new IfAction { Condition = IfCondition.Window, Title = "chrome" }), "window open (contains)");
    Eq("no", Go(new IfAction { Condition = IfCondition.Window, Title = "chrome", State = WindowCondition.Front }), "not in front");
    Eq("yes", Go(new IfAction { Condition = IfCondition.Window, Title = "Notepad", TitleMatch = TitleMatch.Exact, State = WindowCondition.Front }), "in front");
    Eq("no", Go(new IfAction { Condition = IfCondition.Window, Title = "Discord" }), "not open");

    var json = MacroFile.Serialize([new IfAction { Compare = TimeCompare.Before, Time = "9:5", Then = "a" }]);
    Check(json.Contains("{\"type\":\"if\",\"condition\":\"time\",\"compare\":\"before\",\"time\":\"9:5\",\"then\":\"a\"}"), "file format: " + json);
    var back = (IfAction)MacroFile.Parse("""{"actions":[{"type":"if","condition":"day","days":["SUN","mon"],"else":"x"},{"type":"goto","target":"end","label":"x"}]}""")[0];
    Eq("mon,sun", string.Join(",", back.Days!), "days tidied on load");
    Eq("day is Mon, Sun → next, else → x", back.Parameters, "value text");
    Eq("time is between 22:00 and 06:00 → next, else → next", new IfAction { Compare = TimeCompare.Between, Time = "22:00", Time2 = "06:00" }.Parameters, "between text");
    Eq("day is weekdays → next, else → next", new IfAction { Condition = IfCondition.Day, Days = ["mon", "tue", "wed", "thu", "fri"] }.Parameters, "weekdays text");
    Check(MacroValidator.FirstProblem([new IfAction { Time = "25:00" }])?.Contains("not a time") == true, "bad time flagged");
    Check(MacroValidator.FirstProblem([new IfAction { Condition = IfCondition.Day, Days = [] }])?.Contains("at least one day") == true, "no days flagged");
    Check(MacroValidator.FirstProblem([new IfAction { Condition = IfCondition.Window }])?.Contains("window title") == true, "empty title flagged");
    Check(MacroValidator.FirstProblem([new IfAction { Time = "10:00", Then = "missing" }])?.Contains("does not exist") == true, "missing target flagged");
});

Test("Playback speed scales waits and moves, not timeouts", () =>
{
    var clock = new FakeClock();
    var r = new Player([new WaitAction { Ms = 1000 }, new MoveAction { X = 100, Y = 0, TravelMs = 400 }], new FakeInput(), new FakeScreen(), clock,
        options: new PlayOptions { Speed = 2 }).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "runs");
    Check(clock.NowMs is >= 690 and <= 720, $"1000 + 400 ms at 2× ≈ 700 ms, was {clock.NowMs}");
    var slow = new FakeClock();
    new Player([new WaitAction { Ms = 1000 }], new FakeInput(), new FakeScreen(), slow, options: new PlayOptions { Speed = 0.5 }).Run(default);
    Eq(2000L, slow.NowMs, "0.5× doubles");
    var t = new FakeClock();
    new Player([new WaitPixelAction { X = 1, Y = 1, Color = "#FFFFFF", TimeoutMs = 1000, OnTimeout = "end" }], new FakeInput(), new FakeScreen(), t,
        options: new PlayOptions { Speed = 4 }).Run(default);
    Check(t.NowMs >= 1000, "timeouts keep their length");
});

Test("Image pattern encodes and decodes", () =>
{
    var px = Enumerable.Range(0, 12).Select(i => i * 0x010203).ToArray();
    var p = new ImagePattern(4, 3, px);
    var text = p.Encode();
    Check(text.StartsWith("4,3,"), "size first: " + text);
    Check(ImagePattern.TryDecode(text, out var back) && back!.Pixels.SequenceEqual(px), "round trip");
    Check(!ImagePattern.TryDecode("4,3,notbase64!", out _), "bad data rejected");
    Check(!ImagePattern.TryDecode("", out _), "empty rejected");
});

static ScreenImage Screen(int w, int h, Func<int, int, int> color, int ox = 0, int oy = 0)
{
    var px = new int[w * h];
    for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) px[y * w + x] = color(x, y);
    return new ScreenImage(ox, oy, w, h, px);
}

Test("Image search finds a picture with tolerance", () =>
{
    // A 3×2 picture placed at 40,25 on a noisy screen.
    int[] pic = [0xFF0000, 0x00FF00, 0x0000FF, 0x123456, 0xABCDEF, 0x777777];
    var screen = Screen(100, 60, (x, y) =>
    {
        if (x >= 40 && x < 43 && y >= 25 && y < 27) return pic[(y - 25) * 3 + (x - 40)];
        return ((x * 7 + y * 13) % 200) * 0x010101;
    }, ox: 10, oy: 5);
    var needle = new ImagePattern(3, 2, pic);
    Eq((50, 30), ImageSearch.Find(screen, needle, 0), "exact (screen offset added)");
    var shifted = new ImagePattern(3, 2, pic.Select(c => c == 0x777777 ? 0x7A7A7A : c).ToArray());
    Eq(null, ImageSearch.Find(screen, shifted, 0), "off by 3 needs tolerance");
    Eq((50, 30), ImageSearch.Find(screen, shifted, 3), "found with tolerance 3");
    Eq(null, ImageSearch.Find(screen, new ImagePattern(1, 1, [0x010203]), 0), "absent");
});

Test("Find image clicks it, sets foundX/foundY and handles not found", () =>
{
    int[] pic = [0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000];
    var screen = new FakeScreen();
    var showAt = 0L;
    var clock = new FakeClock();
    screen.Frame = () => Screen(50, 50, (x, y) => clock.NowMs >= showAt && x is 20 or 21 && y is 30 or 31 ? 0xFF0000 : 0);
    var input = new FakeInput();
    var find = new FindImageAction { Image = new ImagePattern(2, 2, pic).Encode(), Mouse = ChangeMouseAction.LeftClick, TimeoutMs = 5000 };
    var type = new TypeTextAction { Text = "at {foundX},{foundY}" };
    showAt = 1000;
    var r = new Player([find, type], input, screen, clock).Run(default);
    Eq(PlayOutcome.Finished, r.Outcome, "found");
    Check(clock.NowMs >= 1000, "kept looking until it appeared");
    Check(input.Events.Contains("click Left 21,31"), "clicked its center: " + string.Join("; ", input.Events));
    Check(input.Events.Contains("type at 21,31"), "foundX/foundY set");

    showAt = long.MaxValue;
    var once = new FindImageAction { Image = find.Image, Once = true, OnTimeout = "missing" };
    var r2 = new Player([once, new GotoAction { Target = "stop" }, new WaitAction { Ms = 1, Label = "missing" }], new FakeInput(), screen, new FakeClock()).Run(default);
    Eq(PlayOutcome.Finished, r2.Outcome, "check once jumps to else");
    var r3 = new Player([new FindImageAction { Image = find.Image, TimeoutMs = 500 }], new FakeInput(), screen, new FakeClock()).Run(default);
    Eq(PlayOutcome.StoppedByMacro, r3.Outcome, "timeout → stop");
    Check(MacroValidator.FirstProblem([new FindImageAction()])?.Contains("capture the image") == true, "no image flagged");
});

Test("Variables: set, add, type, If and clipboard", () =>
{
    var input = new FakeInput();
    var host = new FakeHost { Clipboard = "hello" };
    var a = new List<MacroAction>
    {
        new SetVariableAction { Name = "n", Value = "0" },
        new SetVariableAction { Name = "n", Operation = VariableOperation.Add, Value = "1", Label = "loop" },
        new IfAction { Condition = IfCondition.Variable, Variable = "n", VarCompare = VarCompare.Less, Value = "3", Then = "loop" },
        new SetVariableAction { Name = "clip", Operation = VariableOperation.FromClipboard },
        new TypeTextAction { Text = "n={n} clip={clip} {unknown}" },
        new SetVariableAction { Operation = VariableOperation.ToClipboard, Value = "n was {n}" },
    };
    var p = new Player(a, input, new FakeScreen(), new FakeClock(), host: host);
    Eq(PlayOutcome.Finished, p.Run(default).Outcome, "runs");
    Check(input.Events.Contains("type n=3 clip=hello {unknown}"), "typed: " + string.Join("; ", input.Events));
    Eq("n was 3", host.Clipboard, "clipboard set");
    Check(Variables.Compare("10", VarCompare.Greater, "9"), "numbers compare as numbers");
    Check(Variables.Compare("Apple", VarCompare.Equal, "apple"), "text ignores case");
    Check(Variables.Compare("Hello world", VarCompare.Contains, "WORLD"), "contains");
    var bad = new Player([new SetVariableAction { Name = "x", Value = "abc" }, new SetVariableAction { Name = "x", Operation = VariableOperation.Add, Value = "1" }],
        new FakeInput(), new FakeScreen(), new FakeClock()).Run(default);
    Eq(PlayOutcome.Failed, bad.Outcome, "adding to text fails clearly");
    Check(MacroValidator.FirstProblem([new SetVariableAction { Name = "2x" }]) is not null, "bad name flagged");
    Check(MacroValidator.FirstProblem([new IfAction { Condition = IfCondition.Variable, Variable = "" }]) is not null, "If needs a name");
    Eq("n < \"3\" → loop, else → next", ((IfAction)a[2]).Parameters, "If variable text");
});

Test("Run, Message and Type text with a delay", () =>
{
    var host = new FakeHost { Answer = false };
    var input = new FakeInput();
    var clock = new FakeClock();
    var a = new List<MacroAction>
    {
        new SetVariableAction { Name = "site", Value = "example.com" },
        new RunAction { Target = "https://{site}", Wait = true },
        new MessageAction { Text = "Continue?", Box = MessageBoxKind.YesNo, Sound = MessageSound.Beep, Then = "yes", Else = "no" },
        new GotoAction { Target = "end", Label = "yes" },
        new TypeTextAction { Text = "ab", CharDelayMs = 100, Label = "no" },
    };
    Eq(PlayOutcome.Finished, new Player(a, input, new FakeScreen(), clock, host: host).Run(default).Outcome, "runs");
    Eq("run https://example.com wait; sound Beep; ask Continue?", string.Join("; ", host.Calls), "host calls");
    Check(input.Events.SequenceEqual(["type a", "type b"]), "one character at a time");
    Eq(100L, clock.NowMs, "delay between characters");
    var noHost = new Player([new RunAction()], new FakeInput(), new FakeScreen(), new FakeClock()).Run(default);
    Eq(PlayOutcome.Failed, noHost.Outcome, "no host → clear failure");

    var json = MacroFile.Serialize([new MessageAction { Box = MessageBoxKind.None, Sound = MessageSound.Warning }, new RunAction { Target = "x" }]);
    var back = MacroFile.Parse(json);
    Check(back[0] is MessageAction { Box: MessageBoxKind.None, Sound: MessageSound.Warning } && back[1] is RunAction, "file round trip: " + json);
    Eq("Sound", back[0].DisplayName, "sound-only name");
});

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} FAILED, {passed} passed.");
return failures == 0 ? 0 : 1;

sealed class FakeInput : IInputDriver
{
    public List<(int X, int Y)> Clicks { get; } = [];
    public List<(int Vk, Modifiers Mods)> Keys { get; } = [];
    public List<string> Events { get; } = [];
    public List<(int X, int Y)> Moves { get; } = [];
    public Action<int, int>? OnClick;
    public Action<int, Modifiers>? OnKey;
    public (int X, int Y) Cursor;
    public (int X, int Y) Position => Cursor;
    public void MoveTo(int x, int y) { Cursor = (x, y); Moves.Add((x, y)); }
    public void Click(int x, int y, MouseButton button) { Cursor = (x, y); Clicks.Add((x, y)); Events.Add($"click {button} {x},{y}"); OnClick?.Invoke(x, y); }
    public void ButtonEvent(MouseButton button, bool down) => Events.Add($"{button} {(down ? "down" : "up")} {Cursor.X},{Cursor.Y}");
    public void Wheel(int amount, bool horizontal) => Events.Add($"wheel {amount}{(horizontal ? " h" : "")}");
    public void KeyPress(int vk, Modifiers modifiers) { Keys.Add((vk, modifiers)); Events.Add("press " + KeyNames.GetName(vk)); OnKey?.Invoke(vk, modifiers); }
    public void KeyEvent(int vk, bool down) => Events.Add($"key {KeyNames.GetName(vk)} {(down ? "down" : "up")}");
    public void TypeText(string text) => Events.Add("type " + text);
}

sealed class FakeHost : IHostServices
{
    public string? Clipboard;
    public bool Answer = true;
    public List<string> Calls { get; } = [];
    public string? GetClipboard() => Clipboard;
    public void SetClipboard(string text) => Clipboard = text;
    public void Run(string target, string? arguments, bool wait, CancellationToken ct) => Calls.Add($"run {target}{(arguments is null ? "" : " " + arguments)}{(wait ? " wait" : "")}");
    public bool ShowMessage(string text, bool question) { Calls.Add((question ? "ask " : "message ") + text); return Answer; }
    public void PlaySound(MessageSound sound) => Calls.Add("sound " + sound);
}

sealed class FakeScreen : IScreenReader
{
    private readonly Dictionary<(int, int), Rgb> _pixels = new();
    public Dictionary<string, (int X, int Y)> Windows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<ScreenImage?>? Frame;
    public Func<WindowFocusAction, bool>? Focus;
    public void Set(int x, int y, Rgb c) => _pixels[(x, y)] = c;
    public Rgb? GetPixel(int x, int y) => x < 0 || y < 0 ? null : _pixels.GetValueOrDefault((x, y), Rgb.Black);
    public (int X, int Y)? FindWindowClientOrigin(string title) => Windows.TryGetValue(title, out var o) ? o : null;
    public ScreenImage? Capture(int x, int y, int width, int height) => Frame?.Invoke();
    public bool FocusWindow(WindowFocusAction action) => Focus?.Invoke(action) ?? false;
    public List<string> Titles { get; } = [];
    public string? Front;
    public IEnumerable<string> WindowTitles() => Titles;
    public string? ForegroundTitle() => Front;
}

sealed class FakeClock : IClock
{
    private readonly List<(long At, Action Do)> _events = [];
    public long NowMs { get; private set; }
    public void At(long ms, Action a) => _events.Add((ms, a));
    public void Sleep(int ms, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NowMs += Math.Max(0, ms);
        foreach (var e in _events.Where(e => e.At <= NowMs).ToList()) { e.Do(); _events.Remove(e); }
    }
}
