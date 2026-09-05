namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 视频表面重建后需要恢复的用户可见播放状态。
/// </summary>
public enum VideoSurfacePlaybackMode
{
    Playing,
    Paused
}

/// <summary>
/// 与某一次媒体和表面丢失事件绑定的恢复快照。
/// </summary>
public readonly record struct VideoSurfaceRecoveryRequest(
    long RequestId,
    long MediaGeneration,
    long PositionMs,
    VideoSurfacePlaybackMode PlaybackMode);

/// <summary>
/// 管理原生视频表面重建期间的恢复快照。
/// </summary>
/// <remarks>
/// 快照采用一次性消费语义。媒体切换、用户主动操作或普通停止都会使旧快照失效，
/// 只有为释放旧 HWND/vout 而执行的内部 Stop 才会保留快照。
/// </remarks>
public sealed class VideoSurfaceRecoveryPolicy
{
    private long _nextRequestId;
    private VideoSurfaceRecoveryRequest? _pendingRecovery;

    public bool HasPendingRecovery => _pendingRecovery.HasValue;

    /// <summary>
    /// 记录表面丢失前的用户可见状态。没有媒体或播放器已经停止时不创建恢复请求。
    /// </summary>
    public VideoSurfaceRecoveryRequest? OnSurfaceLost(
        long mediaGeneration,
        long positionMs,
        bool hasMedia,
        bool isPlaying,
        bool isPaused)
    {
        if (!hasMedia || (!isPlaying && !isPaused))
        {
            _pendingRecovery = null;
            return null;
        }

        var mode = isPlaying ? VideoSurfacePlaybackMode.Playing : VideoSurfacePlaybackMode.Paused;
        var request = new VideoSurfaceRecoveryRequest(
            ++_nextRequestId,
            mediaGeneration,
            Math.Max(0, positionMs),
            mode);
        _pendingRecovery = request;
        return request;
    }

    /// <summary>
    /// 在新表面就绪后消费一次恢复快照；媒体代次不匹配时丢弃过期请求。
    /// </summary>
    public VideoSurfaceRecoveryRequest? ConsumeRecovery(long mediaGeneration)
    {
        var request = _pendingRecovery;
        _pendingRecovery = null;
        if (request is null || request.Value.MediaGeneration != mediaGeneration)
        {
            return null;
        }

        return request;
    }

    /// <summary>
    /// 普通 Stop 必须取消恢复；表面重建使用的内部 Stop 不改变快照。
    /// </summary>
    public void OnPlaybackStopped(bool isSurfaceTransition)
    {
        if (!isSurfaceTransition)
        {
            Cancel();
        }
    }

    public void Cancel() => _pendingRecovery = null;
}
