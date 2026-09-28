using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using VideoSecurityPlayer.Features.Settings;
using VideoSecurityPlayer.Standalone;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.UiTests;

public sealed class S1SettingsViewTests
{
    [AvaloniaTheory]
    [InlineData(320, false)] [InlineData(400, false)] [InlineData(736, false)]
    [InlineData(320, true)] [InlineData(400, true)] [InlineData(736, true)]
    public async Task 四组设置在窄宽与双主题下绑定真实草稿(double width, bool dark)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1-ui-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var contribution = Assert.Single(runtime.Registration.Tools);
        var model = Assert.IsType<VideoSettingsTool>(runtime.GetToolModel(contribution));
        var view = runtime.CreateToolView(contribution);
        var window = new Window { Width = width, Height = 700, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            Assert.Equal(4, view.GetLogicalDescendants().OfType<Expander>().Count());
            var remember = view.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "记住上次音量"));
            remember.IsChecked = false;
            window.UpdateLayout();
            var volume = view.GetLogicalDescendants().OfType<NumericUpDown>().Single();
            Assert.True(volume.IsEffectivelyVisible);
            volume.Value = 42;
            await model.Playback.Save.PendingTask;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(42, model.Playback.FixedVolume);
            Assert.False(model.Playback.Save.HasFailure);
            model.LibraryExpanded = model.RecordsExpanded = model.RuntimeExpanded = true;
            window.UpdateLayout();
            var visibleInputs = view.GetLogicalDescendants().OfType<ComboBox>().Where(input => input.IsEffectivelyVisible).ToArray();
            Assert.True(visibleInputs.Length >= 3);
            foreach (var input in visibleInputs)
                Assert.InRange(input.Bounds.Width, 1, width);
            model.Runtime.Automatic = false;
            model.Runtime.DirectoryPath = "";
            await model.Runtime.SaveConfigurationCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.Runtime.Save.HasFailure);
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.Contains("绝对目录") == true);
            var capture = Environment.GetEnvironmentVariable("VIDEO_PLAYER_UI_CAPTURE_ROOT");
            if (capture is not null)
            {
                Directory.CreateDirectory(capture);
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(Path.Combine(capture, $"S1-{width}-{(dark ? "dark" : "light")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Content = null; window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task 记录清理确认和运行库失败反馈在窄面板内可见()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1-maintenance-ui-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var contribution = Assert.Single(runtime.Registration.Tools);
        var model = Assert.IsType<VideoSettingsTool>(runtime.GetToolModel(contribution));
        model.PlaybackExpanded = model.LibraryExpanded = false;
        model.RecordsExpanded = model.RuntimeExpanded = true;
        var view = runtime.CreateToolView(contribution);
        var window = new Window { Width = 320, Height = 760, Content = view };
        window.Show();
        try
        {
            var request = view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "清空观看记录"));
            request.Command!.Execute(null);
            window.UpdateLayout();
            var confirm = view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "确认清空记录"));
            Assert.True(confirm.IsEffectivelyVisible);
            await model.Records.ClearHistoryCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.Records.ConfirmHistory);
            model.Runtime.Automatic = false;
            model.Runtime.DirectoryPath = "not-an-absolute-path";
            await model.Runtime.SaveConfigurationCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.Runtime.Save.HasFailure);
            var scroll = view.GetLogicalDescendants().OfType<ScrollViewer>().First();
            scroll.ScrollToEnd(); window.UpdateLayout();
            Assert.Equal(0, scroll.Offset.X);
            var capture = Environment.GetEnvironmentVariable("VIDEO_PLAYER_UI_CAPTURE_ROOT");
            if (capture is not null)
            {
                Directory.CreateDirectory(capture);
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(Path.Combine(capture, "S1-runtime-320.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Content = null; window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task 隐藏和重建View保留同一模型且两个控件独立()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1-views-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var tool = Assert.Single(runtime.Registration.Tools);
        var model = Assert.IsType<VideoSettingsTool>(runtime.GetToolModel(tool));
        var first = runtime.CreateToolView(tool);
        var second = runtime.CreateToolView(tool);
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(second, 1); panel.Children.Add(first); panel.Children.Add(second);
        var window = new Window { Width = 850, Height = 700, Content = panel };
        window.Show();
        try
        {
            Assert.NotSame(first, second);
            Assert.Same(first.DataContext, second.DataContext);
            model.Runtime.DirectoryPath = "草稿保留";
            model.RuntimeExpanded = true;
            panel.Children.Remove(first);
            var replacement = runtime.CreateToolView(tool);
            panel.Children.Add(replacement);
            window.UpdateLayout();
            Assert.Same(model, replacement.DataContext);
            Assert.Equal("草稿保留", replacement.GetLogicalDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "运行库目录").Text);
            Assert.True(replacement.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "播放组件与诊断")).IsExpanded);
        }
        finally { window.Content = null; window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task 页内设置开关保持播放器实例并在关闭后恢复焦点()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1-inline-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new PreviewRuntime(root);
        var window = new MainWindow(runtime) { Width = 760, Height = 700 };
        window.Show();
        try
        {
            var tab = await window.OpenDocumentAsync(runtime.Registration.Documents.Single(c => c.ModelType == typeof(SecretVideoPlayerViewModel)));
            var document = Assert.IsType<PreviewDocument>(tab.Tag);
            var player = document.View!.GetLogicalDescendants().OfType<VideoPlayerControl>().Single();
            var instance = player.DataContext;
            player.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "OpenSettingsButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var settings = player.GetLogicalDescendants().OfType<VideoSettingsView>().Single();
            Assert.True(settings.IsEffectivelyVisible);
            Assert.IsType<VideoSettingsTool>(settings.DataContext);
            var close = player.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "关闭设置"));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Same(instance, player.DataContext);
            Assert.False(settings.IsEffectivelyVisible);
            Assert.True(player.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "OpenSettingsButton").IsFocused);
            await window.CloseDocumentAsync(tab);
        }
        finally { window.Close(); await runtime.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
