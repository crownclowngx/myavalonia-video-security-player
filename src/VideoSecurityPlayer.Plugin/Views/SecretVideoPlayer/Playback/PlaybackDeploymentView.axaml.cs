using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>播放器部署诊断视图；只展示稳定问题码、路径和恢复建议。</summary>
public partial class PlaybackDeploymentView : UserControl
{
    private void ConfigureRuntime(object? sender, RoutedEventArgs e) =>
        this.GetVisualAncestors().OfType<VideoPlayerControl>().FirstOrDefault()?.ShowSettings(true);
    public PlaybackDeploymentView()
    {
        InitializeComponent();
    }
}
