using System.Diagnostics;
using System.Threading.Channels;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 接管已经与 MediaPlayer 解绑的媒体资源，并在专用消费者上确定性释放。
/// </summary>
internal interface IPlaybackResourceReaper : IDisposable
{
    Task EnqueueAsync(
        IPlaybackMediaSource source,
        bool waitForCompletion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Document 级、有界的旧媒体回收器。
/// </summary>
/// <remarks>
/// 容量固定为 1 是有意的背压策略：快速切换时最多允许“一个正在释放、一个等待释放”，
/// 不会为每次点击创建一个长期阻塞的 Task 并同时保留文件、密钥和明文缓存。
/// 回收器绝不接触 MediaPlayer、HWND 或 Avalonia Dispatcher；调用方必须先完成解绑。
/// </remarks>
internal sealed class PlaybackResourceReaper : IPlaybackResourceReaper
{
    private readonly Channel<ReapItem> _queue =
        Channel.CreateBounded<ReapItem>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly Task _consumer;
    private int _disposeState;

    public PlaybackResourceReaper()
    {
        PlaybackResourceDiagnostics.ResourceReaperCreated();
        _consumer = Task.Run(ConsumeAsync);
    }

    public async Task EnqueueAsync(
        IPlaybackMediaSource source,
        bool waitForCompletion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);
        ArgumentNullException.ThrowIfNull(source);

        var item = new ReapItem(source);
        await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        if (waitForCompletion)
        {
            await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _consumer.GetAwaiter().GetResult();
        PlaybackResourceDiagnostics.ResourceReaperDisposed();
        Volatile.Write(ref _disposeState, 2);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                // IPlaybackMediaSource.Dispose 内部固定按 Media -> MediaInput ->
                // 加密流/文件句柄 -> 缓存/密钥上下文的所有权逆序释放。
                item.Source.Dispose();
                item.Completion.TrySetResult();
                Debug.WriteLine(
                    $"[VideoSecurityPlayer.Playback.Reaper] generation={item.Source.Generation} " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} " +
                    $"threadId={Environment.CurrentManagedThreadId} success=true");
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
                Debug.WriteLine(
                    $"[VideoSecurityPlayer.Playback.Reaper] generation={item.Source.Generation} " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} " +
                    $"threadId={Environment.CurrentManagedThreadId} success=false");
            }
        }
    }

    private sealed class ReapItem(IPlaybackMediaSource source)
    {
        public IPlaybackMediaSource Source { get; } = source;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
