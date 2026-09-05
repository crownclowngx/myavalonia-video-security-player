using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

/// <summary>
/// 视频播放器控件
/// 独立的播放器组件，可以在不同的视图中复用
/// </summary>
public partial class VideoPlayerControl : UserControl, IDisposable
{
    public static readonly StyledProperty<IPlaybackNavigationContext?> NavigationContextProperty =
        AvaloniaProperty.Register<VideoPlayerControl, IPlaybackNavigationContext?>(
            nameof(NavigationContext));

    private VideoPlayerControlViewModel? _boundViewModel;
    private readonly PlaybackSurfaceCoordinator _surfaceCoordinator;
    private readonly FullscreenPlaybackPresenter _fullscreenPresenter;
    private bool _hasNavigationContext;
    private bool _isDiagnosticPickerOpen;
    private bool _disposed;

    public IPlaybackNavigationContext? NavigationContext
    {
        get => GetValue(NavigationContextProperty);
        set => SetValue(NavigationContextProperty, value);
    }

    public bool HasNavigationContext
    {
        get => _hasNavigationContext;
        private set => SetAndRaise(
            HasNavigationContextProperty,
            ref _hasNavigationContext,
            value);
    }

    /// <summary>
    /// 播放器控件的 ViewModel
    /// </summary>
    public VideoPlayerControlViewModel? ViewModel => DataContext as VideoPlayerControlViewModel;

    public VideoPlayerControl()
    {
        InitializeComponent();
        _surfaceCoordinator = new PlaybackSurfaceCoordinator(Viewport.Surface);
        _fullscreenPresenter = new FullscreenPlaybackPresenter(
            this,
            NormalPlaceholder,
            PlayerShell,
            _surfaceCoordinator,
            () => _boundViewModel);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        AddHandler(KeyDownEvent, OnPlayerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPlayerPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NavigationContextProperty)
        {
            HasNavigationContext = change.NewValue is IPlaybackNavigationContext;
        }
    }

    // HasNavigationContext 只用于 XAML 显隐；DirectProperty 让 NavigationContext 改变时
    // 无需把呈现端口塞进 VideoPlayerControlViewModel。
    public static readonly DirectProperty<VideoPlayerControl, bool> HasNavigationContextProperty =
        AvaloniaProperty.RegisterDirect<VideoPlayerControl, bool>(
            nameof(HasNavigationContext),
            control => control.HasNavigationContext);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_boundViewModel, DataContext))
        {
            return;
        }

        // 控件回收给另一个文档时，先通知旧 ViewModel 表面已经不可用，
        // 再把当前 HWND 状态交给新的 ViewModel，避免两个播放器同时认为自己拥有同一个输出窗口。
        if (_boundViewModel is not null)
        {
            _fullscreenPresenter.ForceReset();
            _boundViewModel.ResetFullscreenPresentation();
            _boundViewModel.FullscreenPresentationRequested -= OnFullscreenPresentationRequested;
        }
        _surfaceCoordinator.Bind(null);

        // 原生输出不使用 XAML 继承 DataContext 绑定。协调器会按“旧会话同步分离、
        // 清空旧输出、绑定新输出、恢复当前表面”的严格顺序切换 Document。
        _boundViewModel = DataContext as VideoPlayerControlViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.FullscreenPresentationRequested += OnFullscreenPresentationRequested;
        }
        _surfaceCoordinator.Bind(_boundViewModel?.SurfaceSession);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Dock 重新展示 View 时，正常占位容器必须重新拥有唯一 PlayerShell。
        if (NormalPlaceholder.Content is null && PlayerShell.Parent is null)
        {
            NormalPlaceholder.Content = PlayerShell;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Dock 切出或文档关闭时不能把 Shell 留在 TopLevel 覆盖层，否则不可见 Document
        // 仍会截获输入并持有 HWND。这里执行纯视觉幂等回收，表面事件负责同步停止 vout。
        _fullscreenPresenter.ForceReset();
        _boundViewModel?.ResetFullscreenPresentation();
    }

    private async void OnFullscreenPresentationRequested(
        object? sender,
        FullscreenPresentationRequestedEventArgs e)
    {
        if (!ReferenceEquals(sender, _boundViewModel))
        {
            return;
        }

        var failure = await _fullscreenPresenter.ApplyAsync(e.EnterFullscreen);
        var viewModel = _boundViewModel;
        if (viewModel is not null && ReferenceEquals(sender, viewModel))
            viewModel.CompleteFullscreenPresentation(
                e.Revision,
                e.EnterFullscreen && failure is null,
                failure);
    }

    private void OnPlayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is EmbeddedVideoSurface or Border)
        {
            Focus();
        }
    }

    private void OnPlayerKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = _boundViewModel;
        if (viewModel is null)
            return;
        if (PlaybackShortcutRouter.TryHandle(e, viewModel))
            e.Handled = true;
    }

    private async void OnExportDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (_isDiagnosticPickerOpen ||
            DataContext is not VideoPlayerControlViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isDiagnosticPickerOpen = true;
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "导出 VideoSecurityPlayer 脱敏诊断",
                    SuggestedFileName =
                        $"VideoSecurityPlayer-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    DefaultExtension = "json",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("JSON 诊断文件")
                        {
                            Patterns = ["*.json"],
                            MimeTypes = ["application/json"]
                        }
                    ]
                });
            if (file is null || !ReferenceEquals(DataContext, viewModel))
                return;

            var json = await viewModel.CreateDiagnosticJsonAsync();
            await using var output = await file.OpenWriteAsync();
            if (output.CanSeek)
                output.SetLength(0);
            await output.WriteAsync(json);
            await output.FlushAsync();
            if (ReferenceEquals(DataContext, viewModel))
                viewModel.ReportDiagnosticExportSucceeded();
        }
        catch (OperationCanceledException)
        {
            // 用户取消保存是正常交互，不覆盖此前可见状态。
        }
        catch
        {
            // 原始异常可能包含保存路径，界面只显示固定提示。
            if (ReferenceEquals(DataContext, viewModel))
                viewModel.ReportDiagnosticExportFailed();
        }
        finally
        {
            _isDiagnosticPickerOpen = false;
        }
    }

    /// <summary>
    /// 释放最终关闭的 View 所持有的 ViewModel、原生表面和可观察属性订阅。
    /// </summary>
    /// <remarks>
    /// 标签切换不会调用本方法；只有宿主确认 Document 已关闭并逐项移出回收缓存后调用。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _fullscreenPresenter.ForceReset();
        if (_boundViewModel is not null)
        {
            _boundViewModel.ResetFullscreenPresentation();
            _boundViewModel.FullscreenPresentationRequested -=
                OnFullscreenPresentationRequested;
        }

        _surfaceCoordinator.Bind(null);
        _surfaceCoordinator.Dispose();
        _boundViewModel = null;
        NavigationContext = null;
        DataContextChanged -= OnDataContextChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        RemoveHandler(KeyDownEvent, OnPlayerKeyDown);
        RemoveHandler(PointerPressedEvent, OnPlayerPointerPressed);
        // NativeControlHost 可能在窗口合成器完成下一帧前短暂保留原生表面。
        // 断开内容树可避免该临时引用反向保留整套播放器控件。
        Content = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 加载媒体文件
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="password">解密密码</param>
    /// <returns>是否加载成功</returns>
    public async Task<bool> LoadMediaAsync(string filePath, string password)
    {
        if (ViewModel != null)
        {
            return await ViewModel.LoadMediaAsync(filePath, password);
        }
        return false;
    }

    /// <summary>
    /// 清理当前媒体
    /// </summary>
    public Task CleanupMediaAsync(CancellationToken cancellationToken = default)
    {
        return ViewModel?.CleanupMediaAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// 获取当前播放状态
    /// </summary>
    public bool IsPlaying => ViewModel?.IsPlaying ?? false;

    /// <summary>
    /// 获取当前暂停状态
    /// </summary>
    public bool IsPaused => ViewModel?.IsPaused ?? false;
}
