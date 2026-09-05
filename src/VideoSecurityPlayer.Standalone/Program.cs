using Avalonia;
namespace VideoSecurityPlayer.Standalone;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var smokeIndex = Array.IndexOf(args, "--smoke-media");
        if (smokeIndex >= 0)
        {
            var reportIndex = Array.IndexOf(args, "--smoke-report");
            if (smokeIndex + 1 >= args.Length || reportIndex < 0 || reportIndex + 1 >= args.Length)
                throw new ArgumentException("Use --smoke-media <media> --smoke-report <json>.");
            PlaybackSmoke.MediaPath = Path.GetFullPath(args[smokeIndex + 1]);
            PlaybackSmoke.ReportPath = Path.GetFullPath(args[reportIndex + 1]);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
