using System.Diagnostics;
using System.Threading.Channels;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 串行执行同一 Document 内所有可能进入 LibVLC 原生等待的控制命令。
/// </summary>
/// <remarks>
/// 接口刻意只暴露“提交工作并异步等待”，不暴露线程或 Channel：
/// 上层只表达 Play/Stop/Media setter 等业务意图，调度策略可独立测试和替换，
/// 符合依赖倒置原则，也避免播放器服务自行散落 Task.Run。
/// </remarks>
internal interface IPlaybackNativeDispatcher : IDisposable
{
    Task InvokeAsync(
        string operation,
        Action action,
        CancellationToken cancellationToken = default);

    Task<T> InvokeAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken = default);

    Task<T> InvokeAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于单消费者 Channel 的 Document 级原生操作调度器。
/// </summary>
/// <remarks>
/// 单消费者是这里最重要的设计约束。LibVLC 的 Stop、Media setter 与 Play
/// 即使各自在不同线程“异步”执行，彼此并发仍可能争用同一原生状态机并造成卡顿或崩溃。
/// 一个长期消费者既把工作移出 UI 线程，也保证命令严格按提交顺序执行。
/// </remarks>
internal sealed class PlaybackNativeDispatcher : IPlaybackNativeDispatcher
{
    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Task _consumer;
    private int _disposeState;

    public PlaybackNativeDispatcher()
    {
        PlaybackResourceDiagnostics.NativeDispatcherCreated();
        _consumer = Task.Run(ConsumeAsync);
    }

    public Task InvokeAsync(
        string operation,
        Action action,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            operation,
            () =>
            {
                action();
                return true;
            },
            cancellationToken);

    public Task<T> InvokeAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken = default) =>
        Enqueue(new SyncWorkItem<T>(operation, action, cancellationToken));

    public Task<T> InvokeAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) =>
        Enqueue(new AsyncWorkItem<T>(operation, action, cancellationToken));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _consumer.GetAwaiter().GetResult();
        PlaybackResourceDiagnostics.NativeDispatcherDisposed();
        Volatile.Write(ref _disposeState, 2);
    }

    private Task<T> Enqueue<T>(WorkItem<T> item)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);
        if (!_queue.Writer.TryWrite(item))
        {
            throw new ObjectDisposedException(nameof(PlaybackNativeDispatcher));
        }
        return item.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await item.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private abstract class WorkItem
    {
        private readonly long _queuedAt = Stopwatch.GetTimestamp();

        protected WorkItem(string operation, CancellationToken cancellationToken)
        {
            Operation = operation;
            CancellationToken = cancellationToken;
        }

        protected string Operation { get; }
        protected CancellationToken CancellationToken { get; }
        public abstract Task ExecuteAsync();

        protected long BeginExecution()
        {
            var startedAt = Stopwatch.GetTimestamp();
            Debug.WriteLine(
                $"[VideoSecurityPlayer.Playback.Native] operation={Operation} " +
                $"phase=start queueMs={Stopwatch.GetElapsedTime(_queuedAt, startedAt).TotalMilliseconds:F1} " +
                $"threadId={Environment.CurrentManagedThreadId}");
            return startedAt;
        }

        protected void EndExecution(long startedAt, Exception? failure = null)
        {
            Debug.WriteLine(
                $"[VideoSecurityPlayer.Playback.Native] operation={Operation} phase=finish " +
                $"elapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} " +
                $"threadId={Environment.CurrentManagedThreadId} success={failure is null}");
        }
    }

    private abstract class WorkItem<T>(
        string operation,
        CancellationToken cancellationToken) : WorkItem(operation, cancellationToken)
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        protected void SetResult(T result) => _completion.TrySetResult(result);
        protected void SetException(Exception exception) =>
            _completion.TrySetException(exception);
        protected void SetCancelled() =>
            _completion.TrySetCanceled(CancellationToken);

        protected bool TryStart()
        {
            if (!CancellationToken.IsCancellationRequested)
            {
                return true;
            }

            SetCancelled();
            return false;
        }
    }

    private sealed class SyncWorkItem<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken) : WorkItem<T>(operation, cancellationToken)
    {
        public override Task ExecuteAsync()
        {
            if (!TryStart())
            {
                // WorkItem<T>.Task 是本工作项的完成信号，因此这里显式限定类型名，
                // 返回消费者循环自身需要等待的“执行已结束”任务。
                return global::System.Threading.Tasks.Task.CompletedTask;
            }

            var startedAt = BeginExecution();
            try
            {
                SetResult(action());
                EndExecution(startedAt);
            }
            catch (Exception ex)
            {
                SetException(ex);
                EndExecution(startedAt, ex);
            }

            return global::System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private sealed class AsyncWorkItem<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) : WorkItem<T>(operation, cancellationToken)
    {
        public override async Task ExecuteAsync()
        {
            if (!TryStart())
            {
                return;
            }

            var startedAt = BeginExecution();
            try
            {
                SetResult(await action(CancellationToken).ConfigureAwait(false));
                EndExecution(startedAt);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                SetCancelled();
                EndExecution(startedAt, new OperationCanceledException());
            }
            catch (Exception ex)
            {
                SetException(ex);
                EndExecution(startedAt, ex);
            }
        }
    }
}
