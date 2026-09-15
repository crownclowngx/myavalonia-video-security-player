namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

public enum VideoInputKind { PlainVideo, EncryptedVideo }

/// <summary>导入边界提供文件与相对目录；相对目录仅用于输出建议，绝不作为已检查的输出授权。</summary>
public sealed record VideoInputFile(string Path, string RelativeDirectory = "");
public sealed record VideoInputDiscoveryResult(IReadOnlyList<VideoInputFile> Files, int SkippedCount,
    IReadOnlyList<string> Issues);

/// <summary>只发现候选文件，不读密码、不解析容器，也不修改队列。</summary>
public interface IVideoInputDiscovery
{
    Task<VideoInputDiscoveryResult> DiscoverAsync(IReadOnlyList<string> paths, VideoInputKind kind,
        bool recursive, CancellationToken cancellationToken);
}

/// <summary>
/// 将文件选择、目录选择和拖放统一为一次可取消的发现过程。目录逐层遍历，跳过重解析点，
/// 避免符号链接循环；局部目录无权限时保留其他可读候选，并把问题带回界面。
/// </summary>
public sealed class VideoInputDiscovery : IVideoInputDiscovery
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpeg", ".mpg", ".ts", ".m2ts" };

    public Task<VideoInputDiscoveryResult> DiscoverAsync(IReadOnlyList<string> paths, VideoInputKind kind,
        bool recursive, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var files = new List<VideoInputFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        var skipped = 0;
        foreach (var input in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(input);
                if (Directory.Exists(path))
                {
                    var pending = new Stack<string>();
                    pending.Push(path);
                    var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    while (pending.TryPop(out var directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (Directory.Exists(entry)) { if (recursive) pending.Push(entry); }
                                else AddFile(entry, Path.Combine(rootName, Path.GetRelativePath(path, directory)));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { issues.Add($"无法读取目录：{directory}"); }
                    }
                }
                else if (File.Exists(path)) AddFile(path, "");
                else issues.Add($"文件或目录不存在：{path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { issues.Add($"无法读取输入：{input}"); }
        }
        return new VideoInputDiscoveryResult(files, skipped, issues);

        void AddFile(string path, string relative)
        {
            var accepted = kind == VideoInputKind.EncryptedVideo
                ? string.Equals(Path.GetExtension(path), ".secvid", StringComparison.OrdinalIgnoreCase)
                : VideoExtensions.Contains(Path.GetExtension(path));
            if (!accepted || !seen.Add(path)) { skipped++; return; }
            files.Add(new(path, relative));
        }
    }, cancellationToken);
}
