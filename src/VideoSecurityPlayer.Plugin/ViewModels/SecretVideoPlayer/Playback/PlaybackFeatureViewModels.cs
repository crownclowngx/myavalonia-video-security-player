using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>
/// 播放状态功能切片。
/// </summary>
/// <remarks>
/// G7.1 期间由兼容外壳继续保存唯一状态，本类型只为子 View 提供明确的功能入口。
/// 绑定通过 <see cref="Owner"/> 访问原状态，因此不会复制播放进度或制造第二事实源。
/// </remarks>
public sealed class PlaybackStateViewModel(PlaybackCoordinatorViewModel owner)
{
    public PlaybackCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>播放、暂停、停止、定位和音量控制的功能切片。</summary>
public sealed class PlaybackTransportViewModel(PlaybackCoordinatorViewModel owner)
{
    public PlaybackCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>倍速、音轨和字幕选择的功能切片。</summary>
public sealed class PlaybackOptionsViewModel(PlaybackCoordinatorViewModel owner)
{
    public PlaybackCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>媒体加载、历史定位、切换与清理的功能切片。</summary>
public sealed class PlaybackMediaViewModel(PlaybackCoordinatorViewModel owner)
{
    public PlaybackCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));

    public Task<bool> LoadAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken = default) =>
        Owner.LoadMediaAsync(filePath, password, cancellationToken);

    public Task CleanupAsync(CancellationToken cancellationToken = default) =>
        Owner.CleanupMediaAsync(cancellationToken);
}

/// <summary>部署诊断、原生输出、视频表面和全屏呈现的功能切片。</summary>
public sealed class PlaybackPresentationViewModel(PlaybackCoordinatorViewModel owner)
{
    public PlaybackCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));

    public PlaybackSnapshot Snapshot => Owner.PlaybackSnapshot;
}
