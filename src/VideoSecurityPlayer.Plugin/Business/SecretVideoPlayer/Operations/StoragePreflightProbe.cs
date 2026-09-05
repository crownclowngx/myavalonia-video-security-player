namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

public interface IStoragePreflightProbe
{
    Task<VideoPreflightResult> CheckAsync(
        string outputDirectory,
        long requiredBytes,
        bool createDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class StoragePreflightProbe : IStoragePreflightProbe
{
    public async Task<VideoPreflightResult> CheckAsync(
        string outputDirectory,
        long requiredBytes,
        bool createDirectory,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<VideoPreflightIssue>();
        string fullDirectory;
        try
        {
            fullDirectory = Path.GetFullPath(outputDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "输出目录路径无效。",
                "请选择有效的本地输出目录。"));
            return new VideoPreflightResult(requiredBytes, null, issues);
        }

        try
        {
            if (!Directory.Exists(fullDirectory))
            {
                if (!createDirectory)
                {
                    issues.Add(Blocking(
                        VideoTaskFailureCode.DiskIo,
                        "输出目录不存在或已被删除。",
                        "重新选择一个已经存在的输出目录。"));
                    return new VideoPreflightResult(requiredBytes, null, issues);
                }

                Directory.CreateDirectory(fullDirectory);
            }
        }
        catch (Exception ex)
        {
            var mapped = VideoTaskFailureClassifier.Map(ex, readingInput: false);
            issues.Add(Blocking(mapped.FailureCode, mapped.Message, "更换输出目录或检查目录权限。"));
            return new VideoPreflightResult(requiredBytes, null, issues);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var probePath = Path.Combine(fullDirectory, $".secvid-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var probe = new FileStream(
                             probePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1,
                             FileOptions.Asynchronous))
            {
                await probe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Delete(probePath);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                // 取消仍保持取消语义；Document 已关闭时不能用清理错误替换取消。
            }

            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                issues.Add(Blocking(
                    VideoTaskFailureCode.CleanupFailed,
                    "输出目录中的临时探针无法清理。",
                    "检查目录权限、占用程序或更换输出目录。"));
                return new VideoPreflightResult(requiredBytes, null, issues);
            }

            var mapped = VideoTaskFailureClassifier.Map(ex, readingInput: false);
            issues.Add(Blocking(mapped.FailureCode, mapped.Message, "更换输出目录或检查目录权限。"));
            return new VideoPreflightResult(requiredBytes, null, issues);
        }

        long? availableBytes = null;
        try
        {
            var root = Path.GetPathRoot(fullDirectory);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("无法解析输出卷。");

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            if (availableBytes < requiredBytes)
            {
                issues.Add(Blocking(
                    VideoTaskFailureCode.InsufficientDiskSpace,
                    $"可用空间不足：需要至少 {FormatBytes(requiredBytes)}，当前约 {FormatBytes(availableBytes.Value)}。",
                    "释放磁盘空间或选择其他输出目录。"));
            }
        }
        catch
        {
            issues.Add(new VideoPreflightIssue(
                VideoTaskFailureCode.DiskIo,
                PreflightSeverity.Warning,
                "无法可靠获取输出位置的可用空间。",
                "可以继续，但请确认输出设备具有足够空间。"));
        }

        return new VideoPreflightResult(requiredBytes, availableBytes, issues);
    }

    private static VideoPreflightIssue Blocking(
        VideoTaskFailureCode code,
        string message,
        string action) =>
        new(code, PreflightSeverity.Blocking, message, action);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):F2} GiB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):F2} MiB";
        return $"{bytes / 1024d:F2} KiB";
    }
}
