using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class G4DeploymentTests
{
    [Fact]
    public void R1部署与诊断兼容属性转发唯一状态并在关闭时退订()
    {
        using var session = new EmptySession();
        using var vm = new VideoPlayerControlViewModel(session, session,
            new MutableProbe(ReadyResult()), new CountingInitializer());
        var properties = new List<string?>();
        var changingProperties = new List<string?>();
        var commandNotifications = 0;
        vm.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        vm.PropertyChanging += (_, e) => changingProperties.Add(e.PropertyName);
        vm.PlayCommand.CanExecuteChanged += (_, _) => commandNotifications++;
        vm.Deployment.IsPlaybackAvailable = false;
        Assert.False(vm.IsPlaybackAvailable);
        Assert.Equal(1, properties.Count(x => x == nameof(vm.IsPlaybackAvailable)));
        Assert.Equal(1, changingProperties.Count(x => x == nameof(vm.IsPlaybackAvailable)));
        Assert.Equal(1, commandNotifications);
        vm.DeploymentIssueText = "部署提示";
        vm.DeploymentCheckedPath = "native";
        vm.DeploymentSuggestedAction = "重检";
        vm.IsExportingDiagnostics = true;
        vm.DiagnosticsStatusMessage = "导出提示";
        Assert.Equal(vm.DeploymentIssueText, vm.Deployment.DeploymentIssueText);
        Assert.Equal(vm.DeploymentCheckedPath, vm.Deployment.DeploymentCheckedPath);
        Assert.Equal(vm.DeploymentSuggestedAction, vm.Deployment.DeploymentSuggestedAction);
        Assert.True(vm.Diagnostics.IsExportingDiagnostics);
        Assert.Equal(vm.DiagnosticsStatusMessage, vm.Diagnostics.DiagnosticsStatusMessage);
        vm.Deployment.RetryDeploymentCheckCommand.Execute(null);
        Assert.True(vm.IsPlaybackAvailable);
        Assert.Equal("播放器部署自检通过", vm.StatusMessage);
        vm.Dispose();
        properties.Clear();
        changingProperties.Clear();
        vm.Deployment.DeploymentIssueText = "迟到变更";
        vm.Diagnostics.DiagnosticsStatusMessage = "迟到保存";
        Assert.Empty(properties);
        Assert.Empty(changingProperties);
    }

    [Fact]
    public void CompletePluginDeployment_Passes()
    {
        var result = new PlaybackDeploymentProbe().Check();

        Assert.True(result.IsReady, string.Join(Environment.NewLine, result.Issues));
        Assert.EndsWith(
            Path.Combine("native", "win-x64", "libvlc"),
            result.RuntimeDirectory);
    }

    [Fact]
    public void 阶段3托管桥接与私有原生运行时版本匹配()
    {
        var result = new PlaybackDeploymentProbe().Check();

        Assert.True(result.IsReady, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(new Version(3, 10, 0, 0), typeof(LibVLC).Assembly.GetName().Version);
        Assert.Equal(new Version(3, 10, 0, 0), typeof(VideoView).Assembly.GetName().Version);

        AssertNativeVersion(Path.Combine(result.RuntimeDirectory, "libvlc.dll"));
        AssertNativeVersion(Path.Combine(result.RuntimeDirectory, "libvlccore.dll"));
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(result.RuntimeDirectory, "plugins"),
            "*",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("LibVLCSharp.dll")]
    [InlineData("LibVLCSharp.Avalonia.dll")]
    public void MissingManagedBridge_IsClassified(string fileName)
    {
        using var fixture = DeploymentFixture.Create();
        File.Delete(Path.Combine(fixture.Root, fileName));

        var result = fixture.Probe.Check();

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DeploymentIssueCode.ManagedBridgeMissing &&
                     issue.CheckedPath.EndsWith(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidManagedBridge_IsClassified()
    {
        using var fixture = DeploymentFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "LibVLCSharp.dll"), "not an assembly");

        var result = fixture.Probe.Check();

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DeploymentIssueCode.ManagedBridgeInvalid);
    }

    [Theory]
    [InlineData("libvlc.dll")]
    [InlineData("libvlccore.dll")]
    public void MissingNativeLibrary_IsClassified(string fileName)
    {
        using var fixture = DeploymentFixture.Create();
        File.Delete(Path.Combine(fixture.Runtime, fileName));

        var result = fixture.Probe.Check();

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DeploymentIssueCode.NativeLibraryMissing &&
                     issue.CheckedPath.EndsWith(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public void NonAmd64NativeLibrary_IsRejected()
    {
        using var fixture = DeploymentFixture.Create();
        File.Copy(
            typeof(G4DeploymentTests).Assembly.Location,
            Path.Combine(fixture.Runtime, "libvlc.dll"),
            overwrite: true);

        var result = fixture.Probe.Check();

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DeploymentIssueCode.NativeArchitectureMismatch);
    }

    [Fact]
    public void Probe_CollectsAllMissingPluginModules_AndCanRetry()
    {
        using var fixture = DeploymentFixture.Create();
        var mp4 = Path.Combine(fixture.Runtime, "plugins", "demux", "libmp4_plugin.dll");
        var codec = Path.Combine(fixture.Runtime, "plugins", "codec", "libavcodec_plugin.dll");
        File.Delete(mp4);
        File.Delete(codec);

        var failed = fixture.Probe.Check();
        File.Copy(fixture.Source(mp4), mp4);
        File.Copy(fixture.Source(codec), codec);
        var repaired = fixture.Probe.Check();

        Assert.Equal(
            2,
            failed.Issues.Count(x => x.Code == DeploymentIssueCode.NativePluginSetIncomplete));
        Assert.True(repaired.IsReady);
    }

    [Fact]
    public void Runtime_ConcurrentInitialization_RunsExactlyOnce()
    {
        var calls = 0;
        var runtime = new LibVlcRuntime(
            new StaticProbe(ReadyResult()),
            _ => Interlocked.Increment(ref calls));

        Parallel.For(0, 32, _ => runtime.EnsureInitialized());

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Runtime_InitializationFailure_IsActionableAndDoesNotLeakExceptionText()
    {
        var runtime = new LibVlcRuntime(
            new StaticProbe(ReadyResult()),
            _ => throw new DllNotFoundException("sensitive native loader detail"));

        var exception = Assert.Throws<PlaybackDeploymentException>(
            runtime.EnsureInitialized);
        var failure = PlaybackFailureMapper.MapDeployment(exception.Result);

        Assert.Equal(PlaybackFailureCode.DeploymentUnavailable, failure.Code);
        Assert.Equal("DEPLOYMENT_NativeInitializationFailed", failure.DiagnosticCode);
        Assert.DoesNotContain("sensitive", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(failure.SuggestedAction));
    }

    [Fact]
    public async Task PlayerViewModel_BlocksPlaybackUntilRetrySucceeds()
    {
        var probe = new MutableProbe(FailedResult());
        using var session = new EmptySession();
        var initializer = new CountingInitializer();
        using var viewModel = new VideoPlayerControlViewModel(
            session,
            session,
            probe,
            initializer);

        var blocked = await viewModel.LoadMediaAsync("ignored.secvid", "secret");
        probe.Result = ReadyResult();
        viewModel.RetryDeploymentCheckCommand.Execute(null);

        Assert.False(blocked);
        Assert.True(viewModel.IsPlaybackAvailable);
        Assert.Empty(viewModel.DeploymentIssueText);
        Assert.Equal(0, session.LoadCalls);
        Assert.Equal(1, initializer.Calls);
    }

    [Theory]
    [InlineData(Key.Space, false)]
    [InlineData(Key.Left, false)]
    [InlineData(Key.Right, false)]
    [InlineData(Key.Up, true)]
    [InlineData(Key.Down, true)]
    public void 播放快捷键只执行当前状态允许的命令(Key key, bool expectedHandled)
    {
        // G14 的两轮覆盖率证据不能依赖 Headless UI 测试是否恰好经过路由器。
        // 这里直接验证路由职责：策略负责按键映射，路由器只选择并执行现有命令。
        using var session = new EmptySession();
        using var viewModel = new VideoPlayerControlViewModel(
            session,
            session,
            new MutableProbe(ReadyResult()),
            new CountingInitializer());
        var keyEvent = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None
        };

        Assert.Equal(
            expectedHandled,
            PlaybackShortcutRouter.TryHandle(keyEvent, viewModel));
    }

    [Fact]
    public void 未映射快捷键与空参数不会被路由器静默接受()
    {
        using var session = new EmptySession();
        using var viewModel = new VideoPlayerControlViewModel(
            session,
            session,
            new MutableProbe(ReadyResult()),
            new CountingInitializer());
        var keyEvent = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F1,
            KeyModifiers = KeyModifiers.None
        };

        Assert.False(PlaybackShortcutRouter.TryHandle(keyEvent, viewModel));
        Assert.Throws<ArgumentNullException>(() =>
            PlaybackShortcutRouter.TryHandle(null!, viewModel));
        Assert.Throws<ArgumentNullException>(() =>
            PlaybackShortcutRouter.TryHandle(keyEvent, null!));
    }

    [Fact]
    public async Task LazyBackend_DoesNotCreateNativeHostUntilFirstMediaPreparation()
    {
        var factory = new CountingBackendFactory();
        using var backend = new LazyPlaybackBackend(factory);
        var createdEvents = 0;
        backend.OutputChanged += (_, _) => createdEvents++;

        backend.SetVolume(73);
        Assert.Null(backend.NativePlayer);
        Assert.Equal(0, backend.NativeOutputGeneration);
        Assert.Equal(0, factory.CreateCalls);

        using var first = await backend.CreateAsync(1, "one", "password", default);
        using var second = await backend.CreateAsync(2, "two", "password", default);

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, createdEvents);
        Assert.Equal(73, factory.Host!.Volume);
        Assert.Equal(factory.Host.NativeOutputGeneration, backend.NativeOutputGeneration);
        backend.Initialize();
        backend.Initialize();
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, createdEvents);
        factory.Host.NotifyOutputChanged();
        Assert.Equal(2, createdEvents);
        backend.Dispose();
        factory.Host.NotifyOutputChanged();
        Assert.Equal(2, createdEvents);
        Assert.Throws<ObjectDisposedException>(backend.Initialize);
    }

    private static DeploymentCheckResult ReadyResult()
    {
        var root = Path.GetFullPath("plugin");
        return new DeploymentCheckResult(root, Path.Combine(root, "native"), []);
    }

    private static DeploymentCheckResult FailedResult()
    {
        var root = Path.GetFullPath("plugin");
        return new DeploymentCheckResult(
            root,
            Path.Combine(root, "native"),
            [
                new DeploymentIssue(
                    DeploymentIssueCode.NativeLibraryMissing,
                    "运行库缺失。",
                    Path.Combine(root, "native", "libvlc.dll"),
                    "重新部署插件。")
            ]);
    }

    private sealed class StaticProbe(DeploymentCheckResult result) : IPlaybackDeploymentProbe
    {
        public DeploymentCheckResult Check() => result;
    }

    private sealed class MutableProbe(DeploymentCheckResult result) :
        IPlaybackDeploymentProbe,
        IPlaybackPlatformStatus
    {
        public DeploymentCheckResult Result { get; set; } = result;
        public PlaybackPlatformCapabilities Capabilities { get; } = SupportedCapabilities();
        public DeploymentCheckResult Check() => Result;
    }

    private sealed class CountingInitializer : IPlaybackBackendInitializer
    {
        public int Calls { get; private set; }
        public void Initialize() => Calls++;
    }

    private sealed class EmptySession :
        ISecureVideoPlaybackSession,
        IPlaybackSurfaceSession,
        IPlaybackVideoOutput
    {
        public int LoadCalls { get; private set; }
        public event EventHandler<PlaybackChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
        public event EventHandler? OutputChanged
        {
            add { }
            remove { }
        }
        public PlaybackSnapshot Snapshot => PlaybackSnapshot.Empty;
        public IPlaybackVideoOutput VideoOutput => this;
        public long Generation => 0;

        public Task<PlaybackOperationResult> LoadAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(PlaybackOperationResult.Succeeded());
        }

        public Task<PlaybackOperationResult> LoadAndPlayAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) =>
            LoadAsync(filePath, password, cancellationToken);

        public Task<PlaybackOperationResult> PlayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> PauseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> SeekAsync(
            long positionMs,
            bool waitForFrame = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public Task<PlaybackOperationResult> SeekRelativeAsync(
            long deltaMs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

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
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public bool SetVolume(int volume) => true;
        public void DetachSurface(VideoSurfaceIdentity surface) { }

        public Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(
            VideoSurfaceIdentity surface,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());

        public void Dispose()
        {
        }
    }

    private sealed class CountingBackendFactory : IPlaybackBackendFactory
    {
        public int CreateCalls { get; private set; }
        public FakeHost? Host { get; private set; }

        public PlaybackBackend Create()
        {
            CreateCalls++;
            Host = new FakeHost();
            return new PlaybackBackend(Host, new NoopSourceFactory());
        }
    }

    private sealed class NoopSourceFactory : IPlaybackMediaSourceFactory
    {
        public Task<IPlaybackMediaSource> CreateAsync(
            long generation,
            string filePath,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPlaybackMediaSource>(new NoopSource(generation));
    }

    private sealed class NoopSource(long generation) : IPlaybackMediaSource
    {
        public long Generation { get; } = generation;
        public Media NativeMedia => null!;
        public event Action<IPlaybackMediaSource, PlaybackFailure>? Failed
        {
            add { }
            remove { }
        }
        public void PrepareForPlayback() { }
        public void RequestStop() { }
        public void Dispose() { }
    }

    private sealed class FakeHost : IPlaybackPlayerHost
    {
        public event EventHandler? OutputChanged;
        public void Initialize() { }
        public void NotifyOutputChanged() => OutputChanged?.Invoke(this, EventArgs.Empty);

        public MediaPlayer? NativePlayer => null;
        public long NativeOutputGeneration => 41;
        public long PositionMs => 0;
        public long DurationMs => 0;
        public bool IsSeekable => false;
        public bool HasVideo => false;
        public bool HasAudio => false;
        public int VideoTrackCount => 0;
        public int AudioTrackCount => 0;
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public int Volume { get; private set; }
        public float Rate { get; private set; } = 1.0f;
        public int AudioTrack { get; private set; } = -1;
        public int SubtitleTrack { get; private set; } = -1;
        public event Action<long, PlaybackState>? StateChanged
        {
            add { }
            remove { }
        }
        public event Action<long>? PositionChanged
        {
            add { }
            remove { }
        }
        public event Action<long, PlaybackFailure>? Failed
        {
            add { }
            remove { }
        }
        public void Attach(IPlaybackMediaSource source) { }
        public void Detach() { }
        public bool Play() => true;
        public void Stop() { }
        public void SetPause(bool paused) { }
        public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void SetVolume(int volume) => Volume = volume;
        public bool SetRate(float rate)
        {
            Rate = rate;
            return true;
        }
        public IReadOnlyList<PlaybackTrackOption> GetAudioTracks() =>
            Array.Empty<PlaybackTrackOption>();
        public IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks() =>
            [new PlaybackTrackOption(-1, "关闭字幕")];
        public bool SetAudioTrack(int trackId)
        {
            AudioTrack = trackId;
            return true;
        }
        public bool SetSubtitleTrack(int trackId)
        {
            SubtitleTrack = trackId;
            return true;
        }
        public Task SeekAsync(long positionMs, bool waitForFrame, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<bool> RestoreSurfaceAsync(long positionMs, bool restorePaused, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public void Dispose() { }
    }

    private static PlaybackPlatformCapabilities SupportedCapabilities() =>
        new(
            "windows-x64",
            IsSupported: true,
            SupportsNativeVideoOutput: true,
            SupportsEmbeddedFullscreen: true,
            SupportsAudioTrackSelection: true,
            SupportsSubtitleTrackSelection: true,
            UsesBundledRuntime: true,
            UnsupportedReason: null);

    private static void AssertNativeVersion(string path)
    {
        Assert.True(File.Exists(path), $"缺少私有原生运行时：{path}");
        var version = FileVersionInfo.GetVersionInfo(path);
        // NuGet 包版本为 3.0.23.1，而 VideoLAN DLL 的文件版本只发布到 3.0.23；
        // 两者分别验证，避免把包修订号错误地当成原生二进制版本段。
        Assert.Equal(3, version.FileMajorPart);
        Assert.Equal(0, version.FileMinorPart);
        Assert.Equal(23, version.FileBuildPart);
    }

    private sealed class DeploymentFixture : IDisposable
    {
        private static readonly string[] RelativeFiles =
        [
            "LibVLCSharp.dll",
            "LibVLCSharp.Avalonia.dll",
            Path.Combine("native", "win-x64", "libvlc", "libvlc.dll"),
            Path.Combine("native", "win-x64", "libvlc", "libvlccore.dll"),
            Path.Combine("native", "win-x64", "libvlc", "plugins", "demux", "libmp4_plugin.dll"),
            Path.Combine("native", "win-x64", "libvlc", "plugins", "demux", "libmkv_plugin.dll"),
            Path.Combine("native", "win-x64", "libvlc", "plugins", "codec", "libavcodec_plugin.dll"),
            Path.Combine("native", "win-x64", "libvlc", "plugins", "video_output", "libdirect3d11_plugin.dll"),
            Path.Combine("native", "win-x64", "libvlc", "plugins", "audio_output", "libmmdevice_plugin.dll")
        ];

        private readonly string _sourceRoot;

        private DeploymentFixture(string root, string sourceRoot)
        {
            Root = root;
            _sourceRoot = sourceRoot;
            Runtime = Path.Combine(root, "native", "win-x64", "libvlc");
            Probe = new PlaybackDeploymentProbe(
                root,
                () => true,
                () => Architecture.X64);
        }

        public string Root { get; }
        public string Runtime { get; }
        public PlaybackDeploymentProbe Probe { get; }

        public static DeploymentFixture Create()
        {
            var sourceRoot =
                new PluginLocalPlaybackRuntimeLayoutProvider().Resolve().PluginDirectory;
            var root = Path.Combine(
                Path.GetTempPath(),
                "VideoSecurityPlayer-G4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            foreach (var relativePath in RelativeFiles)
            {
                var source = Path.Combine(sourceRoot, relativePath);
                var destination = Path.Combine(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }

            return new DeploymentFixture(root, sourceRoot);
        }

        public string Source(string fixturePath) =>
            Path.Combine(_sourceRoot, Path.GetRelativePath(Root, fixturePath));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
