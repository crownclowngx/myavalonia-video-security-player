using Avalonia.Controls;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

public partial class SecretVideoPlayerView : UserControl, IDisposable
{
    private bool _disposed;

    public SecretVideoPlayerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 最终关闭 Document 时释放复合播放器 View 持有的原生表面订阅。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        PlaybackControl.Dispose();
        Content = null;
        GC.SuppressFinalize(this);
    }
}
