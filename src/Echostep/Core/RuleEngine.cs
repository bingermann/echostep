namespace Echostep.Core;

/// <summary>
/// Watches pixel rules on one background thread. Each rule is read on its own interval; when a rule
/// fires, its actions run on a worker task so a slow action never stalls polling. A rule is skipped
/// while its own actions are still running.
/// </summary>
public sealed class RuleEngine : IDisposable
{
    private sealed class Entry(PixelRule rule)
    {
        public PixelRule Rule { get; } = rule;
        public RuleState State { get; } = new();
        public long NextDue;
        public bool Valid = rule.Validate().Count == 0;
    }

    private readonly IScreenReader _screen;
    private readonly IInputDriver _input;
    private readonly IClock _clock;
    private readonly Func<string?> _rulesDirectory;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile Entry[] _entries = [];
    private volatile bool _watching;
    private volatile bool _disposed;
    private CancellationTokenSource _actionsCts = new();
    private int _runningCount;
    private readonly IHostServices? _host;

    /// <param name="rulesDirectory">Folder used to resolve relative macroFile paths.</param>
    public RuleEngine(IScreenReader screen, IInputDriver input, IClock? clock = null, Func<string?>? rulesDirectory = null,
        IHostServices? host = null)
    {
        _host = host;
        _screen = screen;
        _input = input;
        _clock = clock ?? SystemClock.Instance;
        _rulesDirectory = rulesDirectory ?? (() => null);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Echostep rule watcher", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public bool Watching => _watching;

    /// <summary>Number of rules whose actions are running now.</summary>
    public int RunningCount => Volatile.Read(ref _runningCount);

    /// <summary>Raised (on a worker thread) when watching starts or stops.</summary>
    public event Action<bool>? WatchingChanged;

    /// <summary>Replaces the rule set. The engine keeps its own copies; state starts fresh.</summary>
    public void SetRules(IEnumerable<PixelRule> rules)
    {
        _entries = rules.Select(r => new Entry(r.Clone())).ToArray();
        _wake.Set();
    }

    /// <summary>Live state of rule <paramref name="index"/> (as passed to <see cref="SetRules"/>), for display.</summary>
    public RuleState? GetState(int index)
    {
        var e = _entries;
        return index >= 0 && index < e.Length ? e[index].State : null;
    }

    public void Start()
    {
        if (_watching) return;
        foreach (var e in _entries) { e.State.Reset(); e.NextDue = 0; }
        var usable = _entries.Count(e => e.Rule.Enabled && e.Valid);
        var invalid = _entries.Where(e => e.Rule.Enabled && !e.Valid).ToList();
        foreach (var e in invalid)
            Log.Write($"Rule \"{e.Rule.Name}\" skipped: {string.Join(" ", e.Rule.Validate())}");
        _watching = true;
        Log.Write($"Watching started ({usable} rule{(usable == 1 ? "" : "s")}).");
        _wake.Set();
        WatchingChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!_watching) return;
        _watching = false;
        Log.Write("Watching stopped.");
        _wake.Set();
        WatchingChanged?.Invoke(false);
    }

    /// <summary>Emergency stop: cancels every running rule action.</summary>
    public void CancelRunningActions()
    {
        var old = Interlocked.Exchange(ref _actionsCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            if (!_watching)
            {
                _wake.WaitOne();
                continue;
            }

            var now = _clock.NowMs;
            var nextWake = now + 250;
            foreach (var e in _entries)
            {
                if (!_watching) break;
                if (!e.Rule.Enabled || !e.Valid || e.State.Running) continue;
                if (now < e.NextDue)
                {
                    nextWake = Math.Min(nextWake, e.NextDue);
                    continue;
                }
                e.NextDue = now + Math.Max(5, e.Rule.IntervalMs);
                nextWake = Math.Min(nextWake, e.NextDue);

                Rgb? reading;
                try { reading = Read(e.Rule); }
                catch (Exception ex) { reading = null; Log.Write($"Rule \"{e.Rule.Name}\": read failed: {ex.Message}"); }

                if (reading is null)
                {
                    if (!e.State.UnreadableLogged)
                    {
                        e.State.UnreadableLogged = true;
                        Log.Write($"Rule \"{e.Rule.Name}\": pixel {e.Rule.PixelText} cannot be read (off-screen, window missing or desktop locked); treated as no match.");
                    }
                }
                else e.State.UnreadableLogged = false;

                if (e.State.Update(e.Rule, reading, now)) Fire(e, reading);
            }
            var wait = (int)Math.Clamp(nextWake - _clock.NowMs, 1, 250);
            _wake.WaitOne(wait);
        }
    }

    private Rgb? Read(PixelRule r)
    {
        int x = r.X, y = r.Y;
        if (!string.IsNullOrWhiteSpace(r.Window))
        {
            var o = _screen.FindWindowClientOrigin(r.Window);
            if (o is null) return null;
            x += o.Value.X;
            y += o.Value.Y;
        }
        return _screen.GetPixel(x, y);
    }

    private void Fire(Entry e, Rgb? reading)
    {
        e.State.Running = true;
        Interlocked.Increment(ref _runningCount);
        Log.Write($"Rule \"{e.Rule.Name}\" fired ({e.Rule.ModeText}, pixel {e.Rule.PixelText} = {reading?.ToHex() ?? "?"}).");
        var token = _actionsCts.Token;
        Task.Run(() =>
        {
            try
            {
                RunList(e.Rule.Name, e.Rule.Actions, token);
                if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(e.Rule.MacroFile))
                {
                    var path = e.Rule.MacroFile!;
                    if (!Path.IsPathRooted(path) && _rulesDirectory() is { } dir) path = Path.Combine(dir, path);
                    RunList($"{e.Rule.Name} → {Path.GetFileName(path)}", MacroFile.Load(path), token);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Rule \"{e.Rule.Name}\": {ex.Message}");
            }
            finally
            {
                e.State.Running = false;
                Interlocked.Decrement(ref _runningCount);
                _wake.Set();
            }
        });
    }

    private void RunList(string name, IReadOnlyList<MacroAction> actions, CancellationToken token)
    {
        if (actions.Count == 0) return;
        var player = new Player(actions, _input, _screen, _clock, msg => Log.Write($"Rule \"{name}\": {msg}"), host: _host);
        var result = player.Run(token);
        if (result.Outcome != PlayOutcome.Finished)
            Log.Write($"Rule \"{name}\": {result.Message}");
    }

    public void Dispose()
    {
        _disposed = true;
        _watching = false;
        _actionsCts.Cancel();
        _wake.Set();
        _thread.Join(500);
    }
}
