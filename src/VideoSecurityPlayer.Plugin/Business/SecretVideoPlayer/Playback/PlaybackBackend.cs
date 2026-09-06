using LibVLCSharp.Shared;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// Windows 表面适配器访问 LibVLC 输出的程序集内部端口。
/// </summary>
/// <remarks>
/// 该接口绝不能提升为公共播放契约；它存在的唯一目的，是把 MediaPlayer 限制在
/// LibVLC 与 Avalonia 原生表面的适配边界内。
/// </remarks>
internal interface ILibVlcVideoOutputAccessor : IPlaybackVideoOutput
{
    MediaPlayer? NativePlayer { get; }
}

internal sealed class PlaybackBackend(
    IPlaybackPlayerHost playerHost,
    IPlaybackMediaSourceFactory mediaSourceFactory) : IDisposable
{
    public IPlaybackPlayerHost PlayerHost { get; } =
        playerHost ?? throw new ArgumentNullException(nameof(playerHost));

    public IPlaybackMediaSourceFactory MediaSourceFactory { get; } =
        mediaSourceFactory ?? throw new ArgumentNullException(nameof(mediaSourceFactory));

    public void Dispose() => PlayerHost.Dispose();
}

internal interface IPlaybackBackendFactory
{
    PlaybackBackend Create();
}

/// <summary>
/// 页面只需要表达“部署通过后准备原生 backend”，不应知道 PlayerHost 或 SourceFactory。
/// 该窄端口同时允许部署失败时完全跳过原生对象创建。
/// </summary>
public interface IPlaybackBackendInitializer
{
    void Initialize();
}

/// <summary>会话需要的输出就绪契约，不暴露后端实现或原生类型。</summary>
/// <remarks>
/// Initialize 必须幂等并在返回前准备好输出；OutputChanged 在新输出可读取之后发布。
/// 惰性主机第一次初始化会发布通知，已就绪且输出终生稳定的主机无需重复发布。
/// 该端口不转移资源所有权，调用方只订阅/退订，不通过它释放后端。
/// </remarks>
internal interface IPlaybackOutputLifecycle : IPlaybackBackendInitializer
{
    event EventHandler? OutputChanged;
}

internal sealed class LibVlcPlaybackBackendFactory(IPlaybackRuntimeInitializer runtime)
    : IPlaybackBackendFactory
{
    public PlaybackBackend Create()
    {
        var host = new LibVlcDocumentPlayerHost(runtime);
        return new PlaybackBackend(host, new LibVlcPlaybackMediaSourceFactory(host));
    }
}

/// <summary>
/// Document-scoped 惰性代理。插件加载以及部署失败的页面创建不会触发原生加载；
/// 部署通过后由页面在首次 View 绑定前原子创建配套的 PlayerHost 与 SourceFactory。
/// CreateAsync 仍保留幂等兜底，使脱离标准 UI 的调用方也不会得到半初始化对象。
/// </summary>
internal sealed class LazyPlaybackBackend :
    IPlaybackPlayerHost,
    IPlaybackMediaSourceFactory,
    IPlaybackBackendInitializer
{
    private readonly IPlaybackBackendFactory _factory;
    private readonly object _syncRoot = new();
    private PlaybackBackend? _backend;
    private int _desiredVolume = 50;
    private int _disposeState;

    public LazyPlaybackBackend(IPlaybackBackendFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public event EventHandler? OutputChanged;
    public event Action<long, PlaybackState>? StateChanged;
    public event Action<long>? PositionChanged;
    public event Action<long, PlaybackFailure>? Failed;

    public MediaPlayer? NativePlayer => _backend?.PlayerHost.NativePlayer;
    public long NativeOutputGeneration =>
        _backend?.PlayerHost.NativeOutputGeneration ?? 0;
    public long PositionMs => _backend?.PlayerHost.PositionMs ?? 0;
    public long DurationMs => _backend?.PlayerHost.DurationMs ?? 0;
    public bool IsSeekable => _backend?.PlayerHost.IsSeekable ?? false;
    public bool HasVideo => _backend?.PlayerHost.HasVideo ?? false;
    public bool HasAudio => _backend?.PlayerHost.HasAudio ?? false;
    public int VideoTrackCount => _backend?.PlayerHost.VideoTrackCount ?? 0;
    public int AudioTrackCount => _backend?.PlayerHost.AudioTrackCount ?? 0;
    public bool IsPlaying => _backend?.PlayerHost.IsPlaying ?? false;
    public bool IsPaused => _backend?.PlayerHost.IsPaused ?? false;
    public int Volume => _backend?.PlayerHost.Volume ?? _desiredVolume;
    public float Rate => _backend?.PlayerHost.Rate ?? 1.0f;
    public int AudioTrack => _backend?.PlayerHost.AudioTrack ?? -1;
    public int SubtitleTrack => _backend?.PlayerHost.SubtitleTrack ?? -1;

    public async Task<IPlaybackMediaSource> CreateAsync(
        long generation,
        string filePath,
        string password,
        CancellationToken cancellationToken)
    {
        var backend = EnsureCreated();
        return await backend.MediaSourceFactory
            .CreateAsync(generation, filePath, password, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 由播放会话在首次用户加载的同步段执行幂等兜底。标准 UI 会更早在部署自检通过后、
    /// View 首次绑定前调用 Initialize；这里仍保留防线，避免测试宿主或未来非 UI 调用方
    /// 把 LibVLC/MediaPlayer 构造意外推迟到线程池。后续原生命令仍由 NativeDispatcher
    /// 串行执行，创建时序与命令时序各自只有一个责任主体。
    /// </summary>
    public void Initialize() => EnsureCreated();

    public void Attach(IPlaybackMediaSource source) => RequireHost().Attach(source);
    public void Detach() => _backend?.PlayerHost.Detach();
    public bool Play() => RequireHost().Play();
    public void Stop() => _backend?.PlayerHost.Stop();
    public void SetPause(bool paused) => RequireHost().SetPause(paused);
    public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken) =>
        RequireHost().PauseAtAsync(positionMs, cancellationToken);

    public void SetVolume(int volume)
    {
        _desiredVolume = Math.Clamp(volume, 0, 100);
        _backend?.PlayerHost.SetVolume(_desiredVolume);
    }

    public bool SetRate(float rate) => RequireHost().SetRate(rate);

    public IReadOnlyList<PlaybackTrackOption> GetAudioTracks() =>
        _backend?.PlayerHost.GetAudioTracks() ?? Array.Empty<PlaybackTrackOption>();

    public IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks() =>
        _backend?.PlayerHost.GetSubtitleTracks()
        ?? new[] { new PlaybackTrackOption(-1, "关闭字幕") };

    public bool SetAudioTrack(int trackId) => RequireHost().SetAudioTrack(trackId);

    public bool SetSubtitleTrack(int trackId) => RequireHost().SetSubtitleTrack(trackId);

    public Task SeekAsync(
        long positionMs,
        bool waitForFrame,
        CancellationToken cancellationToken) =>
        RequireHost().SeekAsync(positionMs, waitForFrame, cancellationToken);

    public Task<bool> RestoreSurfaceAsync(
        long positionMs,
        bool restorePaused,
        CancellationToken cancellationToken) =>
        RequireHost().RestoreSurfaceAsync(positionMs, restorePaused, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        PlaybackBackend? backend;
        lock (_syncRoot)
        {
            backend = _backend;
            _backend = null;
        }

        if (backend is not null)
        {
            Unsubscribe(backend.PlayerHost);
            backend.Dispose();
        }

        OutputChanged = null;
        StateChanged = null;
        PositionChanged = null;
        Failed = null;
    }

    private PlaybackBackend EnsureCreated()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_backend is not null)
        {
            return _backend;
        }

        PlaybackBackend created;
        lock (_syncRoot)
        {
            // CreateAsync 可能因快速换片从多个后台任务同时到达。双重检查确保每个
            // Document 最多创建一个 LibVLC/MediaPlayer；候选媒体仍可各自独立创建。
            if (_backend is not null)
            {
                return _backend;
            }

            created = _factory.Create();
            Subscribe(created.PlayerHost);
            try
            {
                // 页面可能先于 backend 设置音量。创建完成后先回放这项非敏感状态，
                // 再发布 OutputChanged；原生表面会在输出事件到达时自行重绑当前 HWND。
                created.PlayerHost.SetVolume(_desiredVolume);
                _backend = created;
            }
            catch
            {
                Unsubscribe(created.PlayerHost);
                created.Dispose();
                throw;
            }
        }

        OutputChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    private IPlaybackPlayerHost RequireHost() =>
        _backend?.PlayerHost
        ?? throw new InvalidOperationException("Playback backend has not been created.");

    private void Subscribe(IPlaybackPlayerHost host)
    {
        host.OutputChanged += ForwardOutputChanged;
        host.StateChanged += ForwardStateChanged;
        host.PositionChanged += ForwardPositionChanged;
        host.Failed += ForwardFailed;
    }

    private void Unsubscribe(IPlaybackPlayerHost host)
    {
        host.OutputChanged -= ForwardOutputChanged;
        host.StateChanged -= ForwardStateChanged;
        host.PositionChanged -= ForwardPositionChanged;
        host.Failed -= ForwardFailed;
    }

    private void ForwardStateChanged(long generation, PlaybackState state) =>
        StateChanged?.Invoke(generation, state);

    private void ForwardOutputChanged(object? sender, EventArgs e) =>
        OutputChanged?.Invoke(this, EventArgs.Empty);

    private void ForwardPositionChanged(long generation) =>
        PositionChanged?.Invoke(generation);

    private void ForwardFailed(long generation, PlaybackFailure failure) =>
        Failed?.Invoke(generation, failure);
}
