using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Styling;
using VideoSecurityPlayer.Standalone;
using Xunit;

namespace VideoSecurityPlayer.UiTests;

public sealed class StandaloneWindowTests
{
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
