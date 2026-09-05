using LibVLCSharp.Shared;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 负责从 VideoSecurityPlayer 插件自身目录初始化唯一的 LibVLC 原生运行时。
/// </summary>
/// <remarks>
/// 路径以当前程序集位置为基准，而不是以宿主进程工作目录为基准，因此插件移动到任意宿主目录后仍可自包含运行。
/// 初始化过程使用双重检查锁，保证多个播放器在不同线程同时首次调用时也只执行一次 Core.Initialize。
/// 本实现明确不回退到宿主根目录、PATH 或系统 VLC，避免误用版本不一致的原生库导致难以复现的崩溃。
/// </remarks>
internal sealed class LibVlcRuntime : IPlaybackRuntimeInitializer
{
    private readonly object _syncRoot = new();
    private readonly IPlaybackDeploymentProbe _deploymentProbe;
    private readonly IPlaybackRuntimeLayoutProvider _layoutProvider;
    private readonly Action<string> _initialize;
    private bool _initialized;

    public LibVlcRuntime(
        IPlaybackDeploymentProbe deploymentProbe,
        IPlaybackRuntimeLayoutProvider layoutProvider)
        : this(deploymentProbe, layoutProvider, Core.Initialize)
    {
    }

    public LibVlcRuntime()
        : this(
            new PlaybackDeploymentProbe(),
            new PluginLocalPlaybackRuntimeLayoutProvider(),
            Core.Initialize)
    {
    }

    internal LibVlcRuntime(
        IPlaybackDeploymentProbe deploymentProbe,
        IPlaybackRuntimeLayoutProvider layoutProvider,
        Action<string> initialize)
    {
        _deploymentProbe = deploymentProbe ??
                           throw new ArgumentNullException(nameof(deploymentProbe));
        _layoutProvider = layoutProvider ??
                          throw new ArgumentNullException(nameof(layoutProvider));
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    internal LibVlcRuntime(
        IPlaybackDeploymentProbe deploymentProbe,
        Action<string> initialize)
        : this(
            deploymentProbe,
            new PluginLocalPlaybackRuntimeLayoutProvider(),
            initialize)
    {
    }

    /// <summary>
    /// 获取当前 VideoSecurityPlayer.Plugin.dll 对应的 LibVLC 私有绝对目录。
    /// </summary>
    internal string RuntimeDirectory => _layoutProvider.Resolve().RuntimeDirectory;

    /// <summary>
    /// 验证平台和必要原生文件，并以线程安全方式完成进程级初始化。
    /// </summary>
    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_initialized)
            {
                return;
            }

            var deployment = _deploymentProbe.Check();
            if (!deployment.IsReady)
            {
                throw new PlaybackDeploymentException(deployment);
            }

            try
            {
                _initialize(deployment.RuntimeDirectory);
                _initialized = true;
            }
            catch (PlaybackDeploymentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlaybackDeploymentException(
                    PlaybackDeploymentProbe.WithInitializationFailure(deployment),
                    ex);
            }
        }
    }
}
