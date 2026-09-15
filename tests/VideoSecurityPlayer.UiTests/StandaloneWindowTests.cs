using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using Avalonia.Automation;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Standalone;
using Xunit;

namespace VideoSecurityPlayer.UiTests;

public sealed class StandaloneWindowTests
{
    [AvaloniaFact]
    public async Task 常用播放控件弹层绑定正确且快捷键不抢输入焦点()
    {
        var root = Path.Combine(Path.GetTempPath(), "ux1-controls-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var window = new MainWindow(runtime) { Width = 760, Height = 600 };
        window.Show();
        try
        {
            var feature = runtime.Registration.Documents.Single(x => x.ModelType == typeof(SecretVideoPlayerViewModel));
            var tab = await window.OpenDocumentAsync(feature);
            var document = Assert.IsType<PreviewDocument>(tab.Tag);
            var model = Assert.IsType<SecretVideoPlayerViewModel>(document.Model);
            var player = model.PlayerViewModel;
            player.Volume = 63;
            Assert.False(VideoSecurityPlayer.Views.SecretVideoPlayer.Playback.PlaybackShortcutRouter.TryHandle(
                new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.M, Source = new TextBox() }, player));
            Assert.Equal(63, player.Volume);
            Assert.True(VideoSecurityPlayer.Views.SecretVideoPlayer.Playback.PlaybackShortcutRouter.TryHandle(
                new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.M, Source = new Border() }, player));
            Assert.True(player.IsMuted);
            var view = Assert.IsAssignableFrom<Control>(document.View);
            var jump = view.GetLogicalDescendants().OfType<Button>().Single(x => Equals(x.Content, "跳转"));
            player.JumpTimeText = "1:23";
            jump.Flyout!.ShowAt(jump);
            window.UpdateLayout();
            var content = Assert.IsAssignableFrom<Control>(Assert.IsType<Flyout>(jump.Flyout).Content);
            Assert.Equal("1:23", content.GetLogicalDescendants().OfType<TextBox>().Single().Text);
            jump.Flyout.Hide();
            await window.CloseDocumentAsync(tab);
        }
        finally { window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task 加密器窄窗口可多选配置且完成项保持只读()
    {
        var root = Path.Combine(Path.GetTempPath(), "ux1-queue-layout-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var window = new MainWindow(runtime) { Width = 760, Height = 600 };
        window.Show();
        try
        {
            var feature = runtime.Registration.Documents.Single(x => x.ModelType == typeof(VideoEncryptorViewModel));
            var tab = await window.OpenDocumentAsync(feature);
            var document = Assert.IsType<PreviewDocument>(tab.Tag);
            var model = Assert.IsType<VideoEncryptorViewModel>(document.Model);
            await model.AddFilesAsync([Path.Combine(root, "one.mp4"), Path.Combine(root, "two.mp4")]);
            var view = Assert.IsAssignableFrom<Control>(document.View);
            var list = view.GetLogicalDescendants().OfType<ListBox>().Single(x => x.Name == "QueueList");
            list.SelectedItems!.Add(model.Items[1]);
            model.UnifiedOutputDirectory = Path.Combine(root, "outputs");
            model.Items[0].Status.State = VideoSecurityPlayer.Business.SecretVideoPlayer.Operations.VideoTaskState.Succeeded;
            var expanded = view.GetLogicalDescendants().OfType<Expander>().Single();
            expanded.IsExpanded = true;
            window.UpdateLayout();
            var apply = view.GetLogicalDescendants().OfType<Button>().Single(x => Equals(x.Content, "应用到所选"));
            apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith(model.UnifiedOutputDirectory, model.Items[1].RequestedOutputPath);
            Assert.DoesNotContain("outputs", model.Items[0].RequestedOutputPath);
            list.SelectedItem = model.Items[0];
            window.UpdateLayout();
            Assert.True(view.GetLogicalDescendants().OfType<TextBox>().Single(x => x.Text == model.Items[0].RequestedOutputPath).IsReadOnly);
            var start = view.GetLogicalDescendants().OfType<Button>().Single(x => Equals(x.Content, "开始执行"));
            var point = start.TranslatePoint(default, view);
            Assert.NotNull(point);
            Assert.InRange(point.Value.Y + start.Bounds.Height, 1, view.Bounds.Height);
            var capture = Environment.GetEnvironmentVariable("VIDEO_PLAYER_UI_CAPTURE_ROOT");
            if (capture is not null)
            {
                Directory.CreateDirectory(capture);
                Dispatcher.UIThread.RunJobs();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(Path.Combine(capture, "EncryptionExpanded.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            await window.CloseDocumentAsync(tab);
        }
        finally { window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task 媒体库窄窗口保留列表高度并可直接选择目录()
    {
        await using var runtime = new PreviewRuntime(Path.Combine(Path.GetTempPath(), "ux1-layout-" + Guid.NewGuid().ToString("N")));
        var window = new MainWindow(runtime) { Width = 760, Height = 600 };
        window.Show();
        try
        {
            var feature = runtime.Registration.Documents.Single(x => x.ModelType == typeof(SecretVideoLibraryViewModel));
            var tab = await window.OpenDocumentAsync(feature);
            var document = Assert.IsType<PreviewDocument>(tab.Tag);
            var model = Assert.IsType<SecretVideoLibraryViewModel>(document.Model);
            model.IsLibrarySettingsExpanded = false;
            window.UpdateLayout();
            var view = Assert.IsAssignableFrom<Control>(document.View);
            var list = view.GetLogicalDescendants().OfType<ListBox>().Single(x => x.Name == "LibraryItemsList");
            Assert.True(list.Bounds.Height >= 90, $"窄窗口列表高度不足：{list.Bounds.Height}");
            var browse = view.GetLogicalDescendants().OfType<Button>()
                .Single(x => AutomationProperties.GetName(x) == "选择视频文件夹");
            var point = browse.TranslatePoint(default, view);
            Assert.NotNull(point);
            Assert.InRange(point.Value.Y, 0, view.Bounds.Height - browse.Bounds.Height);
            Assert.True(browse.Bounds.Height > 0);
            await window.CloseDocumentAsync(tab);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FourFeaturesAndRepeatedTabsHaveIndependentScopesAndCloseTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-preview-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var window = new MainWindow(runtime);
        window.Show();
        try
        {
            Assert.Equal(4, runtime.Registration.Documents.Count);
            Assert.Single(runtime.Registration.Actions);
            foreach (var feature in runtime.Registration.Documents)
            {
                var first = await window.OpenDocumentAsync(feature);
                var second = await window.OpenDocumentAsync(feature);
                var a = Assert.IsType<PreviewDocument>(first.Tag);
                var b = Assert.IsType<PreviewDocument>(second.Tag);
                Assert.NotSame(a.Model, b.Model);
                Assert.NotSame(a.View, b.View);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    window.RequestedThemeVariant = theme;
                    window.Width = 760;
                    window.Height = 600;
                    window.UpdateLayout();
                    Assert.Same(b.Model, b.View!.DataContext);
                    var capture = Environment.GetEnvironmentVariable("VIDEO_PLAYER_UI_CAPTURE_ROOT");
                    if (capture is not null)
                    {
                        Directory.CreateDirectory(capture);
                        Dispatcher.UIThread.RunJobs();
                        using var bitmap = window.CaptureRenderedFrame();
                        Assert.NotNull(bitmap);
                        bitmap.Save(Path.Combine(capture, feature.ModelType.Name + "-" + theme.Key + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                }
                await window.CloseDocumentAsync(first);
                Assert.True(a.ClosingToken.IsCancellationRequested);
                Assert.False(b.ClosingToken.IsCancellationRequested);
                await window.CloseDocumentAsync(second);
                Assert.True(b.ClosingToken.IsCancellationRequested);
            }
        }
        finally { window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task FullscreenLeaseRestoresContentAndCannotReleaseLaterPresentation()
    {
        await using var runtime = new PreviewRuntime(Path.Combine(Path.GetTempPath(), "video-unused-" + Guid.NewGuid().ToString("N")));
        var window = new MainWindow(runtime);
        var original = window.Content;
        window.Show();
        var detachCount = 0;
        Assert.IsAssignableFrom<Control>(original).DetachedFromVisualTree += (_, _) => detachCount++;
        var first = window.TryPresent(new Border());
        Assert.NotNull(first);
        Assert.Null(window.TryPresent(new Border()));
        first.Dispose();
        Assert.Same(original, window.Content);
        var secondContent = new Border();
        using var second = window.TryPresent(secondContent);
        first.Dispose();
        Assert.Same(secondContent, window.FindControl<ContentControl>("FullscreenContent")!.Content);
        Assert.Same(original, window.Content);
        Assert.Equal(0, detachCount);
        second!.Dispose();
        Assert.Same(original, window.Content);
        await Task.Run(second.Dispose);
        window.Close();
    }
}
