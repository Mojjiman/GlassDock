using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Widgets;

/// <summary>
/// Shared timers for widget sampling (design §8.1): one pool timer per interval, however many widgets
/// use it. The sample runs on the pool, the apply step on the dispatcher. Paused while the dock is
/// hidden (fullscreen, locked session), so widgets cost nothing then.
/// </summary>
public sealed class RefreshScheduler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<int, Bucket> _buckets = [];
    private volatile bool _paused;

    public RefreshScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            Log.Debug(value ? "Widget refresh paused" : "Widget refresh resumed");
            // Values are stale after a pause (a 30 s bucket would otherwise wait up to 30 s).
            if (!value) foreach (var s in Snapshot()) ThreadPool.QueueUserWorkItem(_ => Run(s));
        }
    }

    /// <summary>Calls <paramref name="sample"/> on the pool, then <paramref name="apply"/> on the UI thread, every <paramref name="interval"/>, starting now.</summary>
    public IDisposable Every(TimeSpan interval, Action? sample, Action apply)
    {
        int ms = Math.Max(1, (int)interval.TotalMilliseconds);
        var sub = new Subscription(this, ms, sample, apply);
        lock (_gate)
        {
            if (!_buckets.TryGetValue(ms, out var bucket))
            {
                bucket = new Bucket();
                bucket.Timer = new Timer(_ => Tick(bucket), null, ms, ms);
                _buckets[ms] = bucket;
            }
            bucket.Subs.Add(sub);
        }
        ThreadPool.QueueUserWorkItem(_ => Run(sub));
        return sub;
    }

    private void Tick(Bucket bucket)
    {
        if (_paused) return;
        Subscription[] subs;
        lock (_gate) subs = [.. bucket.Subs];
        foreach (var s in subs) Run(s);
    }

    private void Run(Subscription s)
    {
        if (s.Disposed || _paused) return;
        if (s.Skip > 0) { s.Skip--; return; }
        try
        {
            s.Sample?.Invoke();
            s.Failures = 0;
        }
        catch (Exception ex)
        {
            // Back off exponentially so a broken sampler does not flood the log every second.
            s.Failures++;
            s.Skip = Math.Min(1 << Math.Min(s.Failures, 16), AppConstants.Widgets.ErrorBackoffMaxTicks);
            Log.Warn($"Widget sampler failed ({s.Failures} in a row); skipping {s.Skip} ticks", ex);
            return;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (s.Disposed) return;
            try { s.Apply(); }
            catch (Exception ex) { Log.Warn("Widget update failed", ex); }
        });
    }

    private List<Subscription> Snapshot()
    {
        lock (_gate) return _buckets.Values.SelectMany(b => b.Subs).ToList();
    }

    private void Remove(Subscription s)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(s.IntervalMs, out var bucket)) return;
            bucket.Subs.Remove(s);
            if (bucket.Subs.Count > 0) return;
            bucket.Timer?.Dispose();
            _buckets.Remove(s.IntervalMs);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var b in _buckets.Values) b.Timer?.Dispose();
            _buckets.Clear();
        }
    }

    private sealed class Bucket
    {
        public Timer? Timer;
        public readonly List<Subscription> Subs = [];
    }

    private sealed class Subscription(RefreshScheduler owner, int intervalMs, Action? sample, Action apply) : IDisposable
    {
        public int IntervalMs { get; } = intervalMs;
        public Action? Sample { get; } = sample;
        public Action Apply { get; } = apply;
        public volatile bool Disposed;
        public int Failures;
        public int Skip;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            owner.Remove(this);
        }
    }
}
