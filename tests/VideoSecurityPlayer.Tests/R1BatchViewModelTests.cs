using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>在两种真实 ViewModel 边界注入可控预检和进度，验证有效性账本已接入业务流程。</summary>
public sealed class R1BatchViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 加密预检完成前编辑或关闭均拒绝旧计划(bool close)
    {
        using var lifetime = new TestDocumentLifetime();
        var planner = new EncryptionPlanner { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new Runner<PreparedEncryptionItem>();
        using var vm = new EncryptionBatchViewModel(new EncryptionService(), planner, runner, lifetime);
        await vm.AddFilesAsync(["one.mp4"]);
        vm.Password = vm.ConfirmPassword = "123456";
        var check = vm.CheckBatchCommand.ExecuteAsync(null);
        if (close) vm.Dispose();
        else vm.VideoTitle = "编辑后的标题";
        planner.Hold.SetResult();
        await check;
        Assert.False(vm.HasPreparedPlan);
        Assert.False(vm.StartBatchCommand.CanExecute(null));
        if (close)
        {
            Assert.Empty(vm.Password);
            Assert.Empty(vm.ConfirmPassword);
        }
        else
        {
            planner.Hold = null;
            await vm.CheckBatchCommand.ExecuteAsync(null);
            Assert.True(vm.StartBatchCommand.CanExecute(null));
            vm.OutputFilePath = "changed.secvid";
            Assert.False(vm.HasPreparedPlan);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 解密预检完成前编辑或关闭均拒绝旧计划(bool close)
    {
        using var lifetime = new TestDocumentLifetime();
        var service = new DecryptionService { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new Runner<CandidateDecryptionPreflight>();
        using var vm = new DecryptionBatchViewModel(service, runner, lifetime);
        await vm.AddFilesAsync(["one.secvid"]);
        vm.Password = "123456";
        vm.OutputDirectory = "output";
        var check = vm.CheckBatchCommand.ExecuteAsync(null);
        if (close) vm.Dispose();
        else vm.OutputDirectory = "changed-output";
        service.Hold.SetResult();
        await check;
        Assert.False(vm.HasPreparedPlan);
        Assert.False(vm.StartBatchCommand.CanExecute(null));
        if (close) Assert.Empty(vm.Password);
        else
        {
            service.Hold = null;
            await vm.CheckBatchCommand.ExecuteAsync(null);
            Assert.True(vm.StartBatchCommand.CanExecute(null));
            vm.UseSafeRename = false;
            Assert.False(vm.HasPreparedPlan);
        }
    }

    [Fact]
    public async Task 加密拒绝错批次已移除项目及结束关闭后的迟到进度()
    {
        using var lifetime = new TestDocumentLifetime();
        var runner = new Runner<PreparedEncryptionItem>();
        using var vm = new EncryptionBatchViewModel(new EncryptionService(), new EncryptionPlanner(), runner, lifetime);
        await vm.AddFilesAsync(["one.mp4", "two.mp4"]);
        vm.Password = vm.ConfirmPassword = "123456";
        await vm.CheckBatchCommand.ExecuteAsync(null);
        var first = vm.Items[0];
        var removed = vm.Items[1];
        var run = StartWithoutContext(() => vm.StartBatchCommand.ExecuteAsync(null));
        var callback = runner.Progress!;
        callback.Report(Progress(Guid.NewGuid(), first.ItemId, "错误批次"));
        Assert.NotEqual("错误批次", vm.StatusMessage);
        vm.SelectedItem = removed;
        vm.RemoveSelectedCommand.Execute(null);
        Assert.False(runner.IsStillQueued!(removed.ItemId));
        callback.Report(Progress(runner.RunId, removed.ItemId, "已移除"));
        Assert.NotEqual("已移除", vm.StatusMessage);
        callback.Report(Progress(runner.RunId, first.ItemId, "有效进度"));
        Assert.Equal("有效进度", vm.StatusMessage);
        runner.Completion.SetResult(new(2, 1, 0, 0, 1));
        await run;
        var final = vm.StatusMessage;
        callback.Report(Progress(runner.RunId, first.ItemId, "结束后"));
        Assert.Equal(final, vm.StatusMessage);
        // 重试仍需重新预检；旧回调即使伪装成新 RunId，也会被捕获的操作代次拒绝。
        first.Status.State = VideoTaskState.Failed;
        vm.SelectedItem = first;
        vm.RetrySelectedCommand.Execute(null);
        Assert.False(vm.HasPreparedPlan);
        await vm.CheckBatchCommand.ExecuteAsync(null);
        var second = StartWithoutContext(() => vm.StartBatchCommand.ExecuteAsync(null));
        callback.Report(Progress(runner.RunId, first.ItemId, "旧操作"));
        Assert.NotEqual("旧操作", vm.StatusMessage);
        vm.Dispose();
        final = vm.StatusMessage;
        runner.Progress!.Report(Progress(runner.RunId, first.ItemId, "关闭后"));
        runner.Completion.SetResult(new(1, 0, 0, 1, 0));
        await second;
        Assert.Equal(final, vm.StatusMessage);
        Assert.True(runner.CancelAllCalls > 0);
        Assert.Empty(vm.Password);
    }

    [Fact]
    public async Task 解密拒绝错批次已移除项目及结束关闭后的迟到进度()
    {
        using var lifetime = new TestDocumentLifetime();
        var runner = new Runner<CandidateDecryptionPreflight>();
        using var vm = new DecryptionBatchViewModel(new DecryptionService(), runner, lifetime);
        await vm.AddFilesAsync(["one.secvid", "two.secvid"]);
        vm.Password = "123456";
        vm.OutputDirectory = "output";
        await vm.CheckBatchCommand.ExecuteAsync(null);
        var first = vm.Items[0];
        var removed = vm.Items[1];
        var run = StartWithoutContext(() => vm.StartBatchCommand.ExecuteAsync(null));
        var callback = runner.Progress!;
        callback.Report(Progress(Guid.NewGuid(), first.ItemId, "错误批次"));
        Assert.NotEqual("错误批次", vm.StatusMessage);
        vm.SelectedItem = removed;
        vm.RemoveSelectedCommand.Execute(null);
        Assert.False(runner.IsStillQueued!(removed.ItemId));
        callback.Report(Progress(runner.RunId, removed.ItemId, "已移除"));
        Assert.NotEqual("已移除", vm.StatusMessage);
        callback.Report(Progress(runner.RunId, first.ItemId, "有效进度"));
        Assert.Equal("有效进度", vm.StatusMessage);
        runner.Completion.SetResult(new(2, 1, 0, 0, 1));
        await run;
        var final = vm.StatusMessage;
        callback.Report(Progress(runner.RunId, first.ItemId, "结束后"));
        Assert.Equal(final, vm.StatusMessage);
        first.State = VideoTaskState.Failed;
        vm.SelectedItem = first;
        vm.RetrySelectedCommand.Execute(null);
        Assert.False(vm.HasPreparedPlan);
        await vm.CheckBatchCommand.ExecuteAsync(null);
        var second = StartWithoutContext(() => vm.StartBatchCommand.ExecuteAsync(null));
        callback.Report(Progress(runner.RunId, first.ItemId, "旧操作"));
        Assert.NotEqual("旧操作", vm.StatusMessage);
        vm.Dispose();
        final = vm.StatusMessage;
        runner.Progress!.Report(Progress(runner.RunId, first.ItemId, "关闭后"));
        runner.Completion.SetResult(new(1, 0, 0, 1, 0));
        await second;
        Assert.Equal(final, vm.StatusMessage);
        Assert.True(runner.CancelAllCalls > 0);
        Assert.Empty(vm.Password);
    }

    private static VideoQueueProgress Progress(Guid run, Guid item, string message) =>
        new(run, item, VideoTaskState.Running, 1, 10, 10, 10, message);

    // 显式选择生产代码已有的同步进度端口，使测试无需等待线程池或 UI 消息循环。
    private static Task StartWithoutContext(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(null); return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class EncryptionPlanner : IVideoBatchEncryptionService
    {
        public TaskCompletionSource? Hold { get; set; }
        public async Task<BatchEncryptionPlan> PrepareAsync(IReadOnlyList<BatchEncryptionItemRequest> requests,
            OutputConflictPolicy conflictPolicy, int skippedSucceededCount, CancellationToken cancellationToken = default)
        {
            var items = requests.Select(x => new PreparedEncryptionItem(x.ItemId,
                new(x.InputPath, x.RequestedOutputPath, x.PublicTitle, x.PublicDescription), VideoPreflightResult.Ready(10), false)).ToArray();
            if (Hold is not null) await Hold.Task;
            return new(Guid.NewGuid(), new(items.Length, items.Length, 0, 0, 0, 0, items.Length * 10), items, []);
        }
    }
    private sealed class EncryptionService : IVideoEncryptionService
    {
        public Task<VideoPreflightResult> PreflightAsync(VideoEncryptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(10));
        public Task EncryptAsync(VideoEncryptionRequest request, string password, IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class DecryptionService : IVideoDecryptionService
    {
        public TaskCompletionSource? Hold { get; set; }
        public Task<IReadOnlyList<DecryptionCandidate>> InspectAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DecryptionCandidate>>(paths.Select(x => new DecryptionCandidate(
                x, Path.GetFileName(x), Path.GetFileNameWithoutExtension(x) + ".mp4", ".mp4", "标题", 10, true, "")).ToArray());
        public async Task<BatchDecryptionPreflightResult> PreflightAsync(IReadOnlyList<DecryptionCandidate> candidates,
            string outputDirectory, CancellationToken cancellationToken = default)
        {
            var items = candidates.Select(x => new CandidateDecryptionPreflight(x,
                Path.Combine(outputDirectory, x.OriginalFileName), VideoPreflightResult.Ready(10))).ToArray();
            if (Hold is not null) await Hold.Task;
            return new(VideoPreflightResult.Ready(items.Length * 10), items);
        }
        public Task<BatchDecryptionResult> DecryptBatchAsync(IReadOnlyList<DecryptionCandidate> candidates, string outputDirectory,
            string password, IProgress<BatchDecryptionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatchDecryptionResult(0, 0, 0, 0, []));
    }

    /// <summary>只控制完成与回调，故意忽略取消，以模拟关闭后仍到达的外部结果。</summary>
    private sealed class Runner<T> : ISequentialVideoQueueRunner<T> where T : IPreparedVideoQueueItem
    {
        public bool IsRunning { get; private set; }
        public Guid? CurrentItemId { get; private set; }
        public Guid RunId { get; private set; }
        public IProgress<VideoQueueProgress>? Progress { get; private set; }
        public Func<Guid, bool>? IsStillQueued { get; private set; }
        public TaskCompletionSource<VideoQueueRunResult> Completion { get; private set; } = new();
        public int CancelAllCalls { get; private set; }
        public async Task<VideoQueueRunResult> RunAsync(Guid runId, IReadOnlyList<T> items, Func<Guid, bool> isStillQueued,
            Func<T, IProgress<VideoTaskProgress>, CancellationToken, Task> executeAsync,
            IProgress<VideoQueueProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RunId = runId;
            Progress = progress;
            IsStillQueued = isStillQueued;
            CurrentItemId = items[0].ItemId;
            IsRunning = true;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return await Completion.Task; }
            finally { IsRunning = false; CurrentItemId = null; }
        }
        public bool CancelCurrent() => true;
        public void CancelAll() => CancelAllCalls++;
    }
}
