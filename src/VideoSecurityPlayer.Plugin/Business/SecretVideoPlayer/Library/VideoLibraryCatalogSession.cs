using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>媒体目录会话发送给展示层的一组不可变变化。</summary>
/// <remarks>
/// 目录会话只描述文件系统事实，不依赖 Avalonia 集合或 Dispatcher。展示层可以在自己的
/// UI 线程中一次应用整个批次，从而避免每个 FileSystemWatcher 事件都触发一次布局。
/// </remarks>
public sealed record VideoLibraryCatalogBatch(
    IReadOnlyList<VideoLibraryScanResult> Upserts,
    IReadOnlyList<string> RemovedPaths,
    bool ReplaceAll,
    bool IsScanning,
    string StatusMessage);

/// <summary>
/// 为一个目录创建可取消的“初始扫描 + 后续增量监听”会话。
/// </summary>
public interface IVideoLibraryCatalogSession
{
    IAsyncEnumerable<VideoLibraryCatalogBatch> ObserveAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// 使用有界事件通道串行处理目录变化，必要时退化为节流后的完整重扫。
/// </summary>
public sealed class VideoLibraryCatalogSession(IVideoLibraryScanner scanner)
    : IVideoLibraryCatalogSession
{
    private const int WatcherCapacity = 512;
    private const int InitialBatchSize = 50;
    private const int StormUniquePathThreshold = 128;
    private static readonly TimeSpan EventMergeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StormQuietDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan[] ReadRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    ];

    private readonly IVideoLibraryScanner _scanner =
        scanner ?? throw new ArgumentNullException(nameof(scanner));

    public async IAsyncEnumerable<VideoLibraryCatalogBatch> ObserveAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("视频文件夹不能为空。", nameof(folderPath));
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(folderPath);
        var output = Channel.CreateUnbounded<VideoLibraryCatalogBatch>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        // 生产者拥有 watcher 和原始事件通道。异步迭代被取消或消费方提前退出时，
        // linkedCancellation 会让生产者进入 finally 并关闭 watcher，不留下全局监听器。
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(
            fullPath,
            options,
            output.Writer,
            linkedCancellation.Token);

        try
        {
            await foreach (var batch in output.Reader.ReadAllAsync(cancellationToken))
                yield return batch;
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ProduceAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        ChannelWriter<VideoLibraryCatalogBatch> output,
        CancellationToken cancellationToken)
    {
        var events = Channel.CreateBounded<RawChange>(
            new BoundedChannelOptions(WatcherCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false
            });
        var overflowed = 0;
        FileSystemWatcher? watcher = null;

        void Queue(RawChange change)
        {
            if (!events.Writer.TryWrite(change))
                Interlocked.Exchange(ref overflowed, 1);
        }

        try
        {
            if (Directory.Exists(folderPath))
            {
                watcher = CreateWatcher(folderPath, options, Queue);
                watcher.EnableRaisingEvents = true;
            }

            await WriteInitialScanAsync(
                folderPath,
                options,
                output,
                cancellationToken).ConfigureAwait(false);

            // 扫描器测试替身允许使用虚拟目录。生产扫描器会在目录不存在时报告错误，
            // 但这里仍让无真实目录的测试会话在初始结果后自然结束，而不是永久等待事件。
            if (watcher is null)
                return;

            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var changes = new List<RawChange>();
                while (events.Reader.TryRead(out var first))
                    changes.Add(first);

                await Task.Delay(EventMergeDelay, cancellationToken).ConfigureAwait(false);
                while (events.Reader.TryRead(out var pending))
                    changes.Add(pending);

                var forceRescan = Interlocked.Exchange(ref overflowed, 0) != 0 ||
                                  changes.Any(change => change.Kind == RawChangeKind.Rescan);
                var uniquePaths = changes
                    .Where(change => !string.IsNullOrWhiteSpace(change.Path))
                    .Select(change => NormalizePath(change.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                forceRescan |= uniquePaths > StormUniquePathThreshold;

                if (forceRescan)
                {
                    // 溢出后逐项推断会遗漏事实。必须等到“完整一个静默窗口都没有新事件”
                    // 才开始扫描；旧实现只固定等待一次并立即清空队列，文件风暴仍在继续时
                    // 会从半成品目录建立 ReplaceAll，且后续通知可能已被 watcher 丢弃。
                    IReadOnlyList<VideoLibraryScanResult> snapshot;
                    while (true)
                    {
                        await Task.Delay(StormQuietDelay, cancellationToken)
                            .ConfigureAwait(false);
                        var changedDuringQuiet =
                            Interlocked.Exchange(ref overflowed, 0) != 0;
                        while (events.Reader.TryRead(out _))
                            changedDuringQuiet = true;
                        if (changedDuringQuiet)
                            continue;

                        snapshot = await ReadFullSnapshotAsync(
                            folderPath,
                            options,
                            cancellationToken).ConfigureAwait(false);

                        // 扫描自身可能较慢。若扫描期间又有变化，丢弃这份中间快照并重新
                        // 等待静默；展示层只会看到最后一次收敛事实，不会短暂删除仍在写入的项。
                        var changedDuringScan =
                            Interlocked.Exchange(ref overflowed, 0) != 0;
                        while (events.Reader.TryRead(out _))
                            changedDuringScan = true;
                        if (!changedDuringScan)
                            break;
                    }

                    await output.WriteAsync(new VideoLibraryCatalogBatch(
                        snapshot,
                        Array.Empty<string>(),
                        true,
                        false,
                        $"目录变化较多，已重新扫描 {snapshot.Count} 个"),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WriteDeltaAsync(changes, output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            output.TryWrite(new VideoLibraryCatalogBatch(
                Array.Empty<VideoLibraryScanResult>(),
                Array.Empty<string>(),
                false,
                false,
                MapDirectoryError(ex)));
        }
        finally
        {
            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            events.Writer.TryComplete();
            output.TryComplete();
        }
    }

    private async Task WriteInitialScanAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        ChannelWriter<VideoLibraryCatalogBatch> output,
        CancellationToken cancellationToken)
    {
        var batch = new List<VideoLibraryScanResult>(InitialBatchSize);
        var processed = 0;
        await foreach (var item in _scanner
                           .ScanAsync(folderPath, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            batch.Add(item);
            processed++;
            if (batch.Count < InitialBatchSize)
                continue;

            await output.WriteAsync(new VideoLibraryCatalogBatch(
                batch.ToArray(),
                Array.Empty<string>(),
                false,
                true,
                $"正在扫描，已读取 {processed} 个"), cancellationToken).ConfigureAwait(false);
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await output.WriteAsync(new VideoLibraryCatalogBatch(
                batch.ToArray(),
                Array.Empty<string>(),
                false,
                true,
                $"正在扫描，已读取 {processed} 个"), cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync(new VideoLibraryCatalogBatch(
            Array.Empty<VideoLibraryScanResult>(),
            Array.Empty<string>(),
            false,
            false,
            $"扫描完成，共 {processed} 个"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VideoLibraryScanResult>> ReadFullSnapshotAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        CancellationToken cancellationToken)
    {
        var all = new List<VideoLibraryScanResult>();
        await foreach (var item in _scanner
                           .ScanAsync(folderPath, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            all.Add(item);
        }

        return all;
    }

    private async Task WriteDeltaAsync(
        IReadOnlyList<RawChange> changes,
        ChannelWriter<VideoLibraryCatalogBatch> output,
        CancellationToken cancellationToken)
    {
        var actions = new Dictionary<string, RawChangeKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Path))
                continue;
            actions[NormalizePath(change.Path)] = change.Kind;
        }

        var removed = new List<string>();
        var upserts = new List<VideoLibraryScanResult>();
        foreach (var (path, kind) in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind == RawChangeKind.Delete || !VideoLibraryScanner.IsCandidate(path))
            {
                removed.Add(path);
                continue;
            }

            var item = await ReadWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
            if (item is null)
                removed.Add(path);
            else
                upserts.Add(item);
        }

        if (removed.Count == 0 && upserts.Count == 0)
            return;

        await output.WriteAsync(new VideoLibraryCatalogBatch(
            upserts,
            removed,
            false,
            false,
            $"目录已更新：新增或修改 {upserts.Count} 个，删除 {removed.Count} 个"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VideoLibraryScanResult?> ReadWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await _scanner.ReadFileAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null ||
                    result.State == VideoLibraryMetadataState.Ready ||
                    attempt >= ReadRetryDelays.Length ||
                    !result.ErrorMessage.Contains("占用", StringComparison.Ordinal))
                {
                    return result;
                }
                await Task.Delay(ReadRetryDelays[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException &&
                attempt < ReadRetryDelays.Length)
            {
                await Task.Delay(ReadRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FileSystemWatcher CreateWatcher(
        string folderPath,
        VideoLibraryScanOptions options,
        Action<RawChange> queue)
    {
        var watcher = new FileSystemWatcher(folderPath)
        {
            IncludeSubdirectories = options.IncludeSubdirectories,
            Filter = "*",
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024
        };
        watcher.Created += (_, e) => QueueCandidate(queue, e.FullPath, RawChangeKind.Upsert);
        watcher.Changed += (_, e) => QueueCandidate(queue, e.FullPath, RawChangeKind.Upsert);
        watcher.Deleted += (_, e) => QueueCandidate(queue, e.FullPath, RawChangeKind.Delete);
        watcher.Renamed += (_, e) =>
        {
            QueueCandidate(queue, e.OldFullPath, RawChangeKind.Delete);
            QueueCandidate(queue, e.FullPath, RawChangeKind.Upsert);
        };
        watcher.Error += (_, _) => queue(new RawChange(RawChangeKind.Rescan, string.Empty));
        return watcher;
    }

    private static void QueueCandidate(
        Action<RawChange> queue,
        string path,
        RawChangeKind kind)
    {
        if (VideoLibraryScanner.IsCandidate(path))
            queue(new RawChange(kind, path));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string MapDirectoryError(Exception ex) => ex switch
    {
        DirectoryNotFoundException => "文件夹不存在或已被删除",
        UnauthorizedAccessException => "没有访问该文件夹的权限",
        IOException => "读取或监听文件夹失败，请检查磁盘状态",
        _ => "媒体目录会话意外停止，请手动刷新"
    };

    private enum RawChangeKind
    {
        Upsert,
        Delete,
        Rescan
    }

    private sealed record RawChange(RawChangeKind Kind, string Path);
}
