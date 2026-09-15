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
        LibrarySplitView.PropertyChanged += (_, change) =>
        { if (change.Property == SplitView.OpenPaneLengthProperty) UpdatePaneMode(); };
    }

    private void OnLibraryViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePaneMode();
    }

    /// <summary>用户调宽侧栏后也要重新判断并排空间，窄文档继续使用可收起的覆盖侧栏。</summary>
    private void UpdatePaneMode() => LibrarySplitView.DisplayMode =
        Bounds.Width >= Math.Max(InlinePaneMinimumWidth, LibrarySplitView.OpenPaneLength + 480)
            ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;

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
