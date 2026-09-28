namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>串行绑定一次原生库，公开实际状态；探针失败可重查，绑定失败必须重启。</summary>
/// <remarks>选择、预检、绑定和状态各自有单一职责。此对象不创建播放器，不拥有文档资源。</remarks>
internal sealed class LibVlcRuntime : IPlaybackRuntimeInitializer, IPlaybackRuntimeStatus
{
    private readonly object _sync = new();
    private readonly Func<PlaybackRuntimeResolution> _resolve;
    private readonly Action<string> _initialize;
    private readonly Func<string> _version;
    private readonly RuntimeLoadState _state = new();
    private readonly IPlaybackRuntimeLayoutProvider _layout;
    private PlaybackDeploymentException? _failure;
    public event EventHandler? Changed;
    public PlaybackRuntimeSnapshot Snapshot => _state.Snapshot;

    public LibVlcRuntime() : this(new PlaybackDeploymentProbe(), new PluginLocalPlaybackRuntimeLayoutProvider()) { }

    public LibVlcRuntime(IPlaybackDeploymentProbe probe, IPlaybackRuntimeLayoutProvider layout)
        : this(probe, layout, LibVlcNativeBinding.Initialize) { }

    internal LibVlcRuntime(IPlaybackDeploymentProbe probe, Action<string> initialize)
        : this(probe, new PluginLocalPlaybackRuntimeLayoutProvider(), initialize) { }

    internal LibVlcRuntime(IPlaybackDeploymentProbe probe, IPlaybackRuntimeLayoutProvider layout, Action<string> initialize)
        : this(() => new(PlaybackRuntimeSource.Bundled, probe.Check(), ""), initialize, () => "", layout)
    { ArgumentNullException.ThrowIfNull(probe); }

    internal LibVlcRuntime(Func<PlaybackRuntimeResolution> resolve, Action<string> initialize, Func<string> version,
        IPlaybackRuntimeLayoutProvider? layout = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _layout = layout ?? new PluginLocalPlaybackRuntimeLayoutProvider();
    }

    internal string RuntimeDirectory => Snapshot.LoadAttempted ? Snapshot.Directory : _layout.Resolve().RuntimeDirectory;

    public void EnsureInitialized()
    {
        if (Snapshot.Status == RuntimeLoadStatus.Ready) return;
        lock (_sync)
        {
            if (Snapshot.Status == RuntimeLoadStatus.Ready) return;
            if (_failure is not null) throw _failure;
            var resolution = _resolve();
            if (!resolution.IsReady) throw new PlaybackDeploymentException(resolution.Check);
            _state.Begin(resolution);
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                _initialize(resolution.Directory);
                _state.Complete(true, _version());
            }
            catch (Exception error)
            {
                _state.Complete(false);
                _failure = new PlaybackDeploymentException(PlaybackDeploymentProbe.WithInitializationFailure(resolution.Check), error);
                throw _failure;
            }
            finally { Changed?.Invoke(this, EventArgs.Empty); }
        }
    }
}
