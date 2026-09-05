using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace VideoSecurityPlayer.Standalone;
public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (PlaybackSmoke.MediaPath is not null)
            {
                var root = Path.Combine(Path.GetTempPath(), "video-standalone-smoke-" + Guid.NewGuid().ToString("N"));
                var runtime = new PreviewRuntime(root);
                var window = new MainWindow(runtime);
                window.Opened += async (_, _) => await PlaybackSmoke.RunAsync(window, runtime, root);
                desktop.MainWindow = window;
            }
            else desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
