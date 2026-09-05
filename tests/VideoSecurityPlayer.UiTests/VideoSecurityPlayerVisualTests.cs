using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.UiTests;

public sealed class VideoSecurityPlayerVisualTests
{
    [AvaloniaFact]
    public void 四个视频文档在宽窄尺寸与双主题下均可布局()
    {
        var views = CreateViews();
        var application = Assert.IsType<Standalone.App>(Application.Current);
        var originalTheme = application.RequestedThemeVariant;

        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                application.RequestedThemeVariant = theme;
                foreach (var view in views)
                {
                    Measure(view, new Size(1180, 720));
                    Measure(view, new Size(760, 600));

                    Assert.Equal(760, view.Bounds.Width);
                    Assert.Equal(600, view.Bounds.Height);
                    Assert.NotEmpty(view.GetLogicalDescendants().OfType<PathIcon>());
                }
            }
        }
        finally
        {
            application.RequestedThemeVariant = originalTheme;
            foreach (var disposable in views.OfType<IDisposable>())
                disposable.Dispose();
        }
    }

    [AvaloniaFact]
    public void 媒体库继续使用显式虚拟化列表且矢量资源可解析()
    {
        var libraryList = new LibraryListView();
        var list = libraryList.FindControl<ListBox>("LibraryItemsList");

        Assert.NotNull(list);
        Assert.NotNull(list.ItemsPanel);

        var player = new SecretVideoPlayerView();
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = player
        };
        try
        {
            window.Show();
            Measure(player, new Size(900, 700));
            var icons = player.GetLogicalDescendants()
                .OfType<PathIcon>()
                .ToArray();

            Assert.NotEmpty(icons);
            Assert.True(icons.Count(icon => icon.Data is not null) >= 4);
        }
        finally
        {
            window.Content = null;
            window.Close();
            player.Dispose();
        }
    }

    [AvaloniaFact]
    public void 播放进度视图可加载且提供明确Slider事件边界()
    {
        var view = new PlaybackTransportView();

        var slider = view.FindControl<Slider>("PositionSlider");

        Assert.NotNull(slider);
        Assert.Equal("视频播放进度", AutomationProperties.GetName(slider));
    }

    private static UserControl[] CreateViews() =>
    [
        new SecretVideoPlayerView(),
        new SecretVideoLibraryView(),
        new VideoDecryptorView(),
        new VideoEncryptorView()
    ];

    private static void Measure(Control control, Size size)
    {
        control.Measure(size);
        control.Arrange(new Rect(size));
    }
}
