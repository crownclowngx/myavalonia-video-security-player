namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

/// <summary>
/// Document 级严格顺序视频任务运行器。
/// </summary>
/// <typeparam name="TPreparedItem">已经完成批次预检的领域项目。</typeparam>
/// <remarks>
/// 单消费者是安全与资源约束，而不只是性能选择：多个大文件同时执行会争用磁盘、
/// PBKDF2 和大缓冲区，也会让取消和总体进度语义变得不可预测。
///
/// 批次取消源与当前项取消源刻意分离。“取消当前”只触发当前项的链接取消源，待该项
/// 自己释放输入流和 partial 后继续下一项；“取消全部”才触发批次取消源并停止循环。
/// </remarks>
public sealed class SequentialVideoQueueRunner<TPreparedItem> :
    ISequentialVideoQueueRunner<TPreparedItem>,
    IDisposable
    where TPreparedItem : IPreparedVideoQueueItem
{
    private readonly object _sync = new();
    private CancellationTokenSource? _batchCancellation;
    private CancellationTokenSource? _currentCancellation;
    private Guid? _currentItemId;
    private int _isRunning;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    /// <inheritdoc />
    public Guid? CurrentItemId
    {
        get
        {
            lock (_sync)
                return _currentItemId;
        }
    }

    /// <inheritdoc />
    public async Task<VideoQueueRunResult> RunAsync(
        Guid runId,
        IReadOnlyList<TPreparedItem> items,
        Func<Guid, bool> isStillQueued,
        Func<TPreparedItem, IProgress<VideoTaskProgress>, CancellationToken, Task> executeAsync,
        IProgress<VideoQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isStillQueued);
        ArgumentNullException.ThrowIfNull(executeAsync);
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new InvalidOperationException("同一个 Document 队列不能并行启动两个批次。");

        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
            _batchCancellation = batchCancellation;

        var totalBytes = items.Aggregate(0L, (total, item) => SaturatingAdd(total, item.RequiredBytes));
        long completedBytes = 0;
        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;
        var removed = 0;

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (batchCancellation.IsCancellationRequested)
                {
                    cancelled += ReportRemainingCancelled(
                        runId,
                        items,
                        index,
                        isStillQueued,
                        completedBytes,
                        totalBytes,
                        progress);
                    break;
                }

                if (!isStillQueued(item.ItemId))
                {
                    removed++;
                    completedBytes = SaturatingAdd(completedBytes, item.RequiredBytes);
                    continue;
                }

                using var currentCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(batchCancellation.Token);
                lock (_sync)
                {
                    _currentCancellation = currentCancellation;
                    _currentItemId = item.ItemId;
                }

                var latestProcessed = 0L;
                var itemProgress = new InlineProgress<VideoTaskProgress>(value =>
                {
                    latestProcessed = Math.Clamp(value.ProcessedBytes, 0, Math.Max(0, item.RequiredBytes));
                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        value.State,
                        latestProcessed,
                        completedBytes,
                        totalBytes,
                        value.Message,
                        value.FailureCode));
                });

                try
                {
                    // 运行器在调用领域服务前先发布 Running，使“取消当前”不依赖具体加密器
                    // 是否以及何时上报第一条字节进度。
                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        VideoTaskState.Running,
                        0,
                        completedBytes,
                        totalBytes,
                        "正在处理..."));
                    await executeAsync(item, itemProgress, currentCancellation.Token)
                        .ConfigureAwait(false);
                    succeeded++;
                    completedBytes = SaturatingAdd(completedBytes, item.RequiredBytes);
                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        VideoTaskState.Succeeded,
                        item.RequiredBytes,
                        completedBytes - Math.Max(0, item.RequiredBytes),
                        totalBytes,
                        "处理完成"));
                }
                catch (OperationCanceledException) when (currentCancellation.IsCancellationRequested)
                {
                    cancelled++;

                    // 取消当前后把该项权重视为已经走完，避免继续下一项时总体进度倒退；
                    // 取消全部则在这里停住实际百分比，不伪造“批次 100% 完成”。
                    if (!batchCancellation.IsCancellationRequested)
                        completedBytes = SaturatingAdd(completedBytes, item.RequiredBytes);

                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        VideoTaskState.Cancelled,
                        latestProcessed,
                        batchCancellation.IsCancellationRequested
                            ? completedBytes
                            : completedBytes - Math.Max(0, item.RequiredBytes),
                        totalBytes,
                        "已取消",
                        VideoTaskFailureCode.Cancelled));

                    if (batchCancellation.IsCancellationRequested)
                    {
                        cancelled += ReportRemainingCancelled(
                            runId,
                            items,
                            index + 1,
                            isStillQueued,
                            completedBytes,
                            totalBytes,
                            progress);
                        break;
                    }
                }
                catch (VideoTaskException ex)
                {
                    failed++;
                    completedBytes = SaturatingAdd(completedBytes, item.RequiredBytes);
                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        VideoTaskState.Failed,
                        latestProcessed,
                        completedBytes - Math.Max(0, item.RequiredBytes),
                        totalBytes,
                        ex.Message,
                        ex.FailureCode));
                }
                catch
                {
                    failed++;
                    completedBytes = SaturatingAdd(completedBytes, item.RequiredBytes);
                    progress?.Report(CreateProgress(
                        runId,
                        item,
                        VideoTaskState.Failed,
                        latestProcessed,
                        completedBytes - Math.Max(0, item.RequiredBytes),
                        totalBytes,
                        "处理视频时发生未预期错误。",
                        VideoTaskFailureCode.Unknown));
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_currentCancellation, currentCancellation))
                        {
                            _currentCancellation = null;
                            _currentItemId = null;
                        }
                    }
                }
            }

            return new VideoQueueRunResult(items.Count, succeeded, failed, cancelled, removed);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_batchCancellation, batchCancellation))
                    _batchCancellation = null;
                _currentCancellation = null;
                _currentItemId = null;
            }

            Volatile.Write(ref _isRunning, 0);
        }
    }

    /// <inheritdoc />
    public bool CancelCurrent()
    {
        lock (_sync)
        {
            if (_currentCancellation is null)
                return false;

            try
            {
                _currentCancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void CancelAll()
    {
        lock (_sync)
        {
            try
            {
                _batchCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 批次刚好完成时不需要重复取消。
            }
        }
    }

    /// <summary>
    /// 释放运行器只发送取消，不同步等待文件清理；Document 关闭不能阻塞 UI 线程。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelAll();
    }

    private static int ReportRemainingCancelled(
        Guid runId,
        IReadOnlyList<TPreparedItem> items,
        int startIndex,
        Func<Guid, bool> isStillQueued,
        long completedBytes,
        long totalBytes,
        IProgress<VideoQueueProgress>? progress)
    {
        var count = 0;
        for (var index = startIndex; index < items.Count; index++)
        {
            var item = items[index];
            if (!isStillQueued(item.ItemId))
                continue;

            count++;
            progress?.Report(CreateProgress(
                runId,
                item,
                VideoTaskState.Cancelled,
                0,
                completedBytes,
                totalBytes,
                "批次已取消，项目未开始。",
                VideoTaskFailureCode.Cancelled));
        }

        return count;
    }

    private static VideoQueueProgress CreateProgress(
        Guid runId,
        TPreparedItem item,
        VideoTaskState state,
        long processedBytes,
        long completedBeforeItem,
        long totalBytes,
        string message,
        VideoTaskFailureCode? failureCode = null)
    {
        var requiredBytes = Math.Max(0, item.RequiredBytes);
        var filePercentage = requiredBytes == 0
            ? state == VideoTaskState.Succeeded ? 100 : 0
            : Math.Clamp(processedBytes * 100d / requiredBytes, 0, 100);
        var overallProcessed = SaturatingAdd(completedBeforeItem, processedBytes);
        var overallPercentage = totalBytes == 0
            ? state == VideoTaskState.Succeeded ? 100 : 0
            : Math.Clamp(overallProcessed * 100d / totalBytes, 0, 100);

        return new VideoQueueProgress(
            runId,
            item.ItemId,
            state,
            processedBytes,
            requiredBytes,
            filePercentage,
            overallPercentage,
            message,
            failureCode);
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
