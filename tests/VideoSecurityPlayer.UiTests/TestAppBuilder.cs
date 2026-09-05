using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(VideoSecurityPlayer.UiTests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace VideoSecurityPlayer.UiTests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<VideoSecurityPlayer.Standalone.App>();
        return Environment.GetEnvironmentVariable("VIDEO_PLAYER_UI_CAPTURE_ROOT") is null
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            : builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
