using System.Runtime.InteropServices;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 描述当前进程能够安全使用的播放平台能力。
/// </summary>
/// <remarks>
/// 该快照只表达“平台实现是否具备某项能力”，不表达当前媒体是否真的包含音轨或字幕。
/// 媒体级能力仍由 <see cref="PlaybackSnapshot"/> 提供，避免把部署能力和媒体状态混为一谈。
/// </remarks>
public sealed record PlaybackPlatformCapabilities(
    string PlatformId,
    bool IsSupported,
    bool SupportsNativeVideoOutput,
    bool SupportsEmbeddedFullscreen,
    bool SupportsAudioTrackSelection,
    bool SupportsSubtitleTrackSelection,
    bool UsesBundledRuntime,
    string? UnsupportedReason);

/// <summary>
/// ViewModel 使用的平台状态端口；只提供能力和无副作用部署检查。
/// </summary>
public interface IPlaybackPlatformStatus
{
    PlaybackPlatformCapabilities Capabilities { get; }

    DeploymentCheckResult Check();
}

/// <summary>
/// 插件私有播放运行时的绝对目录布局。
/// </summary>
internal sealed record PlaybackRuntimeLayout(
    string PluginDirectory,
    string RuntimeDirectory);

/// <summary>
/// 只负责根据插件程序集位置解析运行时目录，不检查文件也不初始化原生库。
/// </summary>
internal interface IPlaybackRuntimeLayoutProvider
{
    PlaybackRuntimeLayout Resolve();
}

/// <summary>
/// 以 VideoSecurityPlayer.Plugin.dll 的真实位置为唯一锚点解析私有 LibVLC。
/// </summary>
internal sealed class PluginLocalPlaybackRuntimeLayoutProvider : IPlaybackRuntimeLayoutProvider
{
    internal static readonly string RuntimeRelativePath = Path.Combine(
        "native",
        "win-x64",
        "libvlc");

    private readonly Func<string> _assemblyLocation;

    public PluginLocalPlaybackRuntimeLayoutProvider()
        : this(() => typeof(PluginLocalPlaybackRuntimeLayoutProvider).Assembly.Location)
    {
    }

    internal PluginLocalPlaybackRuntimeLayoutProvider(Func<string> assemblyLocation)
    {
        _assemblyLocation = assemblyLocation ??
                            throw new ArgumentNullException(nameof(assemblyLocation));
    }

    public PlaybackRuntimeLayout Resolve()
    {
        var location = _assemblyLocation();
        var pluginDirectory = Path.GetDirectoryName(location)
                              ?? throw new InvalidOperationException(
                                  "无法确定 VideoSecurityPlayer 程序集目录。");
        pluginDirectory = Path.GetFullPath(pluginDirectory);
        return new PlaybackRuntimeLayout(
            pluginDirectory,
            Path.Combine(pluginDirectory, RuntimeRelativePath));
    }
}

/// <summary>
/// Windows x64 是 G9 唯一生产平台；其他平台只返回明确的不支持能力。
/// </summary>
internal sealed class WindowsX64PlaybackCapabilitiesProvider
{
    private readonly Func<bool> _isWindows;
    private readonly Func<Architecture> _processArchitecture;

    public WindowsX64PlaybackCapabilitiesProvider()
        : this(
            OperatingSystem.IsWindows,
            () => RuntimeInformation.ProcessArchitecture)
    {
    }

    internal WindowsX64PlaybackCapabilitiesProvider(
        Func<bool> isWindows,
        Func<Architecture> processArchitecture)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _processArchitecture = processArchitecture ??
                               throw new ArgumentNullException(nameof(processArchitecture));
    }

    internal bool IsWindows => _isWindows();

    internal Architecture ProcessArchitecture => _processArchitecture();

    public PlaybackPlatformCapabilities GetCapabilities()
    {
        var isWindows = IsWindows;
        var isX64 = ProcessArchitecture == Architecture.X64;
        var supported = isWindows && isX64;
        var reason = !isWindows
            ? "安全视频播放器当前只支持 Windows。"
            : !isX64
                ? "安全视频播放器当前只支持 x64 宿主进程。"
                : null;

        return new PlaybackPlatformCapabilities(
            "windows-x64",
            supported,
            SupportsNativeVideoOutput: supported,
            SupportsEmbeddedFullscreen: supported,
            SupportsAudioTrackSelection: supported,
            SupportsSubtitleTrackSelection: supported,
            UsesBundledRuntime: true,
            UnsupportedReason: reason);
    }
}

/// <summary>
/// 把能力判定和部署探针组合成 ViewModel 所需的窄平台状态。
/// </summary>
internal sealed class PlaybackPlatformStatus(
    WindowsX64PlaybackCapabilitiesProvider capabilitiesProvider,
    IPlaybackDeploymentProbe deploymentProbe) : IPlaybackPlatformStatus
{
    private readonly WindowsX64PlaybackCapabilitiesProvider _capabilitiesProvider =
        capabilitiesProvider ?? throw new ArgumentNullException(nameof(capabilitiesProvider));
    private readonly IPlaybackDeploymentProbe _deploymentProbe =
        deploymentProbe ?? throw new ArgumentNullException(nameof(deploymentProbe));

    public PlaybackPlatformCapabilities Capabilities =>
        _capabilitiesProvider.GetCapabilities();

    public DeploymentCheckResult Check() => _deploymentProbe.Check();
}

/// <summary>
/// 原生运行时初始化端口。Backend 只依赖该职责，不读取平台路径或 UI 能力。
/// </summary>
internal interface IPlaybackRuntimeInitializer
{
    void EnsureInitialized();
}
