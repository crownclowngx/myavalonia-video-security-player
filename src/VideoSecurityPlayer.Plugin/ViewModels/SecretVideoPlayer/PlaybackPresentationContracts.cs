using System.ComponentModel;
using System.Windows.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 播放器控件可选消费的媒体库导航端口。
/// </summary>
/// <remarks>
/// 单文件播放器不提供该端口，因此复用播放器控件不需要伪造“上一项/下一项”能力。
/// 接口只包含展示状态和命令，不暴露密码、扫描器或媒体库内部集合。
/// </remarks>
public interface IPlaybackNavigationContext : INotifyPropertyChanged
{
    bool CanNavigatePrevious { get; }
    bool CanNavigateNext { get; }
    bool IsContinuousPlaybackEnabled { get; set; }
    ICommand PreviousCommand { get; }
    ICommand NextCommand { get; }
}

/// <summary>ViewModel 发给 Avalonia View 的全屏呈现请求。</summary>
public sealed class FullscreenPresentationRequestedEventArgs(
    long revision,
    bool enterFullscreen) : EventArgs
{
    public long Revision { get; } = revision;
    public bool EnterFullscreen { get; } = enterFullscreen;
}

/// <summary>当前媒体自然播放结束的代次通知。</summary>
public sealed class PlaybackMediaEndedEventArgs(long mediaGeneration) : EventArgs
{
    public long MediaGeneration { get; } = mediaGeneration;
}

/// <summary>一个新原生表面的异步绑定和恢复结果。</summary>
public sealed class VideoSurfaceAttachmentCompletedEventArgs(
    VideoSurfaceIdentity surface,
    PlaybackOperationResult result) : EventArgs
{
    public VideoSurfaceIdentity Surface { get; } = surface;
    public PlaybackOperationResult Result { get; } = result;
}
