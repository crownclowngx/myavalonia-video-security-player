using System.Collections.Concurrent;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G5 公共队列、冲突计划和敏感信息边界测试。
/// </summary>
/// <remarks>
/// 测试替身刻意不执行密码学，使顺序、取消和竞态能够快速、确定地覆盖 100 项规模；
/// 真实 SECVID03 百文件闭环由同文件末尾的验收测试独立覆盖。
/// </remarks>
[Collection(Secvid03Collection.Name)]
public sealed class G5BatchQueueTests(Secvid03Fixture fixture)
{
    [Fact]
    public async Task SequentialRunner_ExecutesOneHundredItemsInOrderWithOneActiveItem()
    {
        using var runner = new SequentialVideoQueueRunner<PreparedStub>();
        var items = Enumerable.Range(0, 100)
            .Select(index => new PreparedStub(Guid.NewGuid(), index, 1))
            .ToArray();
        var order = new ConcurrentQueue<int>();
        var active = 0;
        var maximumActive = 0;

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            items,
            _ => true,
            async (item, _, token) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                order.Enqueue(item.Ordinal);
                await Task.Delay(1, token);
                Interlocked.Decrement(ref active);
            });

        Assert.Equal(100, result.SucceededCount);
        Assert.Equal(Enumerable.Range(0, 100), order);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task SequentialRunner_CancelCurrentContinuesButCancelAllStopsRemainingItems()
    {
        using var currentRunner = new SequentialVideoQueueRunner<PreparedStub>();
        var currentItems = Enumerable.Range(0, 3)
            .Select(index => new PreparedStub(Guid.NewGuid(), index, 1))
            .ToArray();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentCalls = new ConcurrentQueue<int>();

        var currentRun = currentRunner.RunAsync(
            Guid.NewGuid(),
            currentItems,
            _ => true,
            async (item, _, token) =>
            {
                currentCalls.Enqueue(item.Ordinal);
                if (item.Ordinal == 0)
                {
                    firstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(currentRunner.CancelCurrent());
        var currentResult = await currentRun;

        Assert.Equal(1, currentResult.CancelledCount);
        Assert.Equal(2, currentResult.SucceededCount);
        Assert.Equal([0, 1, 2], currentCalls);

        using var allRunner = new SequentialVideoQueueRunner<PreparedStub>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCalls = new ConcurrentQueue<int>();
        var allRun = allRunner.RunAsync(
            Guid.NewGuid(),
            currentItems,
            _ => true,
            async (item, _, token) =>
            {
                allCalls.Enqueue(item.Ordinal);
                allStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        allRunner.CancelAll();
        var allResult = await allRun;

        Assert.Equal(3, allResult.CancelledCount);
        Assert.Equal([0], allCalls);
    }

    [Fact]
    public async Task SequentialRunner_FailureAndRemovedWaitingItemDoNotBlockLaterWork()
    {
        using var runner = new SequentialVideoQueueRunner<PreparedStub>();
        var items = Enumerable.Range(0, 4)
            .Select(index => new PreparedStub(Guid.NewGuid(), index, 10))
            .ToArray();
        var queued = new ConcurrentDictionary<Guid, byte>();
        foreach (var item in items)
            queued[item.ItemId] = 0;
        queued.TryRemove(items[2].ItemId, out _);
        var calls = new ConcurrentQueue<int>();

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            items,
            queued.ContainsKey,
            (item, _, _) =>
            {
                calls.Enqueue(item.Ordinal);
                return item.Ordinal == 1
                    ? Task.FromException(new VideoTaskException(
                        VideoTaskFailureCode.DiskIo,
                        "注入的稳定单项错误。"))
                    : Task.CompletedTask;
            });

        Assert.Equal([0, 1, 3], calls);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.RemovedBeforeStartCount);
    }

    [Fact]
    public async Task BatchEncryptionPlan_HandlesConflictsAndCumulativeVolumeSpace()
    {
        var directory = fixture.DirectoryPath;
        var requested = Path.Combine(directory, "g5-same.secvid");
        var requests = new[]
        {
            Request("one.mp4", requested),
            Request("two.mp4", requested)
        };
        var fakeSingle = new FixedEncryptionService(requiredBytes: 10, availableBytes: 15);
        var service = new VideoBatchEncryptionService(fakeSingle, new OutputPathConflictResolver());

        var blocked = await service.PrepareAsync(
            requests,
            OutputConflictPolicy.Block,
            skippedSucceededCount: 0);
        Assert.Equal(1, blocked.Summary.ConflictCount);
        Assert.Equal(1, blocked.Summary.RunnableCount);
        Assert.Equal(1, blocked.Summary.BlockingCount);
        Assert.Contains(blocked.Items[1].Preflight.Issues, issue =>
            issue.Code == VideoTaskFailureCode.OutputConflict &&
            issue.Severity == PreflightSeverity.Blocking);

        var renamed = await service.PrepareAsync(
            requests,
            OutputConflictPolicy.GenerateUniqueName,
            skippedSucceededCount: 0);
        Assert.Equal("g5-same (1).secvid", Path.GetFileName(renamed.Items[1].Request.OutputPath));
        Assert.Contains(renamed.Items[1].Preflight.Issues, issue =>
            issue.Code == VideoTaskFailureCode.OutputConflict &&
            issue.Severity == PreflightSeverity.Warning);

        // 两项分别只需 10 字节，但同卷累计 20 字节超过已知 15 字节。该回归不能由
        // 单文件预检发现，因此必须由批次计划服务阻止第二项。
        Assert.Contains(renamed.Items[1].Preflight.Issues, issue =>
            issue.Code == VideoTaskFailureCode.InsufficientDiskSpace);
    }

    [Fact]
    public void G5QueueAndPlanModels_DoNotExposePasswordMembers()
    {
        var types = new[]
        {
            typeof(BatchEncryptionItemRequest),
            typeof(PreparedEncryptionItem),
            typeof(BatchEncryptionPlan),
            typeof(VideoQueueBatchSummary),
            typeof(VideoQueueProgress),
            typeof(VideoQueueRunResult),
            typeof(EncryptionQueueItemViewModel),
            typeof(VideoQueueItemStatusViewModel)
        };

        foreach (var type in types)
        {
            Assert.DoesNotContain(type.GetProperties(), property =>
                ContainsSensitiveName(property.Name));
            Assert.DoesNotContain(type.GetFields(), field =>
                ContainsSensitiveName(field.Name));
        }
    }

    [Fact]
    public async Task RealEncryption_ProcessesOneHundredSmallFilesSequentiallyWithoutPartials()
    {
        var single = new VideoEncryptorService(new Secvid03Encryptor());
        var batch = new VideoBatchEncryptionService(single, new OutputPathConflictResolver());
        using var runner = new SequentialVideoQueueRunner<PreparedEncryptionItem>();
        var requests = Enumerable.Range(0, 100)
            .Select(index => new BatchEncryptionItemRequest(
                Guid.NewGuid(),
                fixture.OriginalPath,
                Path.Combine(fixture.DirectoryPath, $"g5-real-{index:D3}.secvid"),
                $"公开标题 {index}",
                string.Empty))
            .ToArray();

        var plan = await batch.PrepareAsync(
            requests,
            OutputConflictPolicy.Block,
            skippedSucceededCount: 0);
        Assert.Equal(100, plan.Summary.RunnableCount);

        var active = 0;
        var maximumActive = 0;
        var result = await runner.RunAsync(
            Guid.NewGuid(),
            plan.Items,
            _ => true,
            async (item, progress, token) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                try
                {
                    await single.EncryptAsync(
                        item.Request,
                        Secvid03Fixture.Password,
                        progress,
                        token);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        Assert.Equal(100, result.SucceededCount);
        Assert.Equal(1, maximumActive);
        Assert.All(plan.Items, item => Assert.True(File.Exists(item.Request.OutputPath)));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.partial-*"));
    }

    private BatchEncryptionItemRequest Request(string inputName, string outputPath) =>
        new(
            Guid.NewGuid(),
            Path.Combine(fixture.DirectoryPath, inputName),
            outputPath,
            string.Empty,
            string.Empty);

    private static bool ContainsSensitiveName(string name) =>
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Key", StringComparison.OrdinalIgnoreCase);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                return;
        }
    }

    /// <summary>只携带顺序和进度权重的运行器测试项目。</summary>
    private sealed record PreparedStub(
        Guid ItemId,
        int Ordinal,
        long RequiredBytes) : IPreparedVideoQueueItem;

    /// <summary>
    /// 返回确定空间证据的单文件替身，使批次累计空间测试不依赖开发机真实磁盘容量。
    /// </summary>
    private sealed class FixedEncryptionService(long requiredBytes, long availableBytes) :
        IVideoEncryptionService
    {
        public Task<VideoPreflightResult> PreflightAsync(
            VideoEncryptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(requiredBytes, availableBytes));

        public Task EncryptAsync(
            VideoEncryptionRequest request,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
