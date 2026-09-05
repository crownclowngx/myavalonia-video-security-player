using System.Runtime.CompilerServices;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>
/// 枚举当前目录中的 .secvid 文件，并以固定并发度读取无需密码的公开信息。
/// </summary>
public sealed class VideoLibraryScanner : IVideoLibraryScanner
{
    private const int MaxConcurrency = 4;

    public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
        string folderPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var result in ScanAsync(
                           folderPath,
                           VideoLibraryScanOptions.TopDirectoryOnly,
                           cancellationToken))
        {
            yield return result;
        }
    }

    public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
        string folderPath,
        VideoLibraryScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("视频文件夹不能为空。", nameof(folderPath));
        ArgumentNullException.ThrowIfNull(options);

        var filePaths = await Task.Run(
            () => EnumerateCandidates(folderPath, options),
            cancellationToken);
        var activeReads = new List<Task<VideoLibraryScanResult>>(MaxConcurrency);
        var nextIndex = 0;

        while (nextIndex < filePaths.Length && activeReads.Count < MaxConcurrency)
            activeReads.Add(ReadOneAsync(filePaths[nextIndex++], cancellationToken));

        while (activeReads.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(activeReads);
            activeReads.Remove(completed);
            yield return await completed;

            if (nextIndex < filePaths.Length)
                activeReads.Add(ReadOneAsync(filePaths[nextIndex++], cancellationToken));
        }
    }

    public async Task<VideoLibraryScanResult?> ReadFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!IsCandidate(filePath) || !File.Exists(filePath))
            return null;

        return await ReadOneAsync(Path.GetFullPath(filePath), cancellationToken);
    }

    private static string[] EnumerateCandidates(
        string folderPath,
        VideoLibraryScanOptions options)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"文件夹不存在: {folderPath}");

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = options.IncludeSubdirectories,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };
        return Directory
            .EnumerateFiles(folderPath, "*", enumerationOptions)
            .Where(IsCandidate)
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsCandidate(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".secvid", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileName(path).StartsWith(".partial-", StringComparison.OrdinalIgnoreCase);

    private static Task<VideoLibraryScanResult> ReadOneAsync(
        string filePath,
        CancellationToken cancellationToken) =>
        Task.Run(() => ReadOne(filePath, cancellationToken), cancellationToken);

    private static VideoLibraryScanResult ReadOne(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var fileInfo = new FileInfo(filePath);

        try
        {
            var publicInfo = EncryptedVideoContainer.ReadPublicInfo(filePath);
            return new VideoLibraryScanResult(
                filePath,
                fileName,
                publicInfo.Title,
                publicInfo.Description,
                VideoLibraryMetadataState.Ready,
                string.Empty,
                fileInfo.LastWriteTimeUtc,
                fileInfo.Length,
                publicInfo.OriginalFileLength,
                publicInfo.FileId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Failed(filePath, fileName, fileInfo, "不是有效的 SECVID03，或公开信息已损坏");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(filePath, fileName, fileInfo, "没有读取权限");
        }
        catch (IOException)
        {
            return Failed(filePath, fileName, fileInfo, "文件被占用、已删除或发生磁盘错误");
        }
        catch
        {
            return Failed(filePath, fileName, fileInfo, "公开信息读取失败");
        }
    }

    private static VideoLibraryScanResult Failed(
        string path,
        string fileName,
        FileInfo fileInfo,
        string error)
    {
        DateTimeOffset modified = default;
        long length = 0;
        try
        {
            fileInfo.Refresh();
            if (fileInfo.Exists)
            {
                modified = fileInfo.LastWriteTimeUtc;
                length = fileInfo.Length;
            }
        }
        catch
        {
            // 错误项仍可进入列表；文件属性只是辅助展示，不能覆盖更有用的稳定错误。
        }

        return new VideoLibraryScanResult(
            path,
            fileName,
            string.Empty,
            string.Empty,
            VideoLibraryMetadataState.Failed,
            error,
            modified,
            length);
    }
}
