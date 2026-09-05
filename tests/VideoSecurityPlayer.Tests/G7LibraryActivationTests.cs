using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// 锁定媒体库“加载暂停”和“激活播放”两个用户意图，防止后续界面调整再次把它们合并。
/// </summary>
public sealed class G7LibraryActivationTests
{
    [Fact]
    public async Task LoadButtonPausesButActivationRestoresAndStartsPlayback()
    {
        using var fixture = new LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SelectedItem = Assert.Single(fixture.Browser.VisibleItems);
        fixture.Library.Password = "password";

        await fixture.Library.PlaySelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Session.LoadAtPositionCalls);
        Assert.Equal(0, fixture.Session.LoadAtPositionAndPlayCalls);
        Assert.Equal(3_200, fixture.Session.LastRequestedPositionMs);
        Assert.Equal(PlaybackState.Ready, fixture.Session.Snapshot.State);

        await fixture.Library.ActivateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Session.LoadAtPositionCalls);
        Assert.Equal(1, fixture.Session.LoadAtPositionAndPlayCalls);
        Assert.Equal(3_200, fixture.Session.LastRequestedPositionMs);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
        Assert.Contains("上次位置继续播放", fixture.Library.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivationWithoutPasswordDoesNotReachPlaybackSession()
    {
        using var fixture = new LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SelectedItem = Assert.Single(fixture.Browser.VisibleItems);

        await fixture.Library.ActivateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Session.LoadAtPositionCalls);
        Assert.Equal(0, fixture.Session.LoadAtPositionAndPlayCalls);
        Assert.Equal("请输入公共密码", fixture.Library.StatusMessage);
    }

    [Fact]
    public async Task 播放进度视图适配器把按下与释放转交给既有命令()
    {
        using var fixture = new LibraryFixture();

        PlaybackTransportView.ExecuteStartSliderDrag(fixture.Player.Transport);

        Assert.True(fixture.Player.IsSliderBeingDragged);

        PlaybackTransportView.ExecuteEndSliderDrag(fixture.Player.Transport);
        await (fixture.Player.EndSliderDragCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.False(fixture.Player.IsSliderBeingDragged);
    }

    private sealed class LibraryFixture : IDisposable
    {
        private const string FileId = "00112233445566778899AABBCCDDEEFF";
        private const long OriginalLength = 12_345;
        private readonly TestHistoryStore _history;
        private readonly TestDocumentLifetime _lifetime = new();

        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), $"videosecurityplayer-g7-activation-{Guid.NewGuid():N}");
        public string FilePath { get; }
        public RecordingSession Session { get; } = new();
        public VideoLibraryBrowserViewModel Browser { get; }
        public VideoPlayerControlViewModel Player { get; }
        public SecretVideoLibraryViewModel Library { get; }

        public LibraryFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "sample.secvid");
            File.WriteAllBytes(FilePath, [1, 2, 3, 4]);
            _history = new TestHistoryStore(new VideoPlaybackHistoryEntry(
                FilePath,
                FileId,
                OriginalLength,
                3_200,
                10_000,
                DateTimeOffset.UtcNow,
                IsCompleted: false));
            var scanResult = new VideoLibraryScanResult(
                FilePath,
                "sample",
                "示例视频",
                string.Empty,
                VideoLibraryMetadataState.Ready,
                string.Empty,
                DateTimeOffset.UtcNow,
                4,
                OriginalLength,
                FileId);
            Browser = new VideoLibraryBrowserViewModel(
                new FixedScanner(scanResult),
                _lifetime,
                historyStore: _history,
                catalog: new SnapshotCatalog(scanResult));
            Player = new VideoPlayerControlViewModel(
                Session,
                Session,
                new ReadyDeploymentProbe(),
                new NoopBackendInitializer());
            Library = new SecretVideoLibraryViewModel(
                Browser,
                Player,
                _lifetime,
                historyStore: _history);
        }

        public void Dispose()
        {
            Library.Dispose();
            Player.Dispose();
            Browser.Dispose();
            _lifetime.Dispose();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // FileSystemWatcher 的关闭通知可能尚在系统队列中；测试清理失败不影响断言。
            }
        }
    }

    private sealed class FixedScanner(VideoLibraryScanResult item) : IVideoLibraryScanner
    {
        public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class SnapshotCatalog(VideoLibraryScanResult item)
        : IVideoLibraryCatalogSession
    {
        public async IAsyncEnumerable<VideoLibraryCatalogBatch> ObserveAsync(
            string folderPath,
            VideoLibraryScanOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VideoLibraryCatalogBatch(
                [item],
                Array.Empty<string>(),
                ReplaceAll: true,
                IsScanning: false,
                StatusMessage: "已加载 1 个视频");
            await Task.Yield();
        }
    }

    private sealed class TestHistoryStore(VideoPlaybackHistoryEntry entry)
        : IPlaybackHistoryStore
    {
        public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;

        public VideoPlaybackHistoryEntry? Find(
            string filePath,
            string fileId,
            long originalFileLength) =>
            string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.FileId, fileId, StringComparison.OrdinalIgnoreCase) &&
            entry.OriginalFileLength == originalFileLength
                ? entry
                : null;

        public IReadOnlyList<VideoPlaybackHistoryEntry> GetAll() => [entry];
        public void Upsert(VideoPlaybackHistoryEntry value) { }
        public void Remove(string filePath, string fileId, long originalFileLength) { }
        public void Clear() =>
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(PlaybackHistoryChangeKind.Cleared));
    }

    private sealed class RecordingSession :
        ISecureVideoPlaybackSession,
        IPlaybackSurfaceSession,
        IPlaybackVideoOutput
    {
        public event EventHandler<PlaybackChangedEventArgs>? Changed;
        public event EventHandler? OutputChanged
        {
            add { }
            remove { }
        }
        public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Empty;
        public IPlaybackVideoOutput VideoOutput => this;
        public long Generation => 0;
        public int LoadAtPositionCalls { get; private set; }
        public int LoadAtPositionAndPlayCalls { get; private set; }
        public long LastRequestedPositionMs { get; private set; }

        public Task<PlaybackOperationResult> LoadAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Ready, positionMs: 0);

        public Task<PlaybackOperationResult> LoadAtPositionAsync(
            string filePath,
            string password,
            long positionMs,
            PlaybackMediaIdentity? expectedIdentity = null,
            CancellationToken cancellationToken = default)
        {
            LoadAtPositionCalls++;
            LastRequestedPositionMs = positionMs;
            return Complete(PlaybackState.Ready, positionMs, expectedIdentity);
        }

        public Task<PlaybackOperationResult> LoadAtPositionAndPlayAsync(
            string filePath,
            string password,
            long positionMs,
            PlaybackMediaIdentity? expectedIdentity = null,
            CancellationToken cancellationToken = default)
        {
            LoadAtPositionAndPlayCalls++;
            LastRequestedPositionMs = positionMs;
            return Complete(PlaybackState.Playing, positionMs, expectedIdentity);
        }

        public Task<PlaybackOperationResult> LoadAndPlayAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Playing, positionMs: 0);

        public Task<PlaybackOperationResult> PlayAsync(
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Playing, Snapshot.PositionMs, Snapshot.MediaIdentity);

        public Task<PlaybackOperationResult> PauseAsync(
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Paused, Snapshot.PositionMs, Snapshot.MediaIdentity);

        public Task<PlaybackOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Stopped, positionMs: 0, Snapshot.MediaIdentity);

        public Task<PlaybackOperationResult> SeekAsync(
            long positionMs,
            bool waitForFrame = false,
            CancellationToken cancellationToken = default) =>
            Complete(Snapshot.State, positionMs, Snapshot.MediaIdentity);

        public Task<PlaybackOperationResult> SeekRelativeAsync(
            long deltaMs,
            CancellationToken cancellationToken = default) =>
            Complete(
                Snapshot.State,
                Math.Max(0, Snapshot.PositionMs + deltaMs),
                Snapshot.MediaIdentity);

        public Task<PlaybackOperationResult> SetRateAsync(
            float rate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> SelectAudioTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> SelectSubtitleTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> ReleaseAsync(
            CancellationToken cancellationToken = default) =>
            Complete(PlaybackState.Empty, positionMs: 0);

        public bool SetVolume(int volume) => true;
        public void DetachSurface(VideoSurfaceIdentity surface) { }

        public Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(
            VideoSurfaceIdentity surface,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public void Dispose()
        {
            Changed = null;
        }

        private Task<PlaybackOperationResult> Complete(
            PlaybackState state,
            long positionMs,
            PlaybackMediaIdentity? identity = null)
        {
            Snapshot = PlaybackSnapshot.Empty with
            {
                MediaGeneration = 1,
                State = state,
                PositionMs = positionMs,
                DurationMs = 10_000,
                IsSeekable = true,
                HasMedia = state != PlaybackState.Empty,
                MediaIdentity = identity
            };
            Changed?.Invoke(this, new PlaybackChangedEventArgs(Snapshot));
            return Task.FromResult(PlaybackOperationResult.Succeeded());
        }
    }

    private sealed class ReadyDeploymentProbe : IPlaybackPlatformStatus
    {
        public PlaybackPlatformCapabilities Capabilities { get; } = new(
            "windows-x64",
            IsSupported: true,
            SupportsNativeVideoOutput: true,
            SupportsEmbeddedFullscreen: true,
            SupportsAudioTrackSelection: true,
            SupportsSubtitleTrackSelection: true,
            UsesBundledRuntime: true,
            UnsupportedReason: null);

        public DeploymentCheckResult Check() =>
            new(string.Empty, string.Empty, Array.Empty<DeploymentIssue>());
    }

    private sealed class NoopBackendInitializer : IPlaybackBackendInitializer
    {
        public void Initialize() { }
    }
}
