using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

public partial class SecretVideoLibraryView : UserControl, IDisposable
{
    private const double InlinePaneMinimumWidth = 960;
    private bool _disposed;

    public SecretVideoLibraryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        SizeChanged += OnLibraryViewSizeChanged;
    }

    private void OnLibraryViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 400px 的紧凑侧栏在窄 Document 中若继续 Inline，会把原生视频表面压缩到几乎不可用。
        // 低于阈值时只改变 SplitView 的呈现方式，不修改持久化的开关状态；窗口再次变宽后
        // 自动回到并排布局，业务 ViewModel 因而无需感知像素尺寸或宿主窗口结构。
        LibrarySplitView.DisplayMode = e.NewSize.Width >= InlinePaneMinimumWidth
            ? SplitViewDisplayMode.CompactInline
            : SplitViewDisplayMode.CompactOverlay;
    }

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not SecretVideoLibraryViewModel viewModel)
            return;
        try
        {
            await viewModel.InitializeAsync();
        }
        catch
        {
            if (ReferenceEquals(DataContext, viewModel))
                viewModel.StatusMessage = "恢复最近媒体目录失败，请重新选择文件夹";
        }
    }

    /// <summary>
    /// 最终关闭媒体库 Document 时释放播放器表面和 View 级事件。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        SizeChanged -= OnLibraryViewSizeChanged;
        PlaybackControl.Dispose();
        Content = null;
        GC.SuppressFinalize(this);
    }

}
