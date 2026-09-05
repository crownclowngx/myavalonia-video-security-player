using System.Runtime.CompilerServices;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class VideoToolStabilityTests
{
    [Fact]
    public async Task PlayerAndLibraryDocuments_UseDefaultAndCustomPresentationTitles()
    {
        var player = Assert.IsType<VideoPlayerControlViewModel>(
            RuntimeHelpers.GetUninitializedObject(typeof(VideoPlayerControlViewModel)));
        using var lifetime = new TestDocumentLifetime();
        using var single = new SecretVideoPlayerViewModel(player, lifetime);
        using var browser = new VideoLibraryBrowserViewModel(new EmptyScanner(), lifetime);
        using var library = new SecretVideoLibraryViewModel(browser, player, lifetime);

        await single.InitializeAsync(new NewDocumentActivation(string.Empty), default);
        await library.InitializeAsync(new NewDocumentActivation("媒体库自定义标题"), default);

        Assert.Equal("加密视频播放器", single.Presentation.Title);
        Assert.Equal("媒体库自定义标题", library.Presentation.Title);
    }

    [Fact]
    public void VideoDocuments_TogglePasswordVisibility()
    {
        var player = RuntimeHelpers.GetUninitializedObject(typeof(VideoPlayerControlViewModel))
            as VideoPlayerControlViewModel;
        Assert.NotNull(player);

        using var lifetime = new TestDocumentLifetime();
        var singlePlayer = new SecretVideoPlayerViewModel(player, lifetime);
        Assert.False(singlePlayer.ShowPassword);
        singlePlayer.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(singlePlayer.ShowPassword);

        using var browser = new VideoLibraryBrowserViewModel(new EmptyScanner(), lifetime);
        using var library = new SecretVideoLibraryViewModel(browser, player, lifetime);
        Assert.False(library.ShowPassword);
        library.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(library.ShowPassword);
    }

    [Fact]
    public void LibrarySettingsExpansionPersistsWithoutExposingPasswordOrLosingOtherSettings()
    {
        var player = Assert.IsType<VideoPlayerControlViewModel>(
            RuntimeHelpers.GetUninitializedObject(typeof(VideoPlayerControlViewModel)));
        var initial = VideoLibrarySettings.Default with
        {
            RecentFolder = Path.GetFullPath("remembered-library"),
            IncludeSubdirectories = true,
            SortField = VideoLibrarySortField.ModifiedTime,
            SortDirection = VideoLibrarySortDirection.Descending,
            StatusFilter = VideoLibraryStatusFilter.InProgress
        };
        var settings = new RecordingLibrarySettingsStore(initial);
        using var lifetime = new TestDocumentLifetime();
        using var browser = new VideoLibraryBrowserViewModel(
            new EmptyScanner(),
            lifetime,
            settingsStore: settings);
        using var library = new SecretVideoLibraryViewModel(
            browser,
            player,
            lifetime,
            settingsStore: settings);

        Assert.False(library.IsLibrarySettingsExpanded);
        Assert.Equal("密码未输入", library.PasswordStateText);

        library.Password = "must-not-appear-in-summary";
        library.IsLibrarySettingsExpanded = true;

        Assert.Equal("密码已输入", library.PasswordStateText);
        Assert.DoesNotContain(
            "must-not-appear",
            library.PasswordStateText,
            StringComparison.Ordinal);
        Assert.True(settings.CurrentSettings.IsLibrarySettingsExpanded);
        Assert.Equal(initial.RecentFolder, settings.CurrentSettings.RecentFolder);
        Assert.True(settings.CurrentSettings.IncludeSubdirectories);
        Assert.Equal(initial.SortField, settings.CurrentSettings.SortField);
        Assert.Equal(initial.SortDirection, settings.CurrentSettings.SortDirection);
        Assert.Equal(initial.StatusFilter, settings.CurrentSettings.StatusFilter);
    }

    [Fact]
    public async Task VideoEncryptorDocument_DefaultTitleAndVideoTitle_AreIndependent()
    {
        using var lifetime = new TestDocumentLifetime();
        using var viewModel = TestViewModelFactory.CreateEncryptor(
            new VideoEncryptorService(new Secvid03Encryptor()), lifetime);
        await viewModel.InitializeAsync(new NewDocumentActivation(string.Empty), default);

        Assert.Equal("视频文件加密器", viewModel.Presentation.Title);
        Assert.Empty(viewModel.VideoTitle);

        viewModel.VideoTitle = "容器内公开标题";
        Assert.Equal("视频文件加密器", viewModel.Presentation.Title);

        viewModel.ClearAllCommand.Execute(null);
        Assert.Empty(viewModel.VideoTitle);
        Assert.Equal("视频文件加密器", viewModel.Presentation.Title);
    }

    [Fact]
    public async Task VideoEncryptorDocument_PreservesCustomDocumentTitle()
    {
        using var lifetime = new TestDocumentLifetime();
        using var document = TestViewModelFactory.CreateEncryptor(
            new VideoEncryptorService(new Secvid03Encryptor()), lifetime);
        await document.InitializeAsync(
            new NewDocumentActivation("自定义加密任务"), default);
        Assert.Equal("自定义加密任务", document.Presentation.Title);
        Assert.Empty(document.VideoTitle);
    }

    [Fact]
    public void SurfaceRecoveryPolicy_RecordsPlayingStateAndConsumesOnlyOnce()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        var request = policy.OnSurfaceLost(
            mediaGeneration: 3,
            positionMs: 12_345,
            hasMedia: true,
            isPlaying: true,
            isPaused: false);

        Assert.NotNull(request);
        Assert.Equal(3, request.Value.MediaGeneration);
        Assert.Equal(12_345, request.Value.PositionMs);
        Assert.Equal(VideoSurfacePlaybackMode.Playing, request.Value.PlaybackMode);
        Assert.True(policy.HasPendingRecovery);

        Assert.Equal(request, policy.ConsumeRecovery(mediaGeneration: 3));
        Assert.False(policy.HasPendingRecovery);
        Assert.Null(policy.ConsumeRecovery(mediaGeneration: 3));
    }

    [Fact]
    public void SurfaceRecoveryPolicy_RecordsPausedStateForFrameRestoration()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        var request = policy.OnSurfaceLost(
            mediaGeneration: 8,
            positionMs: 4_200,
            hasMedia: true,
            isPlaying: false,
            isPaused: true);

        Assert.NotNull(request);
        Assert.Equal(VideoSurfacePlaybackMode.Paused, request.Value.PlaybackMode);
        Assert.Equal(4_200, request.Value.PositionMs);
    }

    [Fact]
    public void SurfaceRecoveryPolicy_InternalStopPreservesRequest_ButUserStopCancelsIt()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        policy.OnSurfaceLost(1, 100, hasMedia: true, isPlaying: true, isPaused: false);
        policy.OnPlaybackStopped(isSurfaceTransition: true);
        Assert.True(policy.HasPendingRecovery);

        policy.OnPlaybackStopped(isSurfaceTransition: false);
        Assert.False(policy.HasPendingRecovery);
    }

    [Fact]
    public void SurfaceRecoveryPolicy_MediaGenerationRejectsStaleRequest()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        policy.OnSurfaceLost(4, 100, hasMedia: true, isPlaying: true, isPaused: false);

        Assert.Null(policy.ConsumeRecovery(mediaGeneration: 5));
        Assert.False(policy.HasPendingRecovery);
    }

    [Fact]
    public void SurfaceRecoveryPolicy_RapidSurfaceLossKeepsOnlyLatestSnapshot()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        var first = policy.OnSurfaceLost(7, 100, hasMedia: true, isPlaying: true, isPaused: false);
        var second = policy.OnSurfaceLost(7, 900, hasMedia: true, isPlaying: false, isPaused: true);
        var consumed = policy.ConsumeRecovery(7);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.Value.RequestId > first.Value.RequestId);
        Assert.Equal(second, consumed);
        Assert.Null(policy.ConsumeRecovery(7));
    }

    [Fact]
    public void SurfaceRecoveryPolicy_MissingMediaOrStoppedStateDoesNotRecover()
    {
        var policy = new VideoSurfaceRecoveryPolicy();

        Assert.Null(policy.OnSurfaceLost(1, 0, hasMedia: false, isPlaying: true, isPaused: false));
        Assert.Null(policy.OnSurfaceLost(1, 0, hasMedia: true, isPlaying: false, isPaused: false));
        Assert.False(policy.HasPendingRecovery);

        policy.OnSurfaceLost(1, 0, hasMedia: true, isPlaying: true, isPaused: false);
        policy.Cancel();
        Assert.Null(policy.ConsumeRecovery(1));
    }

    [Fact]
    public async Task SurfaceRestoreSequence_PausedModeWaitsForFrameBeforePausing()
    {
        var operations = new RecordingSurfaceRestoreOperations(length: 10_000);

        var restored = await VideoSurfaceRestoreSequence.ExecuteAsync(
            operations,
            positionMs: 4_200,
            restorePaused: true,
            CancellationToken.None);

        Assert.True(restored);
        Assert.Equal(
            ["Play", "WaitForVideoOutput", "Seek:4200", "WaitForFrame", "Pause"],
            operations.Calls);
    }

    [Fact]
    public async Task SurfaceRestoreSequence_PlayingModeKeepsPlayingAfterSeek()
    {
        var operations = new RecordingSurfaceRestoreOperations(length: 10_000);

        var restored = await VideoSurfaceRestoreSequence.ExecuteAsync(
            operations,
            positionMs: 9_999,
            restorePaused: false,
            CancellationToken.None);

        Assert.True(restored);
        // 位置会避开媒体末尾，防止刚恢复就触发 EndReached。
        Assert.Equal(
            ["Play", "WaitForVideoOutput", "Seek:9750", "WaitForFrame"],
            operations.Calls);
    }

    [Fact]
    public async Task VideoEncryptorDocument_DisposeCancelsEncryptionAndRemovesPartialFile()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-DI-Cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "source.mp4");
        var outputPath = Path.Combine(temporaryDirectory, "output.secvid");

        try
        {
            // 使用稀疏文件保证加密操作不会在 Dispose 发出取消前完成，同时避免测试占用大量磁盘空间。
            using (var input = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.SetLength(64L * 1024 * 1024);
            }

            var service = new VideoEncryptorService(new Secvid03Encryptor());
            using var lifetime = new TestDocumentLifetime();
            using var viewModel = TestViewModelFactory.CreateEncryptor(service, lifetime);
            viewModel.SelectedFilePath = inputPath;
            viewModel.OutputFilePath = outputPath;
            viewModel.Password = "123456";
            viewModel.ConfirmPassword = "123456";

            var encryption = viewModel.StartEncryptionCommand.ExecuteAsync(null);
            lifetime.Close();
            await encryption;

            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.partial-*"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VideoEncryptorDocument_ExplicitCancelIsOnlyAvailableWhileRunning()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-Explicit-Cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "source.mp4");
        var outputPath = Path.Combine(temporaryDirectory, "output.secvid");

        try
        {
            using (var input = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.SetLength(64L * 1024 * 1024);

            using var lifetime = new TestDocumentLifetime();
            using var viewModel = TestViewModelFactory.CreateEncryptor(
                new VideoEncryptorService(new Secvid03Encryptor()), lifetime);
            viewModel.SelectedFilePath = inputPath;
            viewModel.OutputFilePath = outputPath;
            viewModel.Password = "123456";
            viewModel.ConfirmPassword = "123456";

            Assert.False(viewModel.CancelEncryptionCommand.CanExecute(null));
            var encryption = viewModel.StartEncryptionCommand.ExecuteAsync(null);
            Assert.True(viewModel.CancelEncryptionCommand.CanExecute(null));

            viewModel.CancelEncryptionCommand.Execute(null);
            await encryption;

            Assert.False(viewModel.IsEncrypting);
            Assert.False(viewModel.CancelEncryptionCommand.CanExecute(null));
            Assert.Contains("加密已取消", viewModel.StatusMessage);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.partial-*"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private sealed class RecordingSurfaceRestoreOperations(long length) : IVideoSurfaceRestoreOperations
    {
        public List<string> Calls { get; } = [];
        public long Length { get; } = length;

        public bool Play()
        {
            Calls.Add("Play");
            return true;
        }

        public Task WaitForVideoOutputAsync(CancellationToken cancellationToken)
        {
            Calls.Add("WaitForVideoOutput");
            return Task.CompletedTask;
        }

        public Task SeekAsync(long positionMs, bool waitForFrame, CancellationToken cancellationToken)
        {
            Calls.Add($"Seek:{positionMs}");
            if (waitForFrame)
            {
                Calls.Add("WaitForFrame");
            }
            return Task.CompletedTask;
        }

        public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken)
        {
            Calls.Add("Pause");
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyScanner : IVideoLibraryScanner
    {
        public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingLibrarySettingsStore(VideoLibrarySettings initial)
        : IVideoLibrarySettingsStore
    {
        public VideoLibrarySettings CurrentSettings { get; private set; } = initial;

        public void UpdateSettings(VideoLibrarySettings settings) =>
            CurrentSettings = settings;
    }

}
