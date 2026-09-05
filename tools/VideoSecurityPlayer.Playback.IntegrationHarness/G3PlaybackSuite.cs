using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Mvvm.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement;
using MyAvaloniaManagement.Business.Documents;
using MyAvaloniaManagement.Business.Docking;
using MyAvaloniaManagement.Business.Layout;
using MyAvaloniaManagement.Business.Workspace;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Constants;
using VideoSecurityPlayer.Plugin;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;

namespace VideoSecurityPlayer.Playback.IntegrationHarness;

internal sealed class G3PlaybackHarnessRunner(
    IServiceProvider services,
    HarnessOptions options)
    : IAcceptanceSuite
{
    private const string Password = "G3-Integration-Public-Password!";
    private readonly List<string> _failures = [];
    private readonly List<long> _nearEndSeekChunkTrace = [];
    private readonly List<long> _randomSeekTargets = [];
    private readonly Dictionary<string, long> _stageDurationsMs = [];
    private readonly List<WeakReference<SecretVideoPlayerViewModel>>
        _closedDocumentReferences = [];
    private readonly List<WeakReference<SecretVideoPlayerView>> _closedViewReferences = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private long _uiHeartbeatMaxIntervalMs;
    private long _nativeOutputGeneration;
    private long _surfaceGenerationBeforeMediaSwitch;
    private long _surfaceGenerationAfterMediaSwitch;
    private ProcessResourceSnapshot? _processBaseline;
    private ProcessResourceSnapshot? _processFinal;

    public async Task<int> RunAsync()
    {
        HarnessReport report;
        string? tempDirectory = null;
        try
        {
            await WaitUntilAsync(
                () => Application.Current?.ApplicationLifetime is
                    IClassicDesktopStyleApplicationLifetime { MainWindow: not null },
                TimeSpan.FromSeconds(10),
                "宿主主窗口未能启动。");

            tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"videosecurityplayer-g3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var stage = Stopwatch.StartNew();
            var assets = await Task.Run(() => PrepareAssetsAsync(tempDirectory));
            _stageDurationsMs["assetPreparation"] = stage.ElapsedMilliseconds;

            var mainWindow = (Application.Current!.ApplicationLifetime as
                IClassicDesktopStyleApplicationLifetime)!.MainWindow!;
            var documentCoordinator = services
                .GetRequiredService<DocumentPersistenceCoordinator>();
            var workspace = services.GetRequiredService<WorkspaceSession>();
            var documentDock = workspace.DockFactory
                .GetDockable<Dock.Model.Controls.IDocumentDock>(DockLayoutIds.Documents)
                as DocumentDock ?? throw new InvalidOperationException("宿主没有规范 Documents Dock。");

            var placeholder = await CreateDocumentAsync(
                documentCoordinator,
                documentDock);
            placeholder.Title = "G3 占位标签";
            // 占位标签本身也是一个真实 Document Scope。G3.1 的 PlayerHost、
            // Dispatcher 和 Reaper 在 Scope 创建时即存在，所以中途资源检查应回到
            // “仅占位标签存活”的基线，而不是错误地要求全进程为 default。
            var placeholderResources = SecurePlaybackDiagnostics.CaptureResources();
            if (options.Suite == HarnessSuite.G10)
            {
                await StabilizeProcessAsync();
                _processBaseline = ProcessResourceSnapshot.Capture();
            }

            await MeasureStageAsync(
                "functionalMatrix",
                () => RunFunctionalMatrixAsync(
                    mainWindow,
                    documentCoordinator,
                    workspace,
                    documentDock,
                    placeholder,
                    assets,
                    placeholderResources));

            await MeasureStageAsync(
                "libraryPresentation",
                () => VerifyLibraryPresentationAsync(
                    mainWindow,
                    documentCoordinator,
                    workspace,
                    documentDock,
                    placeholder));

            await MeasureStageAsync(
                "lifecycleStress",
                () => RunLifecycleStressAsync(
                    mainWindow,
                    documentCoordinator,
                    workspace,
                    documentDock,
                    placeholder,
                    assets,
                    placeholderResources));

            workspace.DockFactory.CloseDockable(placeholder.Dockable);
            await DrainDispatcherAsync();
            // G8 会把每轮已关闭的 Document 与 View 记录为弱引用。这里在生成最终报告前执行一次
            // 可控的完整回收，使“弱引用仍存活”只表示视觉树、Dock 或异步回调仍持有对象，而不是
            // 单纯因为 GC 尚未自然发生。该稳定化也会等待终结器和 Dispatcher 尾部工作完成。
            await StabilizeProcessAsync();
            if (options.Suite == HarnessSuite.G10)
            {
                _processFinal = ProcessResourceSnapshot.Capture();
            }

            var finalResources = SecurePlaybackDiagnostics.CaptureResources();
            Require(
                finalResources == default,
                $"最终播放资源未归零: {finalResources}");
            Require(
                CountUnexpectedTopLevelWindows() == 0,
                "检测到 LibVLC 创建的意外独立顶层窗口。");

            await MeasureStageAsync(
                "roundTripAndFileLocks",
                () => Task.Run(() => VerifyRoundTripAndFileLocksAsync(assets)));

            report = BuildReport(
                success: _failures.Count == 0,
                assets,
                finalResources);
        }
        catch (Exception ex)
        {
            _failures.Add($"{ex.GetType().Name}: {ex.Message}");
            report = BuildReport(
                success: false,
                [],
                SecurePlaybackDiagnostics.CaptureResources());
        }
        finally
        {
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _failures.Add($"临时目录清理失败: {ex.GetType().Name}");
                }
            }
        }

        await WriteReportAsync(report with
        {
            Success = report.Success && _failures.Count == 0,
            Failures = _failures.ToArray()
        });

        Console.WriteLine(
            report.Success && _failures.Count == 0
                ? "G3 Windows x64 播放集成门禁通过。"
                : "G3 Windows x64 播放集成门禁失败。");
        foreach (var failure in _failures)
        {
            Console.Error.WriteLine($"- {failure}");
        }
        return report.Success && _failures.Count == 0 ? 0 : 1;
    }

    private async Task RunFunctionalMatrixAsync(
        Window mainWindow,
        DocumentPersistenceCoordinator documentCoordinator,
        WorkspaceSession workspace,
        DocumentDock documentDock,
        HarnessPlayerDocument placeholder,
        IReadOnlyList<HarnessAsset> assets,
        PlaybackResourceSnapshot expectedResources)
    {
        var document = await CreateDocumentAsync(documentCoordinator, documentDock);
        try
        {
        documentDock.ActiveDockable = document.Dockable;
        await WaitUntilAsync(
            () => document.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(10),
            "真实视频 HWND 未创建。");

        var first = assets[0];
        Require(
            await document.PlayerViewModel.LoadAndPlayMediaAsync(first.EncryptedPath, Password),
            $"真实 MP4 加载播放失败: {document.PlayerViewModel.StatusMessage}");
        await WaitUntilAsync(
            () => document.PlayerViewModel.PlaybackSnapshot.State == PlaybackState.Playing &&
                  document.Model.PlayerViewModel.PlaybackSnapshot.PositionMs > 100,
            TimeSpan.FromSeconds(8),
            "真实 MP4 未进入播放状态或时间未推进。");
        Require(document.PlayerViewModel.PlaybackSnapshot.HasVideo, "MP4 未识别到视频轨。");
        Require(document.PlayerViewModel.PlaybackSnapshot.HasAudio, "MP4 未识别到音轨。");
        var documentOutputGeneration =
            document.PlayerViewModel.SurfaceSession?.VideoOutput.Generation ?? 0;
        var documentSurfaceGeneration =
            document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration;
        _nativeOutputGeneration = documentOutputGeneration;
        Require(documentOutputGeneration > 0, "Document 未暴露稳定的视频输出代次。");

        // G6：真实 LibVLC 倍速必须经过会话端口设置，并反映到不可变控制快照。
        foreach (var rate in new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f })
        {
            var rateResult = await document.PlayerViewModel.SetPlaybackRateAsync(rate);
            Require(rateResult.Success, $"真实媒体设置 {rate} 倍速失败。");
            Require(
                Math.Abs(document.PlayerViewModel.PlaybackSnapshot.Controls.Rate - rate) < 0.001f,
                $"{rate} 倍速未写入控制快照。");
        }
        Require(
            (await document.PlayerViewModel.SetPlaybackRateAsync(1.0f)).Success,
            "真实媒体未能恢复 1.0 倍速。");

        await WaitUntilAsync(
            () => document.PlayerViewModel.PlaybackSnapshot.Controls.AudioTracks.Count >= 2 &&
                  document.PlayerViewModel.PlaybackSnapshot.Controls.SubtitleTracks
                      .Any(track => track.Id >= 0),
            TimeSpan.FromSeconds(5),
            "真实双音轨或内嵌字幕未进入控制快照。");

        // 不只验证“列表能读到”，还逐条执行真实 LibVLC 切换，并确认选中 ID
        // 回写到不可变控制快照。字幕 -1 是产品定义的稳定“关闭字幕”语义。
        foreach (var audioTrack in document.PlayerViewModel.PlaybackSnapshot.Controls.AudioTracks)
        {
            var trackResult = await document.PlayerViewModel.SelectAudioTrackAsync(audioTrack.Id);
            Require(trackResult.Success, $"真实音轨 {audioTrack.Id} 切换失败。");
            Require(
                document.PlayerViewModel.PlaybackSnapshot.Controls.SelectedAudioTrackId ==
                audioTrack.Id,
                $"真实音轨 {audioTrack.Id} 的选择未写入控制快照。");
        }

        var subtitleTrack = document.PlayerViewModel.PlaybackSnapshot.Controls.SubtitleTracks
            .First(track => track.Id >= 0);
        var subtitleResult =
            await document.PlayerViewModel.SelectSubtitleTrackAsync(subtitleTrack.Id);
        Require(subtitleResult.Success, "真实内嵌字幕启用失败。");
        Require(
            document.PlayerViewModel.PlaybackSnapshot.Controls.SelectedSubtitleTrackId ==
            subtitleTrack.Id,
            "真实内嵌字幕选中 ID 未写入控制快照。");
        var disableSubtitleResult =
            await document.PlayerViewModel.SelectSubtitleTrackAsync(-1);
        Require(disableSubtitleResult.Success, "真实字幕关闭失败。");
        Require(
            document.PlayerViewModel.PlaybackSnapshot.Controls.SelectedSubtitleTrackId == -1,
            "关闭字幕后控制快照未记录 -1。");

        // G6 的内容区全屏复用同一个 PlayerShell。真实窗口中完成一次进出，验证
        // 宿主全屏承载没有替换 Document 级视频输出，也没有丢失表面恢复链路。
        Require(
            document.PlayerViewModel.ToggleFullscreenCommand.CanExecute(null),
            "真实媒体加载后全屏命令不可用。");
        document.PlayerViewModel.ToggleFullscreenCommand.Execute(null);
        await WaitUntilAsync(
            () => document.PlayerViewModel.IsFullscreen &&
                  !document.PlayerViewModel.IsFullscreenTransitioning,
            TimeSpan.FromSeconds(8),
            "进入窗口内容区全屏超时。");
        Require(
            documentOutputGeneration ==
            document.PlayerViewModel.SurfaceSession?.VideoOutput.Generation,
            "进入全屏替换了 Document 级视频输出。");
        var fullscreenLayer = mainWindow.GetVisualDescendants()
            .OfType<Border>()
            .SingleOrDefault(control => control.Name == "ContentFullscreenLayer");
        Require(fullscreenLayer is not null, "宿主没有创建内容区全屏承载层。");
        if (fullscreenLayer is not null)
        {
            var fullscreenParent = fullscreenLayer.GetVisualParent() as Control;
            Require(fullscreenLayer.IsVisible, "进入全屏后宿主承载层不可见。");
            Require(
                fullscreenParent is not null &&
                Math.Abs(fullscreenLayer.Bounds.Width - fullscreenParent.Bounds.Width) <= 1 &&
                Math.Abs(fullscreenLayer.Bounds.Height - fullscreenParent.Bounds.Height) <= 1,
                $"全屏承载层未铺满窗口内容区: layer={fullscreenLayer.Bounds.Size}, " +
                $"parent={fullscreenParent?.Bounds.Size}。");
        }
        Require(
            mainWindow is IWindowContentFullscreenHost host &&
            host.TryPresent(new Border()) is null,
            "第二个展示者错误地取得了窗口内容区全屏租约。");
        document.PlayerViewModel.ToggleFullscreenCommand.Execute(null);
        await WaitUntilAsync(
            () => !document.PlayerViewModel.IsFullscreen &&
                  !document.PlayerViewModel.IsFullscreenTransitioning &&
                  document.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(8),
            "退出窗口内容区全屏超时。");
        Require(
            fullscreenLayer?.IsVisible == false,
            "退出全屏后宿主承载层仍然可见。");
        Require(
            document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration >
            documentSurfaceGeneration,
            "全屏进出没有重建 HWND 视频表面。");

        // 后续“普通媒体切换不得重建表面”的断言应当以全屏退出后的当前代次为
        // 基准。若仍与进入全屏前的代次比较，会把全屏按设计产生的 HWND 迁移
        // 错报为普通媒体切换造成的重建。
        documentSurfaceGeneration =
            document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration;

        var random = new Random(0x4733);
        var randomMaximum = Math.Max(
            251,
            document.PlayerViewModel.PlaybackSnapshot.DurationMs - 1_000);
        for (var index = 0; index < 3; index++)
        {
            var target = random.NextInt64(250, randomMaximum);
            _randomSeekTargets.Add(target);
            var randomSeek = await document.PlayerViewModel.SeekMediaAsync(
                target,
                waitForFrame: true);
            Require(randomSeek.Success, $"固定种子随机 Seek {index + 1} 失败。");
            Require(
                Math.Abs(document.PlayerViewModel.PlaybackSnapshot.PositionMs -
                         target) <= 750,
                $"固定种子随机 Seek {index + 1} 的位置误差超过 750 ms。");
        }

        // 固定种子最后一次 Seek 对 3.5 秒短片恰好会落到“距结尾 1 秒”的上限。
        // 若直接在该位置验证暂停，等待原生 Paused 事件与稳定采样的时间足以让媒体自然
        // 到达结尾并回零，最终会把“媒体结束”误报为“暂停仍在推进”。暂停门禁必须先把
        // 播放头放回有充足余量的中段，才能只验证 Pause 自身的语义。
        var pauseVerificationPosition =
            document.PlayerViewModel.PlaybackSnapshot.DurationMs / 2;
        var pausePreparationSeek = await document.PlayerViewModel.SeekMediaAsync(
            pauseVerificationPosition,
            waitForFrame: true);
        Require(pausePreparationSeek.Success, "暂停验证前的中段 Seek 失败。");
        Require(
            Math.Abs(document.PlayerViewModel.PlaybackSnapshot.PositionMs -
                     pauseVerificationPosition) <= 750,
            "暂停验证前的中段 Seek 位置误差超过 750 ms。");

        await document.PlayerViewModel.PauseCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => document.PlayerViewModel.PlaybackSnapshot.State == PlaybackState.Paused &&
                  !document.PlayerViewModel.PlaybackSnapshot.IsTransitioning,
            TimeSpan.FromSeconds(3),
            "播放器没有稳定进入暂停状态。");
        // LibVLC 可能在 Pause 返回后补发多条暂停前已排队的位置事件。必须等待位置连续
        // 三次稳定后再采样；只等待固定时长会把解码器或硬件不同造成的队列延迟误判为继续播放。
        var pausedAt = await WaitForStablePausedPositionAsync(document.PlayerViewModel);
        await Task.Delay(500);
        var pauseDrift = Math.Abs(
            document.PlayerViewModel.PlaybackSnapshot.PositionMs - pausedAt);
        _stageDurationsMs["pauseDriftMs"] = pauseDrift;
        Require(
            pauseDrift <= 250,
            "暂停后播放时间仍持续推进。");

        var pausedSeekTarget = document.PlayerViewModel.PlaybackSnapshot.DurationMs / 3;
        var seek = await document.PlayerViewModel.SeekMediaAsync(
            pausedSeekTarget,
            waitForFrame: true);
        Require(seek.Success, $"暂停 Seek 失败: {seek.Failure?.Code}");
        Require(
            Math.Abs(document.PlayerViewModel.PlaybackSnapshot.PositionMs -
                     pausedSeekTarget) <= 750,
            "暂停 Seek 的位置误差超过 750 ms。");

        for (var index = 0; index < options.DockSwitches; index++)
        {
            var expectedPosition =
                document.PlayerViewModel.PlaybackSnapshot.PositionMs;
            documentDock.ActiveDockable = placeholder.Dockable;
            await WaitUntilAsync(
                () => !document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(5),
                "暂停态切出 Dock 后旧 HWND 未销毁。");
            documentDock.ActiveDockable = document.Dockable;
            await WaitUntilAsync(
                () => document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(5),
                "暂停态切回 Dock 后新 HWND 未创建。");
            await WaitUntilAsync(
                () => document.PlayerViewModel.PlaybackSnapshot.State ==
                    PlaybackState.Paused &&
                    !document.PlayerViewModel.PlaybackSnapshot.IsTransitioning,
                TimeSpan.FromSeconds(5),
                "Dock 恢复后没有保持暂停。");
            var restoredPosition =
                document.PlayerViewModel.PlaybackSnapshot.PositionMs;
            Require(
                Math.Abs(restoredPosition - expectedPosition) <= 750,
                $"暂停态 Dock 恢复位置误差超过 750 ms：expected={expectedPosition}, actual={restoredPosition}。");
        }

        var stopHeartbeat = await MeasureUiHeartbeatAsync(
            () => document.PlayerViewModel.StopCommand.ExecuteAsync(null));
        RecordHeartbeat("stop", stopHeartbeat);
        await WaitUntilAsync(
            () => document.PlayerViewModel.PlaybackSnapshot.State ==
                PlaybackState.Stopped,
            TimeSpan.FromSeconds(5),
            "真实媒体停止命令未进入 Stopped。");
        await document.PlayerViewModel.PlayCommand.ExecuteAsync(null);
        for (var index = 0; index < options.DockSwitches; index++)
        {
            var expectedPosition =
                document.PlayerViewModel.PlaybackSnapshot.PositionMs;
            documentDock.ActiveDockable = placeholder.Dockable;
            await WaitUntilAsync(
                () => !document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(5),
                "切出 Dock 后旧 HWND 未销毁。");
            documentDock.ActiveDockable = document.Dockable;
            await WaitUntilAsync(
                () => document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(5),
                "切回 Dock 后新 HWND 未创建。");
            await WaitUntilAsync(
                () => document.PlayerViewModel.PlaybackSnapshot.State == PlaybackState.Playing &&
                      !document.PlayerViewModel.PlaybackSnapshot.IsTransitioning,
                TimeSpan.FromSeconds(5),
                "Dock 恢复后没有继续播放。");
            Require(
                Math.Abs(document.PlayerViewModel.PlaybackSnapshot.PositionMs -
                         expectedPosition) <= 750,
                "播放态 Dock 恢复位置误差超过 750 ms。");
        }

        // 前面的 Dock 压测按设计会反复重建原生表面。普通媒体切换必须与紧邻切换前的
        // 当前代次比较，不能使用全屏退出时记录的历史代次。
        documentSurfaceGeneration =
            document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration;
        _surfaceGenerationBeforeMediaSwitch = documentSurfaceGeneration;
        _surfaceGenerationAfterMediaSwitch = documentSurfaceGeneration;
        if (options.MediaSwitches > 0)
        {
            var initialMediaGeneration =
                document.PlayerViewModel.PlaybackSnapshot.MediaGeneration;
            var switches = Enumerable.Range(0, options.MediaSwitches)
                .Select(index =>
                {
                    var asset = assets[index % assets.Count];
                    return document.PlayerViewModel.LoadMediaAsync(
                        asset.EncryptedPath,
                        Password);
                })
                .ToArray();
            bool[] switchResults = [];
            var switchHeartbeat = await MeasureUiHeartbeatAsync(
                async () => switchResults = await Task.WhenAll(switches));
            RecordHeartbeat("mediaSwitch", switchHeartbeat);
            Require(
                switchResults[^1] && switchResults.Count(result => result) == 1,
                "快速媒体切换没有做到只有最后请求提交。");
            var expectedMediaGeneration =
                initialMediaGeneration + options.MediaSwitches;
            var actualMediaGeneration =
                document.PlayerViewModel.PlaybackSnapshot.MediaGeneration;
            Require(
                actualMediaGeneration == expectedMediaGeneration,
                "快速媒体切换提交代次错误: " +
                $"initial={initialMediaGeneration}, " +
                $"expected={expectedMediaGeneration}, " +
                $"actual={actualMediaGeneration}。");
            Require(
                documentOutputGeneration ==
                document.PlayerViewModel.SurfaceSession?.VideoOutput.Generation,
                "普通媒体切换替换了 Document 级视频输出。");
            Require(
                documentSurfaceGeneration ==
                document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration,
                "普通媒体切换意外重建了 HWND 视频表面。");
            _surfaceGenerationAfterMediaSwitch =
                document.PlayerViewModel.PlaybackSnapshot.SurfaceGeneration;

            await document.PlayerViewModel.PlayCommand.ExecuteAsync(null);
            await WaitUntilAsync(
                () =>
                {
                    var snapshot = document.PlayerViewModel.PlaybackSnapshot;
                    return snapshot.MediaGeneration == expectedMediaGeneration &&
                           snapshot.State == PlaybackState.Playing &&
                           !snapshot.IsTransitioning;
                },
                TimeSpan.FromSeconds(5),
                "快速媒体切换的最终候选无法播放。");
        }

        var webm = assets.Single(asset => asset.FileName.EndsWith(".webm", StringComparison.Ordinal));
        SecurePlaybackDiagnostics.ClearRecentChunkReads();
        Require(
            await document.PlayerViewModel.LoadAndPlayMediaAsync(webm.EncryptedPath, Password),
            "真实 WebM 加载播放失败。");
        await WaitUntilAsync(
            () => document.PlayerViewModel.PlaybackSnapshot.PositionMs > 100,
            TimeSpan.FromSeconds(8),
            "真实 WebM 播放时间未推进。");
        Require(!document.PlayerViewModel.PlaybackSnapshot.HasAudio, "无声 WebM 被错误识别为有音轨。");
        var nearEnd = Math.Max(0, document.PlayerViewModel.PlaybackSnapshot.DurationMs - 500);
        Require(
            (await document.PlayerViewModel.SeekMediaAsync(nearEnd)).Success,
            "接近结尾 Seek 失败。");
        try
        {
            await WaitUntilAsync(
                () => document.PlayerViewModel.PlaybackSnapshot.State == PlaybackState.Ended,
                TimeSpan.FromSeconds(8),
                "接近结尾 Seek 后没有到达媒体结束状态。");
        }
        catch (TimeoutException ex)
        {
            var snapshot = document.PlayerViewModel.PlaybackSnapshot;
            throw new TimeoutException(
                $"{ex.Message} state={snapshot.State}, position={snapshot.PositionMs}, duration={snapshot.DurationMs}");
        }

        _nearEndSeekChunkTrace.AddRange(
            SecurePlaybackDiagnostics.CaptureRecentChunkReads());
        Require(
            _nearEndSeekChunkTrace.Distinct().Count() >= 3,
            "WebM 真实播放与 Seek 没有跨越至少三个 SECVID03 块。");

        await VerifyTamperedSeekAsync(document, webm, nearEnd);
        await VerifyUnavailableInputAsync(document, webm);
        if (!string.IsNullOrWhiteSpace(options.DiagnosticReportPath))
        {
            var diagnostic = await document.PlayerViewModel.CreateDiagnosticJsonAsync();
            var diagnosticPath = Path.GetFullPath(options.DiagnosticReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
            await File.WriteAllBytesAsync(diagnosticPath, diagnostic.ToArray());
        }
        }
        finally
        {
            documentDock.ActiveDockable = placeholder.Dockable;
            await DrainDispatcherAsync();
            workspace.DockFactory.CloseDockable(document.Dockable);
            _closedDocumentReferences.Add(new WeakReference<SecretVideoPlayerViewModel>(document.Model));
            await DrainDispatcherAsync();
        }

        var functionalResources = SecurePlaybackDiagnostics.CaptureResources();
        Require(
            functionalResources == expectedResources,
            $"功能矩阵关闭 Document 后资源未回到占位基线: {functionalResources}。");
    }

    private async Task VerifyLibraryPresentationAsync(
        Window mainWindow,
        DocumentPersistenceCoordinator documentCoordinator,
        WorkspaceSession workspace,
        DocumentDock documentDock,
        HarnessPlayerDocument placeholder)
    {
        var creation = await documentCoordinator.CreateDocumentAsync(
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument);
        Require(
            !creation.ShouldUpdateError || string.IsNullOrEmpty(creation.Error),
            "媒体库 Document 创建协调器返回失败。");
        var libraryDockable = documentDock.VisibleDockables?
            .OfType<ManagedDocumentDockable>()
            .LastOrDefault(item => item.Model is SecretVideoLibraryViewModel) ??
            throw new InvalidOperationException("无法创建加密视频库播放器 Document。");
        var library = new HarnessLibraryDocument(
            libraryDockable,
            (SecretVideoLibraryViewModel)libraryDockable.Model);

        try
        {
            documentDock.ActiveDockable = library.Dockable;
            await WaitUntilAsync(
                () => mainWindow.GetVisualDescendants()
                    .OfType<VideoPlayerControl>()
                    .Any(control => ReferenceEquals(
                        control.DataContext,
                        library.PlayerViewModel)),
                TimeSpan.FromSeconds(8),
                "媒体库播放器视图未附加到宿主视觉树。");
            await DrainDispatcherAsync();

            var playerControl = mainWindow.GetVisualDescendants()
                .OfType<VideoPlayerControl>()
                .Single(control => ReferenceEquals(
                    control.DataContext,
                    library.PlayerViewModel));
            Require(
                ReferenceEquals(playerControl.NavigationContext, library.Model),
                "媒体库导航上下文没有绑定到媒体库 ViewModel。");
            Require(
                playerControl.HasNavigationContext,
                "媒体库播放器没有公开导航控件。");
            Require(
                !library.IsContinuousPlaybackEnabled,
                "媒体库连续播放没有默认关闭。");

            var previous = playerControl.GetVisualDescendants()
                .OfType<Button>()
                .SingleOrDefault(button => string.Equals(
                    Avalonia.Automation.AutomationProperties.GetName(button),
                    "上一项",
                    StringComparison.Ordinal));
            var next = playerControl.GetVisualDescendants()
                .OfType<Button>()
                .SingleOrDefault(button => string.Equals(
                    Avalonia.Automation.AutomationProperties.GetName(button),
                    "下一项",
                    StringComparison.Ordinal));
            var continuous = playerControl.GetVisualDescendants()
                .OfType<CheckBox>()
                .SingleOrDefault(checkBox => Equals(checkBox.Content, "连续播放"));
            Require(
                previous?.IsVisible == true &&
                next?.IsVisible == true &&
                continuous?.IsVisible == true,
                "媒体库上一项、下一项或连续播放控件没有显示。");
        }
        finally
        {
            documentDock.ActiveDockable = placeholder.Dockable;
            await DrainDispatcherAsync();
            workspace.DockFactory.CloseDockable(library.Dockable);
            await DrainDispatcherAsync();
        }

        var singlePlayerControl = mainWindow.GetVisualDescendants()
            .OfType<VideoPlayerControl>()
            .SingleOrDefault(control => ReferenceEquals(
                control.DataContext,
                placeholder.PlayerViewModel));
        Require(
            singlePlayerControl is not null &&
            singlePlayerControl.NavigationContext is null &&
            !singlePlayerControl.HasNavigationContext,
            "单文件播放器错误地显示了媒体库导航能力。");
    }

    private async Task VerifyTamperedSeekAsync(
        HarnessPlayerDocument document,
        HarnessAsset asset,
        long nearEnd)
    {
        await document.PlayerViewModel.CleanupMediaAsync();
        var tamperedPath = $"{asset.EncryptedPath}.tampered";
        File.Copy(asset.EncryptedPath, tamperedPath, overwrite: true);
        var targetChunk = _nearEndSeekChunkTrace.Last();
        FlipSecvid03ChunkByte(tamperedPath, targetChunk);

        var started = await document.PlayerViewModel.LoadAndPlayMediaAsync(
            tamperedPath,
            Password);
        if (started)
        {
            await ObserveUntilAsync(
                () => document.PlayerViewModel.PlaybackSnapshot.State is
                    PlaybackState.Playing or PlaybackState.Faulted,
                TimeSpan.FromSeconds(5));

            if (document.PlayerViewModel.LastFailure?.Code !=
                PlaybackFailureCode.CorruptedContent)
            {
                _ = await document.PlayerViewModel.SeekMediaAsync(
                    nearEnd,
                    waitForFrame: true);
            }
        }

        var corrupted = await ObserveUntilAsync(
            () => document.PlayerViewModel.LastFailure?.Code ==
                PlaybackFailureCode.CorruptedContent,
            TimeSpan.FromSeconds(8));
        Require(corrupted, "篡改块播放未稳定报告 CorruptedContent。");

        await document.PlayerViewModel.CleanupMediaAsync();
        File.Delete(tamperedPath);
    }

    private async Task VerifyUnavailableInputAsync(
        HarnessPlayerDocument document,
        HarnessAsset asset)
    {
        var unavailablePath = $"{asset.EncryptedPath}.unavailable";
        File.Copy(asset.EncryptedPath, unavailablePath, overwrite: true);
        File.Delete(unavailablePath);

        var loaded = await document.PlayerViewModel.LoadMediaAsync(
            unavailablePath,
            Password);
        Require(!loaded, "已删除的扫描结果仍被加载成功。");
        Require(
            document.PlayerViewModel.LastFailure?.Code ==
            PlaybackFailureCode.InputUnavailable,
            "已删除的扫描结果没有报告 InputUnavailable。");
    }

    private static void FlipSecvid03ChunkByte(string path, long chunkIndex)
    {
        const int fixedHeaderSize = 256;
        const int tagSize = 16;
        Span<byte> header = stackalloc byte[fixedHeaderSize];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.ReadExactly(header);
        var encryptedDataOffset = BinaryPrimitives.ReadInt64LittleEndian(
            header.Slice(48, 8));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(
            header.Slice(56, 4));
        var chunkCount =
            (BinaryPrimitives.ReadInt64LittleEndian(header.Slice(40, 8)) +
             chunkSize - 1) / chunkSize;
        if (chunkIndex < 0 || chunkIndex >= chunkCount)
        {
            throw new InvalidDataException("Seek 轨迹包含无效 SECVID03 块编号。");
        }

        var physicalOffset = checked(
            encryptedDataOffset + chunkIndex * (chunkSize + tagSize));
        stream.Position = physicalOffset + 17;
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("无法篡改目标 SECVID03 块。");
        }
        stream.Position--;
        stream.WriteByte((byte)(value ^ 0x01));
        stream.Flush(flushToDisk: true);
    }

    private async Task RunLifecycleStressAsync(
        Window mainWindow,
        DocumentPersistenceCoordinator documentCoordinator,
        WorkspaceSession workspace,
        DocumentDock documentDock,
        HarnessPlayerDocument placeholder,
        IReadOnlyList<HarnessAsset> assets,
        PlaybackResourceSnapshot expectedResources)
    {
        var fullscreenLayer = mainWindow.GetVisualDescendants()
            .OfType<Border>()
            .Single(control => control.Name == "ContentFullscreenLayer");

        for (var cycle = 0; cycle < options.Cycles; cycle++)
        {
            var document = await CreateDocumentAsync(documentCoordinator, documentDock);
            documentDock.ActiveDockable = document.Dockable;
            await WaitUntilAsync(
                () => document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(8),
                $"第 {cycle + 1} 轮 HWND 未创建。");
            // 立即降级为弱引用，避免 async 状态机把最后一轮 View 作为局部变量保活到报告生成。
            // 该引用只用于证明关闭后对象可回收，不参与关闭动作本身。
            var playerViewReference = new WeakReference<SecretVideoPlayerView>(
                mainWindow.GetVisualDescendants()
                    .OfType<SecretVideoPlayerView>()
                    .First(control => ReferenceEquals(
                        control.DataContext,
                        document.Model)));

            var asset = assets[cycle % assets.Count];
            bool loaded;
            try
            {
                // 单轮真实播放可能阻塞在显卡驱动或 LibVLC 原生调用。Harness 的职责是把
                // 这种事实报告为失败，而不是让发布门禁永久等待；20 秒明显高于正常的
                // 本地短媒体启动时间，并且只约束测试等待，不改变产品播放器的超时策略。
                loaded = await document.PlayerViewModel
                    .LoadAndPlayMediaAsync(asset.EncryptedPath, Password)
                    .WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"第 {cycle + 1} 轮加载播放在 20 秒内未返回。",
                    ex);
            }
            Require(
                loaded,
                $"第 {cycle + 1} 轮加载播放失败。");
            await WaitUntilAsync(
                () => document.PlayerViewModel.PlaybackSnapshot.PositionMs > 50,
                TimeSpan.FromSeconds(6),
                $"第 {cycle + 1} 轮没有产生真实读取。");

            // V3 G8 要验证的是“全屏租约仍有效时直接关闭 Document”，而不是先手工退出全屏后
            // 再关闭。View 的 Detached/Dispose 必须释放租约、移回唯一 PlayerShell、销毁 HWND，
            // 最后才由 Document Scope 释放播放器和加密流。
            document.PlayerViewModel.ToggleFullscreenCommand.Execute(null);
            await WaitUntilAsync(
                () => document.PlayerViewModel.IsFullscreen &&
                      !document.PlayerViewModel.IsFullscreenTransitioning &&
                      fullscreenLayer.IsVisible,
                TimeSpan.FromSeconds(8),
                $"第 {cycle + 1} 轮未能进入全屏。");

            workspace.DockFactory.CloseDockable(document.Dockable);
            _closedDocumentReferences.Add(new WeakReference<SecretVideoPlayerViewModel>(document.Model));
            _closedViewReferences.Add(playerViewReference);
            // 关闭动作本身已经在全屏租约有效时完成；随后显式选回占位标签，仅用于让 Dock 的
            // Presenter 丢弃最后一个已关闭 View 的缓存引用。若省略这一步，业务资源虽已归零，
            // 最后一轮 View 仍可能被展示层缓存到下一次选择，弱引用证据会产生假阳性。
            documentDock.ActiveDockable = placeholder.Dockable;
            await WaitUntilAsync(
                () => !fullscreenLayer.IsVisible &&
                      !document.PlayerViewModel.IsFullscreen &&
                      !document.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(8),
                $"第 {cycle + 1} 轮全屏 Document 关闭后视觉或 HWND 未恢复。");
            await DrainDispatcherAsync();

            var cycleResources = SecurePlaybackDiagnostics.CaptureResources();
            Require(
                cycleResources == expectedResources,
                $"第 {cycle + 1} 轮关闭后资源未回到占位基线: {cycleResources}。");
            Require(
                CountUnexpectedTopLevelWindows() == 0,
                $"第 {cycle + 1} 轮检测到意外顶层视频窗口。");
        }
    }

    private static async Task<HarnessPlayerDocument> CreateDocumentAsync(
        DocumentPersistenceCoordinator documentCoordinator,
        DocumentDock documentDock)
    {
        var creation = await documentCoordinator.CreateDocumentAsync(
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument);
        if (creation.ShouldUpdateError && !string.IsNullOrEmpty(creation.Error))
        {
            throw new InvalidOperationException("安全视频 Document 创建协调器返回失败。");
        }
        var dockable = documentDock.VisibleDockables?
            .OfType<ManagedDocumentDockable>()
            .LastOrDefault(item => item.Model is SecretVideoPlayerViewModel) ??
            throw new InvalidOperationException("无法创建安全视频 Document。");
        return new HarnessPlayerDocument(
            dockable,
            (SecretVideoPlayerViewModel)dockable.Model);
    }

    private static async Task<IReadOnlyList<HarnessAsset>> PrepareAssetsAsync(string directory)
    {
        var sourceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "RealMedia");
        var names = new[]
        {
            "synthetic-multitrack-subtitles.mp4",
            "synthetic-av-short.mp4",
            "synthetic-silent-multiblock.webm"
        };
        var encryptor = new Secvid03Encryptor();
        var assets = new List<HarnessAsset>();
        foreach (var name in names)
        {
            var source = Path.Combine(sourceDirectory, name);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("真实媒体测试资产缺失。", name);
            }

            var encrypted = Path.Combine(directory, $"{name}.secvid");
            await encryptor.EncryptAsync(
                source,
                encrypted,
                Password,
                Path.GetFileNameWithoutExtension(name),
                "G3 integration fixture");
            await using var sourceStream = File.OpenRead(source);
            var sourceHash = await SHA256.HashDataAsync(sourceStream);
            assets.Add(new HarnessAsset(
                name,
                source,
                encrypted,
                Convert.ToHexString(sourceHash)));
        }
        return assets;
    }

    private static async Task VerifyRoundTripAndFileLocksAsync(
        IReadOnlyList<HarnessAsset> assets)
    {
        var decryptor = new Secvid03Decryptor();
        foreach (var asset in assets)
        {
            var decrypted = Path.Combine(
                Path.GetDirectoryName(asset.EncryptedPath)!,
                $"roundtrip-{asset.FileName}");
            await decryptor.DecryptAsync(asset.EncryptedPath, decrypted, Password);
            string hash;
            await using (var stream = File.OpenRead(decrypted))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            }
            if (!hash.Equals(asset.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"资产 {asset.FileName} 解密哈希不一致。");
            }
            File.Delete(decrypted);

            var renamed = $"{asset.EncryptedPath}.lock-check";
            File.Move(asset.EncryptedPath, renamed);
            File.Move(renamed, asset.EncryptedPath);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= timeout)
            {
                throw new TimeoutException(failureMessage);
            }
            await Task.Delay(50);
        }
    }

    private static async Task<long> WaitForStablePausedPositionAsync(
        VideoPlayerControlViewModel player)
    {
        var stopwatch = Stopwatch.StartNew();
        var previous = player.PlaybackSnapshot.PositionMs;
        var stableSamples = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100);
            var current = player.PlaybackSnapshot.PositionMs;
            stableSamples = Math.Abs(current - previous) <= 50
                ? stableSamples + 1
                : 0;
            previous = current;
            if (stableSamples >= 3)
                return current;
        }

        throw new InvalidOperationException("播放器暂停后的位置事件未能在三秒内稳定。");
    }

    private static async Task<bool> ObserveUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= timeout)
            {
                return false;
            }
            await Task.Delay(50);
        }
        return true;
    }

    private static async Task DrainDispatcherAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
    }

    /// <summary>
    /// 在真实 Avalonia Dispatcher 上测量操作期间的消息泵心跳。
    /// </summary>
    /// <remarks>
    /// 这里不规定 Stop 或切换必须在固定毫秒内结束；不同机器和编码格式的原生释放
    /// 时间本来就会变化。门禁只验证：当操作不是瞬时完成时，操作尚未结束期间
    /// UI 定时器仍得到调度。若 LibVLC Stop 被错误地放回 UI 线程，心跳会归零。
    /// </remarks>
    private static async Task<UiHeartbeatResult> MeasureUiHeartbeatAsync(Func<Task> operation)
    {
        var clock = Stopwatch.StartNew();
        var previousTick = clock.ElapsedMilliseconds;
        var maximumGap = 0L;
        var activeTicks = 0;
        var operationActive = false;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        EventHandler tick = (_, _) =>
        {
            var now = clock.ElapsedMilliseconds;
            maximumGap = Math.Max(maximumGap, now - previousTick);
            previousTick = now;
            if (operationActive)
            {
                activeTicks++;
            }
        };
        timer.Tick += tick;

        try
        {
            timer.Start();
            await Task.Delay(30);
            var startedAt = clock.ElapsedMilliseconds;
            operationActive = true;
            await operation();
            operationActive = false;
            var elapsed = clock.ElapsedMilliseconds - startedAt;
            await Task.Delay(30);
            return new UiHeartbeatResult(elapsed, activeTicks, maximumGap);
        }
        finally
        {
            timer.Stop();
            timer.Tick -= tick;
        }
    }

    private void RecordHeartbeat(string operation, UiHeartbeatResult heartbeat)
    {
        _stageDurationsMs[$"{operation}UiHeartbeatTicks"] = heartbeat.ActiveTicks;
        _stageDurationsMs[$"{operation}ElapsedMs"] = heartbeat.OperationElapsedMs;
        _stageDurationsMs[$"{operation}UiHeartbeatMaxGapMs"] = heartbeat.MaximumGapMs;
        _uiHeartbeatMaxIntervalMs = Math.Max(
            _uiHeartbeatMaxIntervalMs,
            heartbeat.MaximumGapMs);

        // 小于一个采样周期的操作无需强求 tick；超过采样周期则必须看到活动期心跳。
        Require(
            heartbeat.OperationElapsedMs < 10 || heartbeat.ActiveTicks > 0,
            $"{operation} 执行期间 Avalonia UI heartbeat 停止。");
    }

    private async Task MeasureStageAsync(string name, Func<Task> operation)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            await operation();
        }
        finally
        {
            _stageDurationsMs[name] = elapsed.ElapsedMilliseconds;
        }
    }

    private void Require(bool condition, string failure)
    {
        if (!condition)
        {
            _failures.Add(failure);
        }
    }

    private HarnessReport BuildReport(
        bool success,
        IReadOnlyList<HarnessAsset> assets,
        PlaybackResourceSnapshot resources) =>
        new(
            options.Suite == HarnessSuite.G10 ? 1 : 3,
            options.Suite == HarnessSuite.G10
                ? "g10-playback-resource-trend"
                : "g3-playback-integration",
            success,
            DateTimeOffset.UtcNow,
            _elapsed.ElapsedMilliseconds,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(ISecureVideoPlaybackSession).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(Dock.Model.Core.IDockable).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(LibVLCSharp.Shared.LibVLC).Assembly.GetName().Version?.ToString() ?? "unknown",
            GetNativeLibVlcVersion(),
            options.Cycles,
            options.DockSwitches,
            options.MediaSwitches,
            assets.Select(asset => new HarnessAssetReport(
                asset.FileName,
                asset.Sha256)).ToArray(),
            resources,
            new Dictionary<string, long>(_stageDurationsMs),
            _uiHeartbeatMaxIntervalMs,
            _randomSeekTargets.ToArray(),
            _nearEndSeekChunkTrace.ToArray(),
            _nativeOutputGeneration,
            _surfaceGenerationBeforeMediaSwitch,
            _surfaceGenerationAfterMediaSwitch,
            _closedDocumentReferences.Count(reference =>
                reference.TryGetTarget(out _)),
            _closedViewReferences.Count(reference =>
                reference.TryGetTarget(out _)),
            SecurePlaybackDiagnostics.CaptureRetainedEncryptedStreamCount(),
            _processBaseline,
            _processFinal,
            _failures.ToArray());

    private static async Task StabilizeProcessAsync()
    {
        // 播放缓存使用 1 MiB LOH 数组。资源已经释放后，LOH 的空闲段仍可能保留在堆中；
        // 验收终点压缩一次 LOH，区分真实存活对象与 GC 为后续分配保留的空容量。
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(250);
        // Avalonia 合成器会在前两次回收之后的下一帧才丢弃已经呈现过的控件树；等待这一帧后
        // 再回收一次，弱引用统计才不会把合成器的短暂帧引用误判为 Dock/View 泄漏。
        await DrainDispatcherAsync();
        GC.Collect();
    }

    private static string GetNativeLibVlcVersion()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "native",
            "win-x64",
            "libvlc",
            "libvlc.dll");
        if (!File.Exists(path))
        {
            return "unavailable";
        }

        return FileVersionInfo.GetVersionInfo(path).ProductVersion ??
               FileVersionInfo.GetVersionInfo(path).FileVersion ??
               "unknown";
    }

    private async Task WriteReportAsync(HarnessReport report)
    {
        var path = Path.GetFullPath(options.ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine($"G3 report: {path}");
    }

    private static int CountUnexpectedTopLevelWindows()
    {
        var processId = (uint)Environment.ProcessId;
        var mainHandle = (Application.Current?.ApplicationLifetime as
            IClassicDesktopStyleApplicationLifetime)?.MainWindow?
            .TryGetPlatformHandle()?.Handle ?? nint.Zero;
        var unexpected = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner == processId &&
                handle != mainHandle &&
                IsWindowVisible(handle))
            {
                unexpected++;
            }
            return true;
        }, nint.Zero);
        return unexpected;
    }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
}

internal sealed record HarnessAsset(
    string FileName,
    string SourcePath,
    string EncryptedPath,
    string Sha256);

internal sealed record HarnessAssetReport(string FileName, string Sha256);

internal sealed record HarnessReport(
    int SchemaVersion,
    string Kind,
    bool Success,
    DateTimeOffset ExecutedAtUtc,
    long ElapsedMs,
    string OperatingSystem,
    string Architecture,
    string DotNetVersion,
    string HostAssemblyVersion,
    string VideoSecurityPlayerAssemblyVersion,
    string AvaloniaVersion,
    string DockVersion,
    string LibVlcSharpVersion,
    string NativeLibVlcVersion,
    int Cycles,
    int DockSwitches,
    int MediaSwitches,
    IReadOnlyList<HarnessAssetReport> Assets,
    PlaybackResourceSnapshot FinalResources,
    IReadOnlyDictionary<string, long> StageDurationsMs,
    long UiHeartbeatMaxIntervalMs,
    IReadOnlyList<long> RandomSeekTargets,
    IReadOnlyList<long> NearEndSeekChunkTrace,
    long NativeOutputGeneration,
    long SurfaceGenerationBeforeMediaSwitch,
    long SurfaceGenerationAfterMediaSwitch,
    int AliveClosedDocuments,
    int AliveClosedViews,
    int AliveDisposedEncryptedStreams,
    ProcessResourceSnapshot? ProcessBaseline,
    ProcessResourceSnapshot? ProcessFinal,
    IReadOnlyList<string> Failures);

internal sealed record ProcessResourceSnapshot(
    long ManagedHeapBytes,
    long LohSizeBytes,
    long LohFragmentationBytes,
    long PinnedObjectHeapSizeBytes,
    long PinnedObjectHeapFragmentationBytes,
    long PinnedObjectsCount,
    long HeapSizeBytes,
    long FragmentedBytes,
    long TotalCommittedBytes,
    long WorkingSetBytes,
    long PrivateBytes,
    int HandleCount,
    int ThreadCount,
    long PendingThreadPoolItems)
{
    public static ProcessResourceSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var memory = GC.GetGCMemoryInfo();
        var generations = memory.GenerationInfo;
        var loh = generations.Length > 3 ? generations[3] : default;
        var poh = generations.Length > 4 ? generations[4] : default;
        return new ProcessResourceSnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            loh.SizeAfterBytes,
            loh.FragmentationAfterBytes,
            poh.SizeAfterBytes,
            poh.FragmentationAfterBytes,
            memory.PinnedObjectsCount,
            memory.HeapSizeBytes,
            memory.FragmentedBytes,
            memory.TotalCommittedBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count,
            ThreadPool.PendingWorkItemCount);
    }
}

internal readonly record struct UiHeartbeatResult(
    long OperationElapsedMs,
    int ActiveTicks,
    long MaximumGapMs);

internal sealed record HarnessOptions(
    HarnessSuite Suite,
    int Cycles,
    int DockSwitches,
    int MediaSwitches,
    int QueueItems,
    int LibraryItems,
    string ReportPath,
    string? DiagnosticReportPath)
{
    public static HarnessOptions Parse(string[] args)
    {
        var suiteText = ReadString(args, "--suite", "g3");
        var suite = suiteText.ToLowerInvariant() switch
        {
            "g3" => HarnessSuite.G3,
            "g8" => HarnessSuite.G8,
            "g10" => HarnessSuite.G10,
            "phase4" => HarnessSuite.Phase4,
            _ => throw new ArgumentException("--suite 只支持 g3、g8、g10 或 phase4。")
        };
        var cycles = ReadInt(args, "--cycles", 100);
        var dockSwitches = ReadInt(args, "--dock-switches", 20);
        var mediaSwitches = ReadInt(args, "--media-switches", 30);
        var queueItems = ReadPositiveInt(args, "--queue-items", 100);
        var libraryItems = ReadPositiveInt(args, "--library-items", 1000);
        var report = ReadString(
            args,
            "--report",
            suite switch
            {
                HarnessSuite.G3 =>
                    Path.Combine("TestResults", "G3", "g3-playback-windows-x64.json"),
                HarnessSuite.G8 =>
                    Path.Combine("TestResults", "G8", "g8-p1-windows-x64.json"),
                HarnessSuite.Phase4 =>
                    Path.Combine("TestResults", "Phase4", "avalonia12-libvlcsharp.json"),
                _ => Path.Combine("TestResults", "G10", "g10-playback-resource-trend.json")
            });
        var diagnosticReport = ReadString(args, "--diagnostic-report", string.Empty);
        return new HarnessOptions(
            suite,
            cycles,
            dockSwitches,
            mediaSwitches,
            queueItems,
            libraryItems,
            report,
            string.IsNullOrWhiteSpace(diagnosticReport) ? null : diagnosticReport);
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var value = ReadString(args, name, fallback.ToString());
        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"{name} 必须是非负整数。");
        }
        return parsed;
    }

    private static int ReadPositiveInt(string[] args, string name, int fallback)
    {
        var parsed = ReadInt(args, name, fallback);
        if (parsed == 0)
            throw new ArgumentException($"{name} 必须是正整数。");
        return parsed;
    }

    private static string ReadString(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
}

internal enum HarnessSuite
{
    G3,
    G8,
    G10,
    Phase4
}
