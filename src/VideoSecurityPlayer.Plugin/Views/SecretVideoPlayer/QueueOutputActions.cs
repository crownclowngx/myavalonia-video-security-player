using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

/// <summary>用户主动请求的系统文件操作。只打开目录或定位文件，不启动视频、不传递密码。</summary>
internal static class QueueOutputActions
{
    public static async Task<string> RevealAsync(Control owner, string path)
    {
        if (!File.Exists(path)) return "输出文件已经移动或删除，请检查所示路径";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 使用参数列表，路径不经过命令行解释器；资源管理器只选中文件。
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("/select,");
                start.ArgumentList.Add(Path.GetFullPath(path));
                using var process = Process.Start(start);
                return "已定位输出文件";
            }
            return await OpenDirectoryAsync(owner, Path.GetDirectoryName(path) ?? "");
        }
        catch { return "无法定位文件，可复制路径后手动打开"; }
    }

    public static async Task<string> OpenDirectoryAsync(Control owner, string path)
    {
        if (!Directory.Exists(path)) return "输出目录不存在，请检查配置或先完成批次检查";
        try
        {
            return TopLevel.GetTopLevel(owner) is { } top &&
                await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
                ? "已打开输出目录" : "无法打开目录，请复制路径后手动打开";
        }
        catch { return "无法打开目录，请检查访问权限"; }
    }

    public static async Task<string> CopyPathAsync(Control owner, string path)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is null) return "当前窗口无法访问剪贴板";
            await clipboard.SetTextAsync(path);
            return "路径已复制，可在播放器中粘贴打开";
        }
        catch { return "复制失败，可从输出路径文本中手动复制"; }
    }
}
