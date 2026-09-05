namespace VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>
/// 拥有一次验收运行产生的全部临时文件。
/// </summary>
/// <remarks>
/// 清理前再次校验绝对路径位于本实例创建的根目录，防止参数或路径拼接错误把递归删除
/// 扩大到工作区、用户目录或其他验收运行。
/// </remarks>
internal sealed class AcceptanceWorkspace : IDisposable
{
    private readonly string _rootPath;
    private bool _disposed;

    private AcceptanceWorkspace(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public static AcceptanceWorkspace Create(string suiteName) =>
        new(Path.Combine(
            Path.GetTempPath(),
            $"videosecurityplayer-{suiteName}-{Guid.NewGuid():N}"));

    public string CreateDirectory(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Resolve(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Resolve(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("验收工作区只接受相对路径。", nameof(relativePath));

        var path = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        if (!path.StartsWith(
                _rootPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("验收路径越过了临时工作区边界。");
        }
        return path;
    }

    public IReadOnlyList<string> CopyMany(
        string sourcePath,
        string relativeDirectory,
        int count,
        string extension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var directory = CreateDirectory(relativeDirectory);
        var paths = new string[count];
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(directory, $"{index:D4}{extension}");
            File.Copy(sourcePath, path, overwrite: false);
            paths[index] = path;
        }
        return paths;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var fullPath = Path.GetFullPath(_rootPath);
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(
                tempRoot + Path.DirectorySeparatorChar + "videosecurityplayer-",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理不属于验收运行的目录。");
        }

        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }
}
