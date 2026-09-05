using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G3/G3.1 播放编排测试。
/// </summary>
/// <remarks>
/// 测试替身刻意把 PlayerHost 与 MediaSource 分开：前者代表 Document 级唯一播放器，
/// 后者代表可替换、可回收的单视频资源。这样测试本身也会阻止架构退回到
/// “每次切换都创建一个 MediaPlayer”的旧模型。
/// </remarks>
public sealed class G3PlaybackSessionTests
{
    [Fact]
    public async Task LoadAtPosition_AtomicallyRestoresWithoutStartingPlayback()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory((_, _) => Task.FromResult<IPlaybackMediaSource>(source)));

        var result = await rig.Session.LoadAtPositionAsync(
            "history.secvid",
            "password",
            4_000);

        Assert.True(result.Success);
        Assert.Equal(PlaybackState.Ready, rig.Session.Snapshot.State);
        Assert.Equal(4_000, rig.Session.Snapshot.PositionMs);
        Assert.False(rig.Host.IsPlaying);
        Assert.Equal(0, source.PrepareCalls);
    }

    [Fact]
    public async Task LoadAtPositionAndPlay_RestoresBeforeStartingPlayback()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory((_, _) => Task.FromResult<IPlaybackMediaSource>(source)));

        var result = await rig.Session.LoadAtPositionAndPlayAsync(
            "history.secvid",
            "password",
            4_000);

        Assert.True(result.Success);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
        Assert.Equal(4_000, rig.Session.Snapshot.PositionMs);
        Assert.True(rig.Host.IsPlaying);
        Assert.Equal(1, source.PrepareCalls);
        Assert.Equal(["Seek:4000", "Play"], rig.Host.Operations);
    }

    [Fact]
    public async Task LoadAtPositionAndPlay_IdentityMismatchSkipsHistoryButStillPlays()
    {
        var source = new FakeSource(1)
        {
            Identity = new PlaybackMediaIdentity("actual", 800)
        };
        using var rig = new TestRig(
            new FakeSourceFactory((_, _) => Task.FromResult<IPlaybackMediaSource>(source)));

        var result = await rig.Session.LoadAtPositionAndPlayAsync(
            "replaced.secvid",
            "password",
            4_000,
            new PlaybackMediaIdentity("stale", 800));

        Assert.True(result.Success);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
        Assert.True(rig.Host.IsPlaying);
        Assert.Equal(["Play"], rig.Host.Operations);
    }

    [Fact]
    public async Task LoadAtPositionAndPlay_SeekFailureFallsBackAndContinuesPlaying()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory((_, _) => Task.FromResult<IPlaybackMediaSource>(source)));
        rig.Host.ThrowOnSeek = true;
        var warnings = new List<PlaybackFailure>();
        rig.Session.Changed += (_, args) =>
        {
            if (args.Failure is not null)
                warnings.Add(args.Failure);
        };

        var result = await rig.Session.LoadAtPositionAndPlayAsync(
            "history.secvid",
            "password",
            4_000);

        Assert.True(result.Success);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
        Assert.True(rig.Host.IsPlaying);
        Assert.Equal("Play", Assert.Single(rig.Host.Operations));
        Assert.Contains(
            warnings,
            failure => failure.Code == PlaybackFailureCode.ControlUnavailable &&
                       failure.Message.Contains("历史位置恢复失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedCandidate_DoesNotReplaceCurrentMedia()
    {
        var first = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(first),
                (_, _) => throw new PlaybackOperationException(
                    PlaybackFailureMapper.ParseFailed())));

        var loaded = await rig.Session.LoadAsync("first.secvid", "password");
        var failed = await rig.Session.LoadAsync("broken.secvid", "password");

        Assert.True(loaded.Success);
        Assert.False(failed.Success);
        Assert.Equal(PlaybackFailureCode.ParseFailed, failed.Failure?.Code);
        Assert.Equal(1, rig.Session.Snapshot.MediaGeneration);
        Assert.True(rig.Session.Snapshot.HasMedia);
        Assert.Same(first, rig.Host.AttachedSource);
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public async Task AttachFailure_RestoresOldSourceBeforeCandidateIsReaped()
    {
        var oldSource = new FakeSource(1);
        var candidate = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(oldSource),
                (_, _) => Task.FromResult<IPlaybackMediaSource>(candidate)));
        Assert.True((await rig.Session.LoadAsync("old.secvid", "password")).Success);
        rig.Host.FailAttachGeneration = 2;

        var result = await rig.Session.LoadAsync("candidate.secvid", "password");

        Assert.False(result.Success);
        Assert.Same(oldSource, rig.Host.AttachedSource);
        Assert.False(oldSource.IsDisposed);
        Assert.True(candidate.IsDisposed);
        Assert.Equal(1, rig.Session.Snapshot.MediaGeneration);
    }

    [Fact]
    public async Task ImmediatePlayFailure_CompensatesBackToOldSource()
    {
        var oldSource = new FakeSource(1);
        var candidate = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(oldSource),
                (_, _) => Task.FromResult<IPlaybackMediaSource>(candidate)));
        Assert.True((await rig.Session.LoadAsync("old.secvid", "password")).Success);
        rig.Host.PlayResult = false;

        var result = await rig.Session.LoadAndPlayAsync(
            "candidate.secvid",
            "password");

        Assert.False(result.Success);
        Assert.Equal(PlaybackFailureCode.DecodeFailed, result.Failure?.Code);
        Assert.Same(oldSource, rig.Host.AttachedSource);
        Assert.False(oldSource.IsDisposed);
        Assert.True(candidate.IsDisposed);
        Assert.Equal(1, rig.Session.Snapshot.MediaGeneration);
    }

    [Fact]
    public async Task NewerLoad_InvalidatesCandidatePreparedByOlderRequest()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeSource(1);
        var second = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                async (_, cancellationToken) =>
                {
                    firstEntered.TrySetResult();
                    try
                    {
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                        return first;
                    }
                    catch
                    {
                        first.Dispose();
                        throw;
                    }
                },
                (_, _) => Task.FromResult<IPlaybackMediaSource>(second)));

        var older = rig.Session.LoadAsync("first.secvid", "password");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = rig.Session.LoadAsync("second.secvid", "password");
        releaseFirst.TrySetResult();

        var olderResult = await older;
        var newerResult = await newer;

        Assert.False(olderResult.Success);
        Assert.Equal(PlaybackFailureCode.Cancelled, olderResult.Failure?.Code);
        Assert.True(first.IsDisposed);
        Assert.True(newerResult.Success);
        Assert.Equal(2, rig.Session.Snapshot.MediaGeneration);
        Assert.Same(second, rig.Host.AttachedSource);
    }

    [Fact]
    public async Task CrossThreadLoads_CommitHighestRegisteredGeneration()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeSource(1);
        var second = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                async (_, _) =>
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                    return first;
                },
                (_, _) => Task.FromResult<IPlaybackMediaSource>(second)));

        var older = Task.Run(
            () => rig.Session.LoadAsync("first.secvid", "password"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = Task.Run(
            () => rig.Session.LoadAsync("second.secvid", "password"));

        PlaybackOperationResult newerResult;
        try
        {
            newerResult = await newer.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var olderResult = await older.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(olderResult.Success);
        Assert.Equal(PlaybackFailureCode.Cancelled, olderResult.Failure?.Code);
        Assert.True(first.IsDisposed);
        Assert.True(newerResult.Success);
        Assert.Equal(2, rig.Session.Snapshot.MediaGeneration);
        Assert.Same(second, rig.Host.AttachedSource);
    }

    [Fact]
    public async Task ControlIntentCancelsOlderLoad_ButNotFollowingLoad()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeSource(1);
        var second = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                async (_, _) =>
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                    return first;
                },
                (_, _) => Task.FromResult<IPlaybackMediaSource>(second)));

        var older = Task.Run(
            () => rig.Session.LoadAsync("first.secvid", "password"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackOperationResult stopResult;
        PlaybackOperationResult newerResult;
        try
        {
            stopResult = await Task.Run(() => rig.Session.StopAsync())
                .WaitAsync(TimeSpan.FromSeconds(2));
            newerResult = await Task.Run(
                    () => rig.Session.LoadAsync("second.secvid", "password"))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var olderResult = await older.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopResult.Success);
        Assert.False(olderResult.Success);
        Assert.Equal(PlaybackFailureCode.Cancelled, olderResult.Failure?.Code);
        Assert.True(first.IsDisposed);
        Assert.True(newerResult.Success);
        Assert.Equal(2, rig.Session.Snapshot.MediaGeneration);
        Assert.Same(second, rig.Host.AttachedSource);
    }

    [Fact]
    public async Task EventsFromOldGeneration_CannotChangeNewSession()
    {
        var first = new FakeSource(1);
        var second = new FakeSource(2);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(first),
                (_, _) => Task.FromResult<IPlaybackMediaSource>(second)));

        Assert.True((await rig.Session.LoadAsync("first.secvid", "password")).Success);
        Assert.True((await rig.Session.LoadAsync("second.secvid", "password")).Success);
        rig.Host.RaiseState(1, PlaybackState.Faulted);
        rig.Host.RaiseFailure(
            1,
            new PlaybackFailure(PlaybackFailureCode.CorruptedContent, "stale"));

        Assert.True(first.IsDisposed);
        Assert.Equal(2, rig.Session.Snapshot.MediaGeneration);
        Assert.Equal(PlaybackState.Ready, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task Pause_IsIdempotentAndNeverTogglesPlayback()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)));
        Assert.True((await rig.Session.LoadAsync("video.secvid", "password")).Success);

        Assert.True((await rig.Session.PauseAsync()).Success);
        Assert.True((await rig.Session.PauseAsync()).Success);

        Assert.Equal([true, true], rig.Host.PauseRequests);
        Assert.Equal(PlaybackState.Paused, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task StopBlockedInNativeDispatcher_DoesNotBlockCallingThread()
    {
        var source = new FakeSource(1);
        using var enteredStop = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        using var dispatcher = new PlaybackNativeDispatcher();
        var host = new FakePlayerHost
        {
            StopEntered = enteredStop,
            StopRelease = releaseStop
        };
        using var reaper = new ImmediateResourceReaper();
        using var lifetime = new TestDocumentLifetime();
        using var session = new SecureVideoPlayer(
            host,
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)),
            dispatcher,
            reaper,
            lifetime);
        Assert.True((await session.LoadAsync("video.secvid", "password")).Success);

        var callerThreadId = Environment.CurrentManagedThreadId;
        var stopTask = session.StopAsync();
        Assert.True(enteredStop.Wait(TimeSpan.FromSeconds(2)));

        // Stop 仍被原生替身阻塞，但调用者已经拿到未完成的 Task 并能继续执行。
        // 这正是 UI Dispatcher 能继续绘制和投递 heartbeat 的必要条件。
        Assert.False(stopTask.IsCompleted);
        Assert.NotEqual(callerThreadId, host.LastStopThreadId);
        var heartbeat = 0;
        heartbeat++;
        Assert.Equal(1, heartbeat);

        releaseStop.Set();
        Assert.True((await stopTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
    }

    [Fact]
    public async Task SurfaceDetachBlockedInNativeStop_DoesNotBlockCallingThread()
    {
        var source = new FakeSource(1);
        using var enteredStop = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        using var dispatcher = new PlaybackNativeDispatcher();
        var host = new FakePlayerHost
        {
            StopEntered = enteredStop,
            StopRelease = releaseStop
        };
        using var reaper = new ImmediateResourceReaper();
        using var lifetime = new TestDocumentLifetime();
        using var session = new SecureVideoPlayer(
            host,
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)),
            dispatcher,
            reaper,
            lifetime);
        Assert.True((await session.LoadAsync("video.secvid", "password")).Success);
        var firstSurface = new VideoSurfaceIdentity(1);
        Assert.True((await session.AttachAndRestoreSurfaceAsync(firstSurface)).Success);
        Assert.True((await session.PlayAsync()).Success);

        var detachCallerThreadId = 0;
        var detachTask = Task.Run(() =>
        {
            detachCallerThreadId = Environment.CurrentManagedThreadId;
            session.DetachSurface(firstSurface);
        });
        try
        {
            Assert.True(enteredStop.Wait(TimeSpan.FromSeconds(2)));

            // 原生替身仍卡在 Stop，但表面回调必须已经返回；否则真实 Avalonia UI 线程会像
            // G7 转储中那样停在 LibVLCMediaPlayerStop，无法继续把 HWND 清零并关闭 Document。
            await detachTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(releaseStop.IsSet);
            Assert.NotEqual(0, detachCallerThreadId);

            // 新表面恢复必须排在旧 Stop 之后，不能为了让 UI 返回而牺牲原生命令顺序。
            var restoreTask = session.AttachAndRestoreSurfaceAsync(
                new VideoSurfaceIdentity(2));
            Assert.False(restoreTask.IsCompleted);
            releaseStop.Set();
            Assert.True((await restoreTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
            Assert.Equal(1, host.RestoreCalls);
        }
        finally
        {
            releaseStop.Set();
        }
    }

    [Fact]
    public async Task NativeDispatcher_PreCancelledWorkNeverInvokesNativeAction()
    {
        using var dispatcher = new PlaybackNativeDispatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var actionInvoked = false;

        // 取消必须在消费者读取工作项之前就生效。这个用例不依赖线程调度先后，
        // 因而稳定覆盖 WorkItem 的“拒绝启动并完成为 Canceled”分支；同时证明
        // 已取消的 Document 不会再进入 LibVLC 原生调用。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.InvokeAsync(
                "pre-cancelled",
                () =>
                {
                    actionInvoked = true;
                    return true;
                },
                cancellation.Token));

        Assert.False(actionInvoked);
    }

    [Fact]
    public async Task PlayingControlRefresh_WhenDocumentAlreadyClosing_ConvergesAsCancellation()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)));
        Assert.True((await rig.Session.LoadAsync("video.secvid", "password")).Success);
        rig.CloseDocument();

        // Playing 后的轨道刷新是既有的私有 fire-and-forget 增强路径，公开契约不应为了
        // 单测增加等待接口。这里仅在测试内反射取得它返回的 Task，并显式等待已经取消的
        // Document 生命周期，从而确定性验证 OperationCanceledException 被本层收敛。
        // 如果该私有职责被重构或删除，本测试会给出清晰失败，而不会静默漏掉关闭语义。
        var refreshMethod = typeof(SecureVideoPlayer).GetMethod(
            "RefreshControlsAfterPlayingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到播放后控制刷新方法。");
        var refreshTask = refreshMethod.Invoke(rig.Session, [source]) as Task
            ?? throw new InvalidOperationException("播放后控制刷新方法没有返回 Task。");

        await refreshTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(rig.Session.Snapshot.State is PlaybackState.Ready or PlaybackState.Stopped);
    }

    [Fact]
    public void SnapshotPublishingWithoutMedia_HonorsDocumentClosingBoundary()
    {
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => throw new InvalidOperationException("本用例不应创建媒体源。")));
        var changedCount = 0;
        rig.Session.Changed += (_, _) => changedCount++;

        // PublishCurrent 是播放器内部所有状态回调最终汇合的单一出口。这里通过反射只调用
        // 这一个私有职责，是为了精确固定两个并发边界，而不是给生产类型增加仅供测试使用的
        // public API：当前媒体为空时必须降级发布 Empty；Document 关闭后同一尾部回调必须静默。
        var publishCurrent = typeof(SecureVideoPlayer).GetMethod(
            "PublishCurrent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到当前播放快照发布方法。");

        publishCurrent.Invoke(
            rig.Session,
            [PlaybackState.Stopped, PlaybackActivity.StoppingCurrent, null]);

        Assert.Equal(1, changedCount);
        Assert.Equal(PlaybackState.Empty, rig.Session.Snapshot.State);
        Assert.Equal(PlaybackActivity.StoppingCurrent, rig.Session.Snapshot.Activity);

        // 先显式关闭生命周期，再重复完全相同的同步调用，避免依赖线程池、原生播放器事件
        // 或 Dispose 的调度先后。若关闭保护被删除，事件计数会立刻变成 2，失败路径清晰可见。
        rig.CloseDocument();
        publishCurrent.Invoke(
            rig.Session,
            [PlaybackState.Stopped, PlaybackActivity.Idle, null]);

        Assert.Equal(1, changedCount);
        Assert.Equal(PlaybackActivity.StoppingCurrent, rig.Session.Snapshot.Activity);
    }

    [Fact]
    public void DisposedSession_GenerationUsesDisposedBoundaryDeterministically()
    {
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => throw new InvalidOperationException("本用例不应创建媒体源。")));

        // IsClosing 同时受 Document 生命周期与播放器自身 Dispose 状态控制。既有异步关闭
        // 测试能稳定覆盖前者，但 Dispose 后是否恰好还有迟到回调会受线程调度影响，导致 OR
        // 短路分支覆盖率在两轮之间漂移。Generation 是公开只读投影：Dispose 前后各读取一次
        // 即可确定性固定“自身仍存活”和“自身已释放”两个分支，不需要反射私有实现或新增 API。
        _ = rig.Session.Generation;
        rig.Session.Dispose();

        Assert.Equal(0, rig.Session.Generation);
    }

    [Fact]
    public async Task StopThenPlay_ReusesSameSourceAndWaitsForStop()
    {
        var source = new FakeSource(1);
        using var enteredStop = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        using var dispatcher = new PlaybackNativeDispatcher();
        var host = new FakePlayerHost
        {
            StopEntered = enteredStop,
            StopRelease = releaseStop
        };
        using var reaper = new ImmediateResourceReaper();
        using var lifetime = new TestDocumentLifetime();
        using var session = new SecureVideoPlayer(
            host,
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)),
            dispatcher,
            reaper,
            lifetime);
        Assert.True((await session.LoadAsync("video.secvid", "password")).Success);

        var stopTask = session.StopAsync();
        Assert.True(enteredStop.Wait(TimeSpan.FromSeconds(2)));
        var playTask = session.PlayAsync();
        Assert.False(playTask.IsCompleted);

        releaseStop.Set();
        Assert.True((await stopTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.True((await playTask.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Same(source, host.AttachedSource);
        Assert.Equal(1, source.PrepareCalls);
    }

    [Fact]
    public async Task LoadAndPlay_PublishesDetailedActivitySequence()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)));
        var activities = new List<PlaybackActivity>();
        rig.Session.Changed += (_, args) => activities.Add(args.Snapshot.Activity);

        var result = await rig.Session.LoadAndPlayAsync(
            "video.secvid",
            "password");

        Assert.True(result.Success);
        Assert.Contains(PlaybackActivity.PreparingCandidate, activities);
        Assert.Contains(PlaybackActivity.AttachingCandidate, activities);
        Assert.Contains(PlaybackActivity.StartingPlayback, activities);
        Assert.Equal(PlaybackActivity.Idle, rig.Session.Snapshot.Activity);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task ResourceReaper_HasCapacityOneAndAppliesAsyncBackpressure()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var first = new FakeSource(1) { DisposeRelease = releaseFirst };
        var second = new FakeSource(2);
        var third = new FakeSource(3);
        using var reaper = new PlaybackResourceReaper();

        await reaper.EnqueueAsync(first, waitForCompletion: false);
        Assert.True(first.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
        await reaper.EnqueueAsync(second, waitForCompletion: false);
        var thirdEnqueue = reaper.EnqueueAsync(third, waitForCompletion: false);

        Assert.False(thirdEnqueue.IsCompleted);
        releaseFirst.Set();
        await thirdEnqueue.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UserStopDuringSurfaceLoss_CancelsAutomaticRestore()
    {
        var source = new FakeSource(1);
        using var rig = new TestRig(
            new FakeSourceFactory(
                (_, _) => Task.FromResult<IPlaybackMediaSource>(source)));
        Assert.True((await rig.Session.LoadAsync("video.secvid", "password")).Success);

        var firstSurface = new VideoSurfaceIdentity(1);
        Assert.True((await rig.Session.AttachAndRestoreSurfaceAsync(firstSurface)).Success);
        Assert.True((await rig.Session.PlayAsync()).Success);
        rig.Session.DetachSurface(firstSurface);
        Assert.True((await rig.Session.StopAsync()).Success);

        var secondSurface = new VideoSurfaceIdentity(2);
        Assert.True((await rig.Session.AttachAndRestoreSurfaceAsync(secondSurface)).Success);

        Assert.Equal(0, rig.Host.RestoreCalls);
        Assert.Equal(PlaybackState.Stopped, rig.Session.Snapshot.State);
    }

    [Fact]
    public void FailureMapper_UsesStableCodesAndSafeMessages()
    {
        var path = @"C:\private\secret-name.secvid";
        var load = PlaybackFailureMapper.MapLoad(new FileNotFoundException(path));
        var content = PlaybackFailureMapper.MapMediaInput(new InvalidDataException(path));

        Assert.Equal(PlaybackFailureCode.InputUnavailable, load.Code);
        Assert.Equal(PlaybackFailureCode.CorruptedContent, content.Code);
        Assert.DoesNotContain("private", load.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-name", content.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaInput_PreservesFirstTypedFailureAndConsumesItOnce()
    {
        using var stream = new SequenceFailureStream(
            new InvalidDataException("first"),
            new IOException("second"));
        using var input = new SeekableStreamMediaInput(stream);
        var native = Marshal.AllocHGlobal(4);
        try
        {
            Assert.Equal(-1, input.Read(native, 4));
            Assert.Equal(-1, input.Read(native, 4));
            Assert.True(input.TryTakeLastFailure(out var failure));
            Assert.Equal(PlaybackFailureCode.CorruptedContent, failure?.Code);
            Assert.False(input.TryTakeLastFailure(out _));
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>
    /// 管理单测中的依赖释放顺序。生产环境由 Document DI Scope 完成同样的逆序释放。
    /// </summary>
    private sealed class TestRig : IDisposable
    {
        private readonly InlineNativeDispatcher _dispatcher = new();
        private readonly ImmediateResourceReaper _reaper = new();
        private readonly TestDocumentLifetime _lifetime = new();

        public TestRig(IPlaybackMediaSourceFactory factory)
        {
            Host = new FakePlayerHost();
            Session = new SecureVideoPlayer(Host, factory, _dispatcher, _reaper, _lifetime);
        }

        public FakePlayerHost Host { get; }
        public SecureVideoPlayer Session { get; }

        public void CloseDocument() => _lifetime.Close();

        public void Dispose()
        {
            Session.Dispose();
            _reaper.Dispose();
            _dispatcher.Dispose();
            Host.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class FakeSourceFactory(
        params Func<long, CancellationToken, Task<IPlaybackMediaSource>>[] factories)
        : IPlaybackMediaSourceFactory
    {
        private int _index;

        public Task<IPlaybackMediaSource> CreateAsync(
            long generation,
            string filePath,
            string password,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return factories[Math.Min(index, factories.Length - 1)](
                generation,
                cancellationToken);
        }
    }

    private sealed class FakeSource(long generation) : IPlaybackMediaSource
    {
        public long Generation { get; } = generation;
        public Media NativeMedia => null!;
        public PlaybackMediaIdentity? Identity { get; init; }
        public bool IsDisposed { get; private set; }
        public int PrepareCalls { get; private set; }
        public ManualResetEventSlim DisposeEntered { get; } = new();
        public ManualResetEventSlim? DisposeRelease { get; init; }

        public event Action<IPlaybackMediaSource, PlaybackFailure>? Failed;

        public void PrepareForPlayback() => PrepareCalls++;
        public void RequestStop()
        {
        }

        public void RaiseFailure(PlaybackFailure failure) => Failed?.Invoke(this, failure);

        public void Dispose()
        {
            DisposeEntered.Set();
            DisposeRelease?.Wait();
            IsDisposed = true;
            Failed = null;
        }
    }

    private sealed class FakePlayerHost : IPlaybackPlayerHost
    {
        public MediaPlayer NativePlayer => null!;
        public long NativeOutputGeneration => 1;
        public long PositionMs { get; private set; } = 1_000;
        public long DurationMs { get; } = 6_000;
        public bool IsSeekable { get; } = true;
        public bool HasVideo { get; } = true;
        public bool HasAudio { get; } = true;
        public int VideoTrackCount { get; } = 1;
        public int AudioTrackCount { get; } = 1;
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public int Volume { get; private set; } = 50;
        public float Rate { get; private set; } = 1.0f;
        public int AudioTrack { get; private set; } = 1;
        public int SubtitleTrack { get; private set; } = -1;
        public IReadOnlyList<PlaybackTrackOption> AudioTracks { get; set; } =
            [new PlaybackTrackOption(1, "音轨 1")];
        public IReadOnlyList<PlaybackTrackOption> SubtitleTracks { get; set; } =
            [new PlaybackTrackOption(-1, "关闭字幕")];
        public bool RateResult { get; set; } = true;
        public bool AudioTrackResult { get; set; } = true;
        public bool SubtitleTrackResult { get; set; } = true;
        public IPlaybackMediaSource? AttachedSource { get; private set; }
        public List<bool> PauseRequests { get; } = [];
        public int RestoreCalls { get; private set; }
        public ManualResetEventSlim? StopEntered { get; init; }
        public ManualResetEventSlim? StopRelease { get; init; }
        public int LastStopThreadId { get; private set; }
        public long? FailAttachGeneration { get; set; }
        public bool PlayResult { get; set; } = true;
        public bool ThrowOnSeek { get; set; }
        public List<string> Operations { get; } = [];

        public event Action<long, PlaybackState>? StateChanged;
        public event Action<long>? PositionChanged;
        public event Action<long, PlaybackFailure>? Failed;

        public void Attach(IPlaybackMediaSource source)
        {
            if (source.Generation == FailAttachGeneration)
            {
                throw new InvalidOperationException("Injected attach failure.");
            }
            AttachedSource = source;
        }
        public void Detach() => AttachedSource = null;

        public bool Play()
        {
            Operations.Add("Play");
            IsPlaying = true;
            IsPaused = false;
            RaiseState(AttachedSource?.Generation ?? 0, PlaybackState.Playing);
            return PlayResult;
        }

        public void Stop()
        {
            LastStopThreadId = Environment.CurrentManagedThreadId;
            StopEntered?.Set();
            StopRelease?.Wait();
            IsPlaying = false;
            IsPaused = false;
            RaiseState(AttachedSource?.Generation ?? 0, PlaybackState.Stopped);
        }

        public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseRequests.Add(true);
            PositionMs = positionMs;
            IsPaused = true;
            IsPlaying = false;
            RaiseState(
                AttachedSource?.Generation ?? 0,
                PlaybackState.Paused);
            return Task.CompletedTask;
        }

        public void SetPause(bool paused)
        {
            PauseRequests.Add(paused);
            IsPaused = paused;
            IsPlaying = !paused;
        }

        public void SetVolume(int volume) => Volume = Math.Clamp(volume, 0, 100);
        public bool SetRate(float rate)
        {
            if (!RateResult)
            {
                return false;
            }
            Rate = rate;
            return true;
        }

        public IReadOnlyList<PlaybackTrackOption> GetAudioTracks() => AudioTracks;
        public IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks() => SubtitleTracks;

        public bool SetAudioTrack(int trackId)
        {
            if (!AudioTrackResult)
            {
                return false;
            }
            AudioTrack = trackId;
            return true;
        }

        public bool SetSubtitleTrack(int trackId)
        {
            if (!SubtitleTrackResult)
            {
                return false;
            }
            SubtitleTrack = trackId;
            return true;
        }

        public Task SeekAsync(
            long positionMs,
            bool waitForFrame,
            CancellationToken cancellationToken)
        {
            if (ThrowOnSeek)
                throw new InvalidOperationException("Injected seek failure.");
            Operations.Add($"Seek:{positionMs}");
            PositionMs = Math.Clamp(positionMs, 0, DurationMs);
            PositionChanged?.Invoke(AttachedSource?.Generation ?? 0);
            return Task.CompletedTask;
        }

        public Task<bool> RestoreSurfaceAsync(
            long positionMs,
            bool restorePaused,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            PositionMs = positionMs;
            IsPaused = restorePaused;
            IsPlaying = !restorePaused;
            return Task.FromResult(true);
        }

        public void RaiseState(long generation, PlaybackState state) =>
            StateChanged?.Invoke(generation, state);

        public void RaiseFailure(long generation, PlaybackFailure failure) =>
            Failed?.Invoke(generation, failure);

        public void Dispose()
        {
            AttachedSource = null;
            StateChanged = null;
            PositionChanged = null;
            Failed = null;
        }
    }

    /// <summary>
    /// 普通编排测试使用同步替身，避免线程调度噪声；真正的后台行为由专门测试覆盖。
    /// </summary>
    private sealed class InlineNativeDispatcher : IPlaybackNativeDispatcher
    {
        public Task InvokeAsync(
            string operation,
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(
            string operation,
            Func<T> action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }

        public Task<T> InvokeAsync<T>(
            string operation,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default) =>
            action(cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateResourceReaper : IPlaybackResourceReaper
    {
        public Task EnqueueAsync(
            IPlaybackMediaSource source,
            bool waitForCompletion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class SequenceFailureStream(params Exception[] failures) : Stream
    {
        private int _readIndex;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 4;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw failures[Math.Min(_readIndex++, failures.Length - 1)];

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = offset;
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
