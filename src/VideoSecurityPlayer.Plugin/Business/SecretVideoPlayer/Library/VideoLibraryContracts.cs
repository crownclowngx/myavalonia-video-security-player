namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>控制一次媒体库扫描是否进入子目录。</summary>
/// <remarks>
/// 默认值刻意保持 G6 的“仅当前目录”行为，避免升级后突然读取用户没有主动选择的目录树。
/// </remarks>
public sealed record VideoLibraryScanOptions(bool IncludeSubdirectories)
{
    public static VideoLibraryScanOptions TopDirectoryOnly { get; } = new(false);
}

public enum VideoLibraryMetadataState
{
    Ready,
    Failed
}

public enum VideoLibrarySortField
{
    FileName,
    PublicTitle,
    ModifiedTime,
    LastPlayedTime
}

public enum VideoLibrarySortDirection
{
    Ascending,
    Descending
}

public enum VideoLibraryStatusFilter
{
    All,
    Available,
    MetadataFailed,
    Unplayed,
    InProgress,
    Completed
}

public enum VideoPlaybackHistoryState
{
    Unplayed,
    InProgress,
    Completed
}

/// <summary>
/// 扫描阶段可获得的 SECVID03 文件快照。
/// </summary>
/// <remarks>
/// <see cref="FileId"/> 来自无需密码即可读取的固定头，因此扫描阶段只能把它当作索引提示，
/// 不能把它当作认证结论。真正加载媒体时，现有播放链路仍会使用密码认证完整固定头。
/// </remarks>
public sealed record VideoLibraryScanResult(
    string FilePath,
    string FileNameWithoutExtension,
    string PublicTitle,
    string PublicDescription,
    VideoLibraryMetadataState State,
    string ErrorMessage,
    DateTimeOffset LastWriteTimeUtc = default,
    long FileLength = 0,
    long OriginalFileLength = 0,
    string FileId = "");

public interface IVideoLibraryScanner
{
    /// <summary>
    /// 保留 G6 的窄入口，使既有测试替身和插件扩展不必为了 G7 立即理解递归选项。
    /// </summary>
    IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
        string folderPath,
        CancellationToken cancellationToken);

    /// <summary>按指定递归策略扫描目录中的 SECVID03 文件。</summary>
    IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        CancellationToken cancellationToken) =>
        ScanAsync(folderPath, cancellationToken);

    /// <summary>
    /// 读取单个候选文件，供目录监听的增量更新复用与全量扫描完全相同的错误映射。
    /// </summary>
    Task<VideoLibraryScanResult?> ReadFileAsync(
        string filePath,
        CancellationToken cancellationToken) =>
        Task.FromResult<VideoLibraryScanResult?>(null);
}
