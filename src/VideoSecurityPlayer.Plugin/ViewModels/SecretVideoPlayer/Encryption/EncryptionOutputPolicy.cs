namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

/// <summary>只生成输出建议。目录边界检查防止相对路径越界，磁盘可写、重名与实际创建仍交给原预检及事务。</summary>
public static class EncryptionOutputPolicy
{
    public static string Create(string input, string outputDirectory, string relativeDirectory, bool preserveDirectories)
    {
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("请选择绝对输出目录");
        var root = Path.GetFullPath(outputDirectory);
        var target = Path.GetFullPath(Path.Combine(root, preserveDirectories ? relativeDirectory : ""));
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("相对目录不能越过统一输出目录");
        return Path.Combine(target, Path.GetFileNameWithoutExtension(input) + "_encrypted.secvid");
    }
}
