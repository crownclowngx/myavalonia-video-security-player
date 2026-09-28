using Avalonia;
namespace VideoSecurityPlayer.Standalone;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var smokeIndex = Array.IndexOf(args, "--smoke-media");
        var s1Index = Array.IndexOf(args, "--smoke-s1-config");
        if (s1Index >= 0)
        {
            if (s1Index + 1 >= args.Length) throw new ArgumentException("缺少 S1 smoke 配置文件。");
            S1PlaybackSmoke.ReadConfiguration(args[s1Index + 1]);
        }
        if (smokeIndex >= 0)
        {
            var reportIndex = Array.IndexOf(args, "--smoke-report");
            if (smokeIndex + 1 >= args.Length || reportIndex < 0 || reportIndex + 1 >= args.Length)
                throw new ArgumentException("Use --smoke-media <media> --smoke-report <json>.");
            PlaybackSmoke.UseUx1Scenario = args.Contains("--smoke-ux1", StringComparer.Ordinal);
            PlaybackSmoke.MediaPath = Path.GetFullPath(args[smokeIndex + 1]);
            PlaybackSmoke.ReportPath = Path.GetFullPath(args[reportIndex + 1]);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
