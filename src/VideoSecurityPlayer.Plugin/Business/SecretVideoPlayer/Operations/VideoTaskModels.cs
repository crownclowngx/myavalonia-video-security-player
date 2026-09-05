namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

public enum VideoTaskState
{
    Pending,
    Preflighting,
    Ready,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum PreflightSeverity
{
    Warning,
    Blocking
}

public enum VideoTaskFailureCode
{
    InvalidRequest,
    InvalidFormat,
    AuthenticationFailed,
    CorruptedContent,
    InputUnavailable,
    InputOutputConflict,
    OutputConflict,
    PermissionDenied,
    InsufficientDiskSpace,
    DiskIo,
    CleanupFailed,
    Cancelled,
    Unknown
}

public sealed record VideoPreflightIssue(
    VideoTaskFailureCode Code,
    PreflightSeverity Severity,
    string Message,
    string SuggestedAction)
{
    public string SeverityText => Severity == PreflightSeverity.Blocking ? "阻止" : "警告";
}

public sealed record VideoPreflightResult(
    long RequiredBytes,
    long? AvailableBytes,
    IReadOnlyList<VideoPreflightIssue> Issues)
{
    public bool CanProceed => Issues.All(issue => issue.Severity != PreflightSeverity.Blocking);

    public static VideoPreflightResult Ready(long requiredBytes, long? availableBytes = null) =>
        new(requiredBytes, availableBytes, Array.Empty<VideoPreflightIssue>());
}

public sealed record VideoTaskProgress(
    VideoTaskState State,
    long ProcessedBytes,
    long TotalBytes,
    double Percentage,
    string Message,
    VideoTaskFailureCode? FailureCode = null);

public sealed class VideoTaskException : Exception
{
    public VideoTaskException(
        VideoTaskFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public VideoTaskFailureCode FailureCode { get; }
}

internal static class VideoTaskFailureClassifier
{
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;

    public static bool IsDiskFull(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is ErrorHandleDiskFull or ErrorDiskFull;
    }

    public static VideoTaskException Map(Exception exception, bool readingInput)
    {
        if (exception is VideoTaskException taskException)
            return taskException;

        return exception switch
        {
            UnauthorizedAccessException => new VideoTaskException(
                VideoTaskFailureCode.PermissionDenied,
                readingInput
                    ? "没有读取输入文件的权限。"
                    : "没有写入输出目录的权限。",
                exception),
            FileNotFoundException or DirectoryNotFoundException => new VideoTaskException(
                readingInput ? VideoTaskFailureCode.InputUnavailable : VideoTaskFailureCode.DiskIo,
                readingInput
                    ? "输入文件不存在或已被删除。"
                    : "输出目录不存在或已被删除。",
                exception),
            IOException ioException when IsDiskFull(ioException) => new VideoTaskException(
                VideoTaskFailureCode.InsufficientDiskSpace,
                "磁盘空间不足，无法完成写入。",
                exception),
            IOException => new VideoTaskException(
                readingInput ? VideoTaskFailureCode.InputUnavailable : VideoTaskFailureCode.DiskIo,
                readingInput
                    ? "输入文件被占用、已删除或无法读取。"
                    : "输出设备或文件系统发生错误。",
                exception),
            _ => new VideoTaskException(
                VideoTaskFailureCode.Unknown,
                "处理视频时发生未预期错误。",
                exception)
        };
    }
}
