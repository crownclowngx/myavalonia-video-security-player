using Avalonia.Controls;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>
/// 唯一原生视频表面视图。公开 Surface 是为了让顶层全屏呈现器迁移同一个 HWND，
/// 禁止普通模式和全屏模式各创建一份视频表面。
/// </summary>
public partial class PlaybackViewportView : UserControl
{
    public PlaybackViewportView()
    {
        InitializeComponent();
    }

    public EmbeddedVideoSurface Surface => VideoSurface;
}
