using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

public readonly record struct PlaybackResourceSnapshot(
    int LiveLeases,
    int LivePlayers,
    int LiveMediaInputs,
    int LiveEncryptedStreams,
    int ActiveSurfaceRestores,
    int CachedPlaintextChunks,
    int LiveNativeDispatchers,
    int LiveResourceReapers);

public static class SecurePlaybackDiagnostics
{
    public static PlaybackResourceSnapshot CaptureResources()
    {
        var playback = PlaybackResourceDiagnostics.Capture();
        var streams = EncryptedStreamResourceDiagnostics.Capture();
        return playback with
        {
            LiveEncryptedStreams = streams.LiveStreams,
            CachedPlaintextChunks = streams.CachedPlaintextChunks
        };
    }

    public static IReadOnlyList<long> CaptureRecentChunkReads() =>
        EncryptedStreamResourceDiagnostics.CaptureRecentChunkReads();

    public static void ClearRecentChunkReads() =>
        EncryptedStreamResourceDiagnostics.ClearRecentChunkReads();

    public static PlaybackCacheStatistics CaptureCacheStatistics() =>
        EncryptedStreamResourceDiagnostics.CaptureCacheStatistics();

    /// <summary>仅供隔离的测试和性能场景建立本轮零点。</summary>
    public static void ResetCacheStatistics() =>
        EncryptedStreamResourceDiagnostics.ResetCacheStatistics();

    internal static int CaptureRetainedEncryptedStreamCount() =>
        EncryptedStreamResourceDiagnostics.CaptureRetainedStreamCount();
}

internal static class PlaybackResourceDiagnostics
{
    private static int _liveLeases;
    private static int _livePlayers;
    private static int _liveMediaInputs;
    private static int _activeSurfaceRestores;
    private static int _liveNativeDispatchers;
    private static int _liveResourceReapers;

    public static PlaybackResourceSnapshot Capture() => new(
        Volatile.Read(ref _liveLeases),
        Volatile.Read(ref _livePlayers),
        Volatile.Read(ref _liveMediaInputs),
        0,
        Volatile.Read(ref _activeSurfaceRestores),
        0,
        Volatile.Read(ref _liveNativeDispatchers),
        Volatile.Read(ref _liveResourceReapers));

    public static void LeaseCreated() => Interlocked.Increment(ref _liveLeases);
    public static void LeaseDisposed() => Interlocked.Decrement(ref _liveLeases);
    public static void PlayerCreated() => Interlocked.Increment(ref _livePlayers);
    public static void PlayerDisposed() => Interlocked.Decrement(ref _livePlayers);
    public static void InputCreated() => Interlocked.Increment(ref _liveMediaInputs);
    public static void InputDisposed() => Interlocked.Decrement(ref _liveMediaInputs);
    public static void SurfaceRestoreStarted() => Interlocked.Increment(ref _activeSurfaceRestores);
    public static void SurfaceRestoreFinished() => Interlocked.Decrement(ref _activeSurfaceRestores);
    public static void NativeDispatcherCreated() =>
        Interlocked.Increment(ref _liveNativeDispatchers);
    public static void NativeDispatcherDisposed() =>
        Interlocked.Decrement(ref _liveNativeDispatchers);
    public static void ResourceReaperCreated() =>
        Interlocked.Increment(ref _liveResourceReapers);
    public static void ResourceReaperDisposed() =>
        Interlocked.Decrement(ref _liveResourceReapers);
}

internal sealed class PlaybackOperationException(
    PlaybackFailure failure,
    Exception? innerException = null) : Exception(failure.Message, innerException)
{
    public PlaybackFailure Failure { get; } = failure;
}

/// <summary>
/// 单个 SECVID03 视频的资源所有权边界。
/// </summary>
/// <remarks>
/// Source 不控制 MediaPlayer，也不知道 HWND。它只拥有随媒体切换而变化的 Media、
/// MediaInput、解密流、文件句柄、派生密钥和固定大小明文缓存，因此只有从 PlayerHost
/// 解绑以后才允许交给后台回收器。这种拆分落实了单一职责，并消除了旧 Lease
/// 同时拥有播放器和文件资源所造成的“换视频等于重建播放器”。
/// </remarks>
internal interface IPlaybackMediaSource : IDisposable
{
    long Generation { get; }
    Media NativeMedia { get; }
    PlaybackMediaIdentity? Identity => null;
    Secvid03DiagnosticSummary? DiagnosticSummary => null;
    event Action<IPlaybackMediaSource, PlaybackFailure>? Failed;
    void PrepareForPlayback();
    void RequestStop();
}

/// <summary>
/// 在不修改当前播放器的前提下，认证并解析候选媒体。
/// </summary>
internal interface IPlaybackMediaSourceFactory
{
    Task<IPlaybackMediaSource> CreateAsync(
        long generation,
        string filePath,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>
/// Document 生命周期内唯一的 LibVLC/MediaPlayer 主机。
/// </summary>
/// <remarks>
/// Host 只负责稳定的原生播放器和输出表面；它不打开 SECVID03 文件，也不决定
/// 用户意图。接口使编排层依赖抽象，同时确保 MediaPlayer 的创建数量可单独测试。
/// </remarks>
internal interface IPlaybackPlayerHost : IDisposable, IPlaybackOutputLifecycle
{
    MediaPlayer? NativePlayer { get; }
    long NativeOutputGeneration { get; }
    long PositionMs { get; }
    long DurationMs { get; }
    bool IsSeekable { get; }
    bool HasVideo { get; }
    bool HasAudio { get; }
    int VideoTrackCount { get; }
    int AudioTrackCount { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    int Volume { get; }
    float Rate { get; }
    int AudioTrack { get; }
    int SubtitleTrack { get; }

    event Action<long, PlaybackState>? StateChanged;
    event Action<long>? PositionChanged;
    event Action<long, PlaybackFailure>? Failed;

    void Attach(IPlaybackMediaSource source);
    void Detach();
    bool Play();
    void Stop();
    void SetPause(bool paused);
    Task PauseAtAsync(long positionMs, CancellationToken cancellationToken);
    void SetVolume(int volume);
    bool SetRate(float rate);
    IReadOnlyList<PlaybackTrackOption> GetAudioTracks();
    IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks();
    bool SetAudioTrack(int trackId);
    bool SetSubtitleTrack(int trackId);
    Task SeekAsync(long positionMs, bool waitForFrame, CancellationToken cancellationToken);
    Task<bool> RestoreSurfaceAsync(
        long positionMs,
        bool restorePaused,
        CancellationToken cancellationToken);
}

/// <summary>
/// 生产环境 PlayerHost：一个 Document 创建一次，关闭 Document 时释放一次。
/// </summary>
internal sealed class LibVlcDocumentPlayerHost : IPlaybackPlayerHost
{
    private static long _nextNativeOutputGeneration;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private IPlaybackMediaSource? _source;
    private NativeEventSubscription? _events;
    private int _disposeState;

    public LibVlcDocumentPlayerHost(IPlaybackRuntimeInitializer runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.EnsureInitialized();
        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        NativeOutputGeneration = Interlocked.Increment(ref _nextNativeOutputGeneration);
        PlaybackResourceDiagnostics.PlayerCreated();
    }

    internal LibVLC LibVlc => _libVlc;
    // 此实现构造时已完成初始化，输出在整个 Document 内保持不变，因此没有后续输出通知。
    public event EventHandler? OutputChanged { add { } remove { } }
    public void Initialize() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    public MediaPlayer NativePlayer => _player;
    public long NativeOutputGeneration { get; }
    public long PositionMs => Math.Max(0, _player.Time);
    public long DurationMs => Math.Max(0, _player.Length);
    public bool IsSeekable => _player.IsSeekable;
    public bool HasVideo => _player.VideoTrackCount > 0;
    public bool HasAudio => _player.AudioTrackCount > 0;
    public int VideoTrackCount => _player.VideoTrackCount;
    public int AudioTrackCount => _player.AudioTrackCount;
    public bool IsPlaying => _player.IsPlaying || _player.State == VLCState.Playing;
    public bool IsPaused => _player.State == VLCState.Paused;
    public int Volume => _player.Volume;
    public float Rate => _player.Rate;
    public int AudioTrack => _player.AudioTrack;
    public int SubtitleTrack => _player.Spu;

    public event Action<long, PlaybackState>? StateChanged;
    public event Action<long>? PositionChanged;
    public event Action<long, PlaybackFailure>? Failed;

    public void Attach(IPlaybackMediaSource source)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null)
        {
            throw new InvalidOperationException("A media source is already attached.");
        }

        // Media setter 必须由 NativeDispatcher 调用。先完成原生绑定再更新托管字段，
        // 可保证 setter 抛异常时 Host 仍保持“没有挂载 Source”的一致状态。
        _player.Media = source.NativeMedia;
        _source = source;
        _events = new NativeEventSubscription(this, _player, source.Generation);
    }

    public void Detach()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        // 必须先退订带代次的事件，再清空 Media。否则清空过程中产生的 Stopped
        // 回调可能以旧代次覆盖刚刚发布的新媒体状态。
        _events?.Dispose();
        _events = null;
        _player.Media = null;
        _source = null;
    }

    public bool Play()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return _player.Play();
    }

    public void Stop()
    {
        if (Volatile.Read(ref _disposeState) == 0 && _source is not null)
        {
            _player.Stop();
        }
    }

    public void SetPause(bool paused)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        _player.SetPause(paused);
    }

    public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

        // SECVID03 通过回调流向 LibVLC 提供数据。部分 demux 在收到暂停后会异步把
        // MediaPlayer.Time 短暂重置为零，所以“发出 SetPause”并不等于“暂停位置已经
        // 稳定”。复用表面恢复中经过真实媒体门禁验证的序列：等待原生 Paused 事件，
        // 再于暂停态重申调用前的位置。原生调用仍由上层 NativeDispatcher 串行化，
        // 本对象只负责一个 MediaPlayer 的原生语义，不承担文档或 UI 状态发布。
        return new LibVlcVideoSurfaceRestoreOperations(_player)
            .PauseAtAsync(positionMs, cancellationToken);
    }

    public void SetVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        _player.Volume = Math.Clamp(volume, 0, 100);
    }

    public bool SetRate(float rate)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return _player.SetRate(rate) == 0;
    }

    public IReadOnlyList<PlaybackTrackOption> GetAudioTracks()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return NormalizeTracks(_player.AudioTrackDescription, "音轨", includeSubtitleOff: false);
    }

    public IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return NormalizeTracks(_player.SpuDescription, "字幕", includeSubtitleOff: true);
    }

    public bool SetAudioTrack(int trackId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return _player.SetAudioTrack(trackId);
    }

    public bool SetSubtitleTrack(int trackId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return _player.SetSpu(trackId);
    }

    public async Task SeekAsync(
        long positionMs,
        bool waitForFrame,
        CancellationToken cancellationToken)
    {
        var operations = new LibVlcVideoSurfaceRestoreOperations(_player);
        if (waitForFrame && IsPaused)
        {
            var restored = await VideoSurfaceRestoreSequence
                .ExecuteAsync(operations, positionMs, restorePaused: true, cancellationToken)
                .ConfigureAwait(false);
            if (!restored)
            {
                throw new PlaybackOperationException(new PlaybackFailure(
                    PlaybackFailureCode.DecodeFailed,
                    "The media decoder could not resume for seeking."));
            }
            return;
        }

        await operations.SeekAsync(positionMs, waitForFrame, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> RestoreSurfaceAsync(
        long positionMs,
        bool restorePaused,
        CancellationToken cancellationToken) =>
        VideoSurfaceRestoreSequence.ExecuteAsync(
            new LibVlcVideoSurfaceRestoreOperations(_player),
            positionMs,
            restorePaused,
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                _player.Stop();
            }
            catch
            {
            }

            _events?.Dispose();
            _events = null;
            _player.Media = null;
            _source = null;
            _player.Dispose();
            PlaybackResourceDiagnostics.PlayerDisposed();
            _libVlc.Dispose();
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
        }
    }

    private static IReadOnlyList<PlaybackTrackOption> NormalizeTracks(
        IEnumerable<LibVLCSharp.Shared.Structures.TrackDescription>? descriptions,
        string fallbackPrefix,
        bool includeSubtitleOff)
    {
        var result = new List<PlaybackTrackOption>();
        if (includeSubtitleOff)
        {
            result.Add(new PlaybackTrackOption(-1, "关闭字幕"));
        }

        if (descriptions is null)
        {
            return result.ToArray();
        }

        var displayIndex = 1;
        foreach (var description in descriptions)
        {
            // 音轨描述中的 -1 通常表示“禁用”。产品已有音量/静音入口，因此音轨列表不重复
            // 暴露该伪轨道；字幕则由上面的稳定“关闭字幕”选项统一表达。
            if (description.Id < 0)
            {
                continue;
            }

            result.Add(new PlaybackTrackOption(
                description.Id,
                PlaybackTrackNamePolicy.Sanitize(
                    description.Name,
                    fallbackPrefix,
                    displayIndex)));
            displayIndex++;
        }

        return result.ToArray();
    }

    private sealed class NativeEventSubscription : IDisposable
    {
        private readonly LibVlcDocumentPlayerHost _owner;
        private readonly MediaPlayer _player;
        private readonly long _generation;
        private int _disposed;

        public NativeEventSubscription(
            LibVlcDocumentPlayerHost owner,
            MediaPlayer player,
            long generation)
        {
            // 每次 Attach 建立一组捕获 generation 的订阅。即便 LibVLC 延迟投递旧事件，
            // SecureVideoPlayer 也能按 generation 丢弃它，而不是信任事件到达顺序。
            _owner = owner;
            _player = player;
            _generation = generation;
            _player.Playing += OnPlaying;
            _player.Paused += OnPaused;
            _player.Stopped += OnStopped;
            _player.EndReached += OnEnded;
            _player.TimeChanged += OnPositionChanged;
            _player.PositionChanged += OnPositionChanged;
            _player.LengthChanged += OnPositionChanged;
            _player.SeekableChanged += OnPositionChanged;
            _player.EncounteredError += OnEncounteredError;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _player.Playing -= OnPlaying;
            _player.Paused -= OnPaused;
            _player.Stopped -= OnStopped;
            _player.EndReached -= OnEnded;
            _player.TimeChanged -= OnPositionChanged;
            _player.PositionChanged -= OnPositionChanged;
            _player.LengthChanged -= OnPositionChanged;
            _player.SeekableChanged -= OnPositionChanged;
            _player.EncounteredError -= OnEncounteredError;
        }

        private void OnPlaying(object? sender, EventArgs e) =>
            _owner.StateChanged?.Invoke(_generation, PlaybackState.Playing);

        private void OnPaused(object? sender, EventArgs e) =>
            _owner.StateChanged?.Invoke(_generation, PlaybackState.Paused);

        private void OnStopped(object? sender, EventArgs e)
        {
            var state = _owner.DurationMs > 0 &&
                        _owner.PositionMs >= Math.Max(0, _owner.DurationMs - 500)
                ? PlaybackState.Ended
                : PlaybackState.Stopped;
            _owner.StateChanged?.Invoke(_generation, state);
        }

        private void OnEnded(object? sender, EventArgs e) =>
            _owner.StateChanged?.Invoke(_generation, PlaybackState.Ended);

        private void OnPositionChanged(object? sender, EventArgs e) =>
            _owner.PositionChanged?.Invoke(_generation);

        private void OnEncounteredError(object? sender, EventArgs e) =>
            _owner.Failed?.Invoke(_generation, PlaybackFailureMapper.DecodeFailed());
    }
}

/// <summary>
/// SECVID03 候选媒体工厂。调用方负责把整个 CreateAsync 放到后台线程，
/// 因为 Open 中的密钥派生和 LibVLC Parse 都可能是耗时操作。
/// </summary>
internal sealed class LibVlcPlaybackMediaSourceFactory(
    LibVlcDocumentPlayerHost playerHost) : IPlaybackMediaSourceFactory
{
    public async Task<IPlaybackMediaSource> CreateAsync(
        long generation,
        string filePath,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrEmpty(password))
        {
            throw new PlaybackOperationException(
                new PlaybackFailure(
                    PlaybackFailureCode.InvalidRequest,
                    "文件路径和密码不能为空。"));
        }

        SeekableEncryptedVideoStream? stream = null;
        SeekableStreamMediaInput? input = null;
        Media? media = null;
        try
        {
            // 资源按所有权转移构建：直到 Source 成功创建前，finally 始终负责清理；
            // Source 创建成功后将 input/media 置空，避免工厂与 Source 双重 Dispose。
            stream = SeekableEncryptedVideoStream.Open(filePath, password);
            input = new SeekableStreamMediaInput(stream);
            // 从这里开始 Input 接管 Stream；工厂 finally 只需释放仍由自己持有的对象。
            stream = null;
            media = new Media(playerHost.LibVlc, input);

            var parsedStatus = await media
                .Parse(MediaParseOptions.ParseNetwork, 15_000, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (parsedStatus is MediaParsedStatus.Failed or MediaParsedStatus.Timeout)
            {
                if (input.TryTakeLastFailure(out var inputFailure) && inputFailure is not null)
                {
                    throw new PlaybackOperationException(inputFailure);
                }

                throw new PlaybackOperationException(PlaybackFailureMapper.ParseFailed());
            }

            if (parsedStatus == MediaParsedStatus.Skipped &&
                input.TryTakeLastFailure(out var skippedFailure) &&
                skippedFailure is not null)
            {
                throw new PlaybackOperationException(skippedFailure);
            }

            var source = new LibVlcPlaybackMediaSource(generation, input, media);
            input = null;
            media = null;
            return source;
        }
        catch (PlaybackOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlaybackOperationException(PlaybackFailureMapper.MapLoad(ex), ex);
        }
        finally
        {
            media?.Dispose();
            input?.Dispose();
            stream?.Dispose();
        }
    }
}

/// <summary>
/// 生产环境的单媒体资源聚合根。
/// </summary>
internal sealed class LibVlcPlaybackMediaSource : IPlaybackMediaSource
{
    private readonly SeekableStreamMediaInput _input;
    private readonly Media _media;
    private int _disposeState;

    public LibVlcPlaybackMediaSource(
        long generation,
        SeekableStreamMediaInput input,
        Media media)
    {
        Generation = generation;
        _input = input;
        _media = media;
        _input.Failed += OnInputFailed;
        PlaybackResourceDiagnostics.LeaseCreated();
        PlaybackResourceDiagnostics.InputCreated();
    }

    public long Generation { get; }
    public Media NativeMedia => _media;
    public PlaybackMediaIdentity? Identity => _input.MediaIdentity;
    public Secvid03DiagnosticSummary? DiagnosticSummary => _input.DiagnosticSummary;
    public event Action<IPlaybackMediaSource, PlaybackFailure>? Failed;

    public void PrepareForPlayback() => _input.PrepareForPlayback();
    public void RequestStop() => _input.RequestStop();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            // 顺序不可交换：Media 可能仍持有 MediaInput 回调；先终止新读取并释放
            // Media，再释放 Input，最终由 Input 关闭加密流、文件、缓存和密钥上下文。
            _input.RequestStop();
            _input.Failed -= OnInputFailed;
            _media.Dispose();
            _input.Dispose();
            PlaybackResourceDiagnostics.InputDisposed();
        }
        finally
        {
            PlaybackResourceDiagnostics.LeaseDisposed();
            Failed = null;
            Volatile.Write(ref _disposeState, 2);
        }
    }

    private void OnInputFailed(PlaybackFailure failure) =>
        Failed?.Invoke(this, failure);
}
