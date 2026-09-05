using System.Reflection;
using System.Runtime.InteropServices;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G9 平台能力和原生表面边界测试。
/// </summary>
public sealed class G9PlatformAbstractionTests
{
    [Fact]
    public void WindowsX64Capabilities_ExposeTheOnlyProductionPlatform()
    {
        var provider = new WindowsX64PlaybackCapabilitiesProvider(
            () => true,
            () => Architecture.X64);

        var capabilities = provider.GetCapabilities();

        Assert.Equal("windows-x64", capabilities.PlatformId);
        Assert.True(capabilities.IsSupported);
        Assert.True(capabilities.SupportsNativeVideoOutput);
        Assert.True(capabilities.SupportsEmbeddedFullscreen);
        Assert.True(capabilities.SupportsAudioTrackSelection);
        Assert.True(capabilities.SupportsSubtitleTrackSelection);
        Assert.True(capabilities.UsesBundledRuntime);
        Assert.Null(capabilities.UnsupportedReason);
    }

    [Theory]
    [InlineData(false, Architecture.X64)]
    [InlineData(true, Architecture.X86)]
    [InlineData(true, Architecture.Arm64)]
    public void UnsupportedRuntime_ReturnsExplicitCapabilities(
        bool isWindows,
        Architecture architecture)
    {
        var provider = new WindowsX64PlaybackCapabilitiesProvider(
            () => isWindows,
            () => architecture);

        var capabilities = provider.GetCapabilities();

        Assert.False(capabilities.IsSupported);
        Assert.False(capabilities.SupportsNativeVideoOutput);
        Assert.False(capabilities.SupportsEmbeddedFullscreen);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.UnsupportedReason));
    }

    [Fact]
    public void RuntimeLayout_UsesAssemblyLocationInsteadOfWorkingDirectory()
    {
        var assemblyPath = Path.Combine(
            Path.GetTempPath(),
            "g9-plugin",
            "VideoSecurityPlayer.Plugin.dll");
        var provider = new PluginLocalPlaybackRuntimeLayoutProvider(() => assemblyPath);

        var layout = provider.Resolve();

        Assert.Equal(
            Path.GetFullPath(Path.GetDirectoryName(assemblyPath)!),
            layout.PluginDirectory);
        Assert.Equal(
            Path.Combine(
                layout.PluginDirectory,
                "native",
                "win-x64",
                "libvlc"),
            layout.RuntimeDirectory);
    }

    [Fact]
    public void UnsupportedPlatform_DoesNotInitializeDocumentBackend()
    {
        using var session = new FakeSession();
        var initializer = new CountingInitializer();
        var platform = new FakePlatformStatus(
            Supported: false,
            DeploymentReady: true);
        using var viewModel = new VideoPlayerControlViewModel(
            session,
            session,
            platform,
            initializer);

        Assert.False(viewModel.IsPlaybackAvailable);
        Assert.Equal(0, initializer.Calls);
        Assert.Contains("不支持", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SurfaceCoordinator_DetachesBeforeDestroyAndRestoresNewGeneration()
    {
        var log = new List<string>();
        var surface = new FakeSurface(log);
        using var coordinator = new PlaybackSurfaceCoordinator(surface);
        using var session = new FakeSession(log);
        coordinator.Bind(session);

        var firstCompleted = WaitForAttachmentAsync(coordinator, generation: 1);
        surface.Create(1);
        await firstCompleted;
        surface.Destroy();

        var secondCompleted = WaitForAttachmentAsync(coordinator, generation: 2);
        surface.Create(2);
        await secondCompleted;

        Assert.Equal(
            [
                "output:set",
                "surface:ready:1",
                "session:attach:1",
                "surface:losing:1",
                "session:detach:1",
                "surface:destroyed:1",
                "surface:ready:2",
                "session:attach:2"
            ],
            log);
    }

    [Fact]
    public void PublicPlaybackContracts_DoNotExposeHandlesOrLibVlcTypes()
    {
        Type[] contracts =
        [
            typeof(VideoSurfaceIdentity),
            typeof(IPlaybackVideoOutput),
            typeof(IPlaybackSurfaceSession),
            typeof(ISecureVideoPlaybackSession),
            typeof(PlaybackCoordinatorViewModel),
            typeof(VideoPlayerControlViewModel)
        ];

        var exposedTypes = contracts.SelectMany(GetPublicSignatureTypes).ToArray();

        Assert.DoesNotContain(exposedTypes, type => type == typeof(IntPtr));
        Assert.DoesNotContain(
            exposedTypes,
            type => string.Equals(
                type.Namespace,
                "LibVLCSharp.Shared",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerViewModels_DoNotDependOnUiDockOrNativePlayerTypes()
    {
        var viewModelTypes = typeof(VideoPlayerControlViewModel).Assembly
            .GetTypes()
            .Where(type =>
                type == typeof(VideoPlayerControlViewModel) ||
                type == typeof(LibraryBrowserCoordinatorViewModel) ||
                type.Namespace?.StartsWith(
                    "VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback",
                    StringComparison.Ordinal) == true)
            .ToArray();

        var prohibited = viewModelTypes
            .SelectMany(GetDeclaredDependencyTypes)
            .Distinct()
            .Where(type =>
                type == typeof(IntPtr) ||
                type == typeof(UIntPtr) ||
                type.Name is "VideoView" or "MediaPlayer" ||
                type.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true ||
                type.Namespace?.StartsWith("Dock.", StringComparison.Ordinal) == true ||
                type.Namespace?.StartsWith("LibVLCSharp", StringComparison.Ordinal) == true)
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(prohibited);
    }

    private static async Task WaitForAttachmentAsync(
        PlaybackSurfaceCoordinator coordinator,
        long generation)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VideoSurfaceAttachmentCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.Surface.Generation == generation)
            {
                completion.TrySetResult();
            }
        };
        coordinator.AttachmentCompleted += handler;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            coordinator.AttachmentCompleted -= handler;
        }
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type contract)
    {
        yield return contract;
        foreach (var property in contract.GetProperties(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.DeclaredOnly))
        {
            foreach (var type in Flatten(property.PropertyType))
            {
                yield return type;
            }
        }

        foreach (var method in contract.GetMethods(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.DeclaredOnly))
        {
            foreach (var type in Flatten(method.ReturnType))
            {
                yield return type;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var type in Flatten(parameter.ParameterType))
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<Type> GetDeclaredDependencyTypes(Type type)
    {
        if (type.BaseType is { } baseType)
        {
            foreach (var dependency in Flatten(baseType))
                yield return dependency;
        }

        foreach (var interfaceType in type.GetInterfaces())
        {
            foreach (var dependency in Flatten(interfaceType))
                yield return dependency;
        }

        foreach (var field in type.GetFields(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            foreach (var dependency in Flatten(field.FieldType))
                yield return dependency;
        }

        foreach (var constructor in type.GetConstructors(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var dependency in Flatten(parameter.ParameterType))
                    yield return dependency;
            }
        }

        foreach (var method in type.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            foreach (var dependency in Flatten(method.ReturnType))
                yield return dependency;
            foreach (var parameter in method.GetParameters())
            {
                foreach (var dependency in Flatten(parameter.ParameterType))
                    yield return dependency;
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var element in Flatten(type.GetElementType()!))
                yield return element;
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    private sealed class CountingInitializer : IPlaybackBackendInitializer
    {
        public int Calls { get; private set; }

        public void Initialize() => Calls++;
    }

    private sealed class FakePlatformStatus(
        bool Supported,
        bool DeploymentReady) : IPlaybackPlatformStatus
    {
        public PlaybackPlatformCapabilities Capabilities { get; } = new(
            "windows-x64",
            IsSupported: Supported,
            SupportsNativeVideoOutput: Supported,
            SupportsEmbeddedFullscreen: Supported,
            SupportsAudioTrackSelection: Supported,
            SupportsSubtitleTrackSelection: Supported,
            UsesBundledRuntime: true,
            UnsupportedReason: Supported ? null : "当前测试平台不支持原生视频输出。");

        public DeploymentCheckResult Check()
        {
            var root = Path.GetFullPath("g9-test-plugin");
            return new DeploymentCheckResult(
                root,
                Path.Combine(root, "native"),
                DeploymentReady
                    ? []
                    :
                    [
                        new DeploymentIssue(
                            DeploymentIssueCode.NativeLibraryMissing,
                            "运行库缺失。",
                            Path.Combine(root, "native", "libvlc.dll"),
                            "重新部署插件。")
                    ]);
        }
    }

    private sealed class FakeSurface(List<string> log) : IPlaybackVideoSurface
    {
        private IPlaybackVideoOutput? _output;

        public event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceReady;
        public event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceLosing;

        public VideoSurfaceIdentity? CurrentSurface { get; private set; }

        public IPlaybackVideoOutput? Output
        {
            get => _output;
            set
            {
                _output = value;
                log.Add(value is null ? "output:clear" : "output:set");
            }
        }

        public void Create(long generation)
        {
            var identity = new VideoSurfaceIdentity(generation);
            CurrentSurface = identity;
            log.Add($"surface:ready:{generation}");
            SurfaceReady?.Invoke(this, new VideoSurfaceChangedEventArgs(identity));
        }

        public void Destroy()
        {
            var identity = CurrentSurface!.Value;
            log.Add($"surface:losing:{identity.Generation}");
            SurfaceLosing?.Invoke(this, new VideoSurfaceChangedEventArgs(identity));
            log.Add($"surface:destroyed:{identity.Generation}");
            CurrentSurface = null;
        }
    }

    private sealed class FakeSession :
        ISecureVideoPlaybackSession,
        IPlaybackSurfaceSession,
        IPlaybackVideoOutput
    {
        private readonly List<string>? _log;

        public FakeSession(List<string>? log = null)
        {
            _log = log;
        }

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
        public long Generation => 1;

        public void DetachSurface(VideoSurfaceIdentity surface) =>
            _log?.Add($"session:detach:{surface.Generation}");

        public Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(
            VideoSurfaceIdentity surface,
            CancellationToken cancellationToken = default)
        {
            _log?.Add($"session:attach:{surface.Generation}");
            return Task.FromResult(PlaybackOperationResult.Succeeded());
        }

        public Task<PlaybackOperationResult> LoadAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> LoadAndPlayAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> PlayAsync(
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> PauseAsync(
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> StopAsync(
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> SeekAsync(
            long positionMs,
            bool waitForFrame = false,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> SeekRelativeAsync(
            long deltaMs,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> SetRateAsync(
            float rate,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> SelectAudioTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> SelectSubtitleTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) => Success();

        public Task<PlaybackOperationResult> ReleaseAsync(
            CancellationToken cancellationToken = default) => Success();

        public bool SetVolume(int volume) => true;

        public void Dispose()
        {
        }

        private static Task<PlaybackOperationResult> Success() =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
    }
}
