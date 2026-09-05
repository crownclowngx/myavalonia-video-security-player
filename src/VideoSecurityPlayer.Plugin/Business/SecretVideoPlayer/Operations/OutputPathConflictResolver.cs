namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

/// <summary>
/// 计划阶段的输出路径冲突解析结果。
/// </summary>
public sealed record OutputPathResolution(
    string OutputPath,
    bool HadConflict,
    bool IsResolved);

/// <summary>
/// 在批次计划内分配不覆盖的输出路径。
/// </summary>
public interface IOutputPathConflictResolver
{
    /// <summary>
    /// 根据策略检查磁盘和当前批次已经占用的路径。
    /// </summary>
    OutputPathResolution Resolve(
        string requestedPath,
        OutputConflictPolicy policy,
        ISet<string> allocatedPaths);
}

/// <summary>
/// 使用 Windows 大小写不敏感语义和数字后缀分配输出路径。
/// </summary>
/// <remarks>
/// 数字后缀只解决“检查批次”时已经存在的冲突。预检不是文件锁；如果另一个进程在
/// 检查和执行之间创建了最终路径，G2 输出事务必须返回 <c>OutputConflict</c>，
/// 而不能在执行阶段再次悄悄改名。
/// </remarks>
public sealed class OutputPathConflictResolver : IOutputPathConflictResolver
{
    /// <inheritdoc />
    public OutputPathResolution Resolve(
        string requestedPath,
        OutputConflictPolicy policy,
        ISet<string> allocatedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        ArgumentNullException.ThrowIfNull(allocatedPaths);

        var fullPath = Path.GetFullPath(requestedPath);
        if (!IsOccupied(fullPath, allocatedPaths))
        {
            allocatedPaths.Add(fullPath);
            return new OutputPathResolution(fullPath, false, true);
        }

        if (policy == OutputConflictPolicy.Block)
            return new OutputPathResolution(fullPath, true, false);

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return new OutputPathResolution(fullPath, true, false);

        var extension = Path.GetExtension(fullPath);
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
            if (IsOccupied(candidate, allocatedPaths))
                continue;

            allocatedPaths.Add(candidate);
            return new OutputPathResolution(candidate, true, true);
        }

        return new OutputPathResolution(fullPath, true, false);
    }

    private static bool IsOccupied(string path, ISet<string> allocatedPaths) =>
        File.Exists(path) || Directory.Exists(path) || allocatedPaths.Contains(path);
}
