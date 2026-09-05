using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>播放器日常控制视图；导航上下文由媒体库按需注入。</summary>
public partial class PlaybackTransportView : UserControl
{
    public static readonly StyledProperty<IPlaybackNavigationContext?> NavigationContextProperty =
        AvaloniaProperty.Register<PlaybackTransportView, IPlaybackNavigationContext?>(
            nameof(NavigationContext));

    public static readonly StyledProperty<bool> HasNavigationContextProperty =
        AvaloniaProperty.Register<PlaybackTransportView, bool>(nameof(HasNavigationContext));

    public PlaybackTransportView()
    {
        InitializeComponent();

        // Slider 自身会处理部分指针事件，普通 XAML 事件处理器无法稳定观察拖动起止。
        // 这里使用 Avalonia 的 handledEventsToo 只完成“视图事件 -> 既有命令”的局部适配：
        // 播放状态、计时器和 Seek 仍全部由 PlaybackCoordinatorViewModel 拥有，View 不复制
        // 任何业务状态。相比 SDK 中面向任意事件的反射 Behavior，这个明确入口更符合 SRP，
        // 也不会为了单个插件的两个手势扩大所有插件都必须依赖的公共 API 和 NuGet 图。
        PositionSlider.AddHandler(
            InputElement.PointerPressedEvent,
            OnPositionSliderPointerPressed,
            handledEventsToo: true);
        PositionSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPositionSliderPointerReleased,
            handledEventsToo: true);
    }

    public IPlaybackNavigationContext? NavigationContext
    {
        get => GetValue(NavigationContextProperty);
        set => SetValue(NavigationContextProperty, value);
    }

    public bool HasNavigationContext
    {
        get => GetValue(HasNavigationContextProperty);
        set => SetValue(HasNavigationContextProperty, value);
    }

    private void OnPositionSliderPointerPressed(object? sender, PointerPressedEventArgs args)
        => ExecuteStartSliderDrag(DataContext);

    private void OnPositionSliderPointerReleased(object? sender, PointerReleasedEventArgs args)
        => ExecuteEndSliderDrag(DataContext);

    /// <summary>
    /// 将按下手势转交给播放协调器。拆成纯适配方法后，单元测试无需伪造平台指针设备，
    /// 仍能验证 View 使用的确切命令入口；方法保持 internal，不形成插件公共契约。
    /// </summary>
    internal static void ExecuteStartSliderDrag(object? dataContext)
    {
        if (dataContext is not PlaybackTransportViewModel transport)
        {
            return;
        }

        var command = transport.Owner.StartSliderDragCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>将释放手势转交给既有异步 Seek 命令，不在 View 中复制结束拖动规则。</summary>
    internal static void ExecuteEndSliderDrag(object? dataContext)
    {
        if (dataContext is not PlaybackTransportViewModel transport)
        {
            return;
        }

        var command = transport.Owner.EndSliderDragCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
