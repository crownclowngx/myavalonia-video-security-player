namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>播放器对界面公开的稳定生命周期状态。</summary>
public enum PlaybackState
{
    Empty,
    Ready,
    Stopped,
    Playing,
    Paused,
    Ended,
    Faulted,
    Disposed
}

/// <summary>
/// 描述播放器当前正在执行的非稳定活动。
/// <see cref="PlaybackState"/> 表示已经提交的播放状态，本枚举则用于告诉界面
/// “为什么当前命令需要等待”，避免把耗时的原生 Stop 或媒体切换表现成 UI 假死。
/// </summary>
public enum PlaybackActivity
{
    Idle,
    PreparingCandidate,
    WaitingForPlayer,
    StoppingCurrent,
    AttachingCandidate,
    StartingPlayback,
    Stopping,
    ReleasingOldMedia
}

/// <summary>播放器失败的稳定分类。用户界面不得根据异常文本推断失败原因。</summary>
public enum PlaybackFailureCode
{
    InvalidRequest,
    InvalidFormat,
    AuthenticationFailed,
    CorruptedContent,
    InputUnavailable,
    DeploymentUnavailable,
    ParseFailed,
    DecodeFailed,
    SurfaceRestoreFailed,
    ControlUnavailable,
    Cancelled,
    Unknown
}

/// <summary>可公开到界面和脱敏验收报告的播放失败。</summary>
public sealed record PlaybackFailure(
    PlaybackFailureCode Code,
    string Message,
    string? SuggestedAction = null,
    string? DiagnosticCode = null);

/// <summary>密码认证成功后从固定头取得的非敏感媒体身份。</summary>
public sealed record PlaybackMediaIdentity(string FileId, long OriginalFileLength);

/// <summary>播放操作的统一返回值。</summary>
public readonly record struct PlaybackOperationResult(
    bool Success,
    PlaybackFailure? Failure = null)
{
    public static PlaybackOperationResult Succeeded() => new(true);

    public static PlaybackOperationResult Failed(PlaybackFailure failure) =>
        new(false, failure ?? throw new ArgumentNullException(nameof(failure)));
}

/// <summary>
/// 一次原生视频表面的单调递增身份。
/// </summary>
/// <remarks>
/// 身份刻意不保存 HWND。业务层只用代次拒绝迟到的恢复结果，真实句柄始终留在
/// Windows 视频表面适配器内部。
/// </remarks>
public readonly record struct VideoSurfaceIdentity(long Generation)
{
    public bool IsValid => Generation > 0;
}

/// <summary>
/// 当前媒体的一条可选轨道。
/// </summary>
/// <remarks>
/// ID 只在当前媒体代次内有效，不能持久化，也不能跨媒体复用。
/// DisplayName 只用于界面展示；PlayerHost 在创建本对象前必须移除控制字符并限制长度，
/// 防止媒体内嵌元数据破坏布局或进入后续诊断文本。
/// </remarks>
public sealed record PlaybackTrackOption(int Id, string DisplayName);

/// <summary>
/// 把不可信的容器轨道名称转换为只用于展示的短文本。
/// </summary>
/// <remarks>
/// 轨道名称来自媒体容器，不能用于身份判断、路径或日志。策略独立于 LibVLC 类型，
/// 便于用纯单元测试锁定控制字符、空名称和 Unicode Rune 上限。
/// </remarks>
internal static class PlaybackTrackNamePolicy
{
    public static string Sanitize(
        string? value,
        string fallbackPrefix,
        int displayIndex)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{fallbackPrefix} {displayIndex}";
        }

        var builder = new System.Text.StringBuilder();
        var runeCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (runeCount >= 128)
            {
                break;
            }

            if (!System.Text.Rune.IsControl(rune))
            {
                builder.Append(rune.ToString());
                runeCount++;
            }
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0
            ? $"{fallbackPrefix} {displayIndex}"
            : normalized;
    }
}

/// <summary>
/// 与高频位置状态分离的播放器日常控制快照。
/// </summary>
/// <remarks>
/// 位置事件可能每 100 ms 发布一次，因此轨道集合必须作为不可变引用复用，
/// 只有媒体提交、轨道刷新或用户控制成功时才创建新实例，避免播放期间持续分配集合。
/// </remarks>
public sealed record PlaybackControlSnapshot(
    float Rate,
    IReadOnlyList<PlaybackTrackOption> AudioTracks,
    int? SelectedAudioTrackId,
    IReadOnlyList<PlaybackTrackOption> SubtitleTracks,
    int? SelectedSubtitleTrackId)
{
    public static PlaybackControlSnapshot Empty { get; } = new(
        1.0f,
        Array.Empty<PlaybackTrackOption>(),
        null,
        Array.Empty<PlaybackTrackOption>(),
        null);
}

/// <summary>播放会话当前的原子只读快照。</summary>
public sealed record PlaybackSnapshot(
    long MediaGeneration,
    PlaybackState State,
    bool IsTransitioning,
    long PositionMs,
    long DurationMs,
    bool IsSeekable,
    bool HasMedia,
    long SurfaceGeneration,
    int Volume,
    bool HasVideo,
    bool HasAudio,
    int VideoTrackCount,
    int AudioTrackCount,
    PlaybackControlSnapshot Controls,
    PlaybackActivity Activity,
    PlaybackMediaIdentity? MediaIdentity = null)
{
    public static PlaybackSnapshot Empty { get; } = new(
        0,
        PlaybackState.Empty,
        false,
        0,
        0,
        false,
        false,
        0,
        50,
        false,
        false,
        0,
        0,
        PlaybackControlSnapshot.Empty,
        PlaybackActivity.Idle,
        null);
}

/// <summary>携带媒体代次的统一播放通知。</summary>
public sealed class PlaybackChangedEventArgs(
    PlaybackSnapshot snapshot,
    PlaybackFailure? failure = null) : EventArgs
{
    public PlaybackSnapshot Snapshot { get; } =
        snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public PlaybackFailure? Failure { get; } = failure;
}

/// <summary>
/// ViewModel 使用的窄播放端口。密码只存在于 LoadAsync 调用参数和同步调用链中。
/// </summary>
public interface ISecureVideoPlaybackSession : IDisposable
{
    event EventHandler<PlaybackChangedEventArgs>? Changed;

    PlaybackSnapshot Snapshot { get; }

    /// <summary>
    /// 在首次媒体加载前设置当前 Document 的初始音量和倍速。
    /// </summary>
    /// <remarks>
    /// 该入口只初始化会话内偏好，不启动原生播放器，也不把存储职责引入播放服务。
    /// </remarks>
    void ApplyInitialPreferences(int volume, float rate)
    {
        SetVolume(volume);
    }

    Task<PlaybackOperationResult> LoadAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在同一媒体切换事务中加载并定位，但不启动播放。
    /// </summary>
    /// <remarks>
    /// 默认实现仅用于兼容既有测试替身；生产实现必须覆盖该方法，把提交和 Seek 放在同一个
    /// 操作门内，避免两次调用之间被新的播放意图插入。
    /// </remarks>
    async Task<PlaybackOperationResult> LoadAtPositionAsync(
        string filePath,
        string password,
        long positionMs,
        PlaybackMediaIdentity? expectedIdentity = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(filePath, password, cancellationToken);
        if (!loaded.Success || positionMs <= 0)
            return loaded;
        if (expectedIdentity is not null && Snapshot.MediaIdentity != expectedIdentity)
            return loaded;
        return await SeekAsync(positionMs, waitForFrame: false, cancellationToken);
    }

    /// <summary>
    /// 在同一次媒体切换事务中完成候选验证、身份复核、历史定位和启动播放。
    /// </summary>
    /// <remarks>
    /// 这是“用户明确激活媒体”的组合用例。生产实现必须覆盖此方法，并让提交、Seek 和 Play
    /// 共享同一个操作门与媒体代次；否则若 ViewModel 顺序调用三个公开方法，Stop 或新的
    /// Load 可能插入中间状态，让已经过期的双击请求错误地启动另一段媒体。
    ///
    /// 默认实现只用于兼容轻量测试替身。它仍会先校验已认证媒体身份，再尝试播放，但不承诺
    /// 跨调用原子性，不能作为生产播放会话的实现方案。
    /// </remarks>
    async Task<PlaybackOperationResult> LoadAtPositionAndPlayAsync(
        string filePath,
        string password,
        long positionMs,
        PlaybackMediaIdentity? expectedIdentity = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAtPositionAsync(
            filePath,
            password,
            positionMs,
            expectedIdentity,
            cancellationToken);
        return loaded.Success
            ? await PlayAsync(cancellationToken)
            : loaded;
    }

    /// <summary>
    /// 在同一次媒体切换事务中完成候选验证、提交和启动播放。
    /// 该组合入口避免 ViewModel 在 Load 与 Play 之间观察到可被其他操作插入的中间状态。
    /// </summary>
    Task<PlaybackOperationResult> LoadAndPlayAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> PlayAsync(CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> PauseAsync(CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> StopAsync(CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> SeekAsync(
        long positionMs,
        bool waitForFrame = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在会话操作门内基于执行时的真实位置进行相对定位。
    /// 该入口专门避免连续快捷键请求都基于同一份过期 UI 快照计算目标。
    /// </summary>
    Task<PlaybackOperationResult> SeekRelativeAsync(
        long deltaMs,
        CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> SetRateAsync(
        float rate,
        CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> SelectAudioTrackAsync(
        int trackId,
        CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> SelectSubtitleTrackAsync(
        int trackId,
        CancellationToken cancellationToken = default);

    Task<PlaybackOperationResult> ReleaseAsync(CancellationToken cancellationToken = default);

    bool SetVolume(int volume);

}

/// <summary>
/// View 与原生表面使用的不透明视频输出。
/// </summary>
/// <remarks>
/// 输出代次用于验收“同一 Document 不重建播放器”，不允许通过本接口取得
/// LibVLC 或 MediaPlayer。Windows 适配器通过程序集内部端口访问具体原生对象。
/// </remarks>
public interface IPlaybackVideoOutput
{
    event EventHandler? OutputChanged;

    long Generation { get; }
}

/// <summary>
/// 原生表面生命周期使用的语义化播放端口。
/// </summary>
public interface IPlaybackSurfaceSession
{
    IPlaybackVideoOutput VideoOutput { get; }

    /// <summary>
    /// 在 NativeControlHost 销毁原生表面前同步保存一次性恢复快照并请求输入停止。
    /// 可能阻塞的原生 Stop 由会话内部串行调度；新表面恢复必须等待它完成。
    /// </summary>
    void DetachSurface(VideoSurfaceIdentity surface);

    /// <summary>在新表面完成原生绑定后恢复播放或暂停状态。</summary>
    Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(
        VideoSurfaceIdentity surface,
        CancellationToken cancellationToken = default);
}
