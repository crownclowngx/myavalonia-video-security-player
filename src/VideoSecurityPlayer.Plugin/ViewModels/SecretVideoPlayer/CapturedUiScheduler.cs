namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 捕获创建 ViewModel 时的 UI 同步上下文，使 ViewModel 不依赖具体 UI 框架。
/// </summary>
internal sealed class CapturedUiScheduler
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context))
        {
            action();
            return;
        }

        _context.Post(static state => ((Action)state!).Invoke(), action);
    }

    public CapturedUiPeriodicTimer CreatePeriodicTimer(
        TimeSpan interval,
        Action tick) =>
        new(this, interval, tick);
}

/// <summary>
/// 周期源在后台计时，但每次状态更新都回到所属 ViewModel 捕获的 UI 上下文。
/// </summary>
internal sealed class CapturedUiPeriodicTimer : IDisposable
{
    private readonly CapturedUiScheduler _scheduler;
    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private readonly Timer _timer;
    private long _generation;
    private int _running;
    private int _disposed;

    public CapturedUiPeriodicTimer(
        CapturedUiScheduler scheduler,
        TimeSpan interval,
        Action tick)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Volatile.Write(ref _running, 1);
        Interlocked.Increment(ref _generation);
        _timer.Change(_interval, _interval);
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        Interlocked.Increment(ref _generation);
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        var generation = Interlocked.Read(ref _generation);
        if (Volatile.Read(ref _running) == 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _scheduler.Post(() =>
        {
            if (Volatile.Read(ref _running) != 0 &&
                Volatile.Read(ref _disposed) == 0 &&
                generation == Interlocked.Read(ref _generation))
            {
                _tick();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _running, 0);
        Interlocked.Increment(ref _generation);
        _timer.Dispose();
    }
}
