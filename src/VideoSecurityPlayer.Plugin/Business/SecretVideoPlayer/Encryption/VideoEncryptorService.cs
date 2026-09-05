using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

/// <summary>
/// 单文件加密用例：预检输入和输出环境，并把执行委托给 SECVID03 流式加密器。
/// </summary>
public sealed class VideoEncryptorService : IVideoEncryptionService
{
    private readonly ISecvid03Encryptor _encryptor;
    private readonly IStoragePreflightProbe _storageProbe;

    public VideoEncryptorService(ISecvid03Encryptor encryptor)
        : this(encryptor, new StoragePreflightProbe())
    {
    }

    public VideoEncryptorService(
        ISecvid03Encryptor encryptor,
        IStoragePreflightProbe storageProbe)
    {
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _storageProbe = storageProbe ?? throw new ArgumentNullException(nameof(storageProbe));
    }

    public async Task<VideoPreflightResult> PreflightAsync(
        VideoEncryptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<VideoPreflightIssue>();

        if (string.IsNullOrWhiteSpace(request.InputPath) ||
            string.IsNullOrWhiteSpace(request.OutputPath))
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "输入文件和输出路径不能为空。",
                "请选择输入视频并指定输出文件。"));
            return new VideoPreflightResult(0, null, issues);
        }

        string inputPath;
        string outputPath;
        try
        {
            inputPath = Path.GetFullPath(request.InputPath);
            outputPath = Path.GetFullPath(request.OutputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "输入文件或输出路径无效。",
                "重新选择有效的文件路径。"));
            return new VideoPreflightResult(0, null, issues);
        }

        if (inputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InputOutputConflict,
                "输入文件和输出文件不能相同。",
                "为加密文件选择其他名称或目录。"));
        }

        if (File.Exists(outputPath))
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.OutputConflict,
                "输出文件已经存在，不会覆盖现有文件。",
                "更换输出文件名或目录。"));
        }

        if (EncryptedVideoContainer.CountRunes(request.PublicTitle) > EncryptedVideoContainer.MaxTitleRunes ||
            EncryptedVideoContainer.CountRunes(request.PublicDescription) > EncryptedVideoContainer.MaxDescriptionRunes)
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "公开标题或描述超过 SECVID03 允许的长度。",
                "缩短公开标题或描述后重试。"));
        }

        long requiredBytes = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(inputPath);
            if (!info.Exists)
                throw new FileNotFoundException();

            await using var input = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                Secvid03Format.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var originalHeaderLength = Secvid03Format.DetectOriginalHeaderLength(input);
            requiredBytes = Secvid03Format.CalculateLayout(info.Length, originalHeaderLength).PhysicalFileLength;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var mapped = VideoTaskFailureClassifier.Map(ex, readingInput: true);
            issues.Add(Blocking(
                mapped.FailureCode,
                mapped.Message,
                "检查输入文件是否存在、可读且未被其他程序独占。"));
        }

        if (issues.Any(issue => issue.Severity == PreflightSeverity.Blocking))
            return new VideoPreflightResult(requiredBytes, null, issues);

        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "输出路径缺少有效目录。",
                "选择一个有效的输出目录。"));
            return new VideoPreflightResult(requiredBytes, null, issues);
        }

        var storage = await _storageProbe
            .CheckAsync(directory, requiredBytes, createDirectory: true, cancellationToken)
            .ConfigureAwait(false);
        issues.AddRange(storage.Issues);
        return new VideoPreflightResult(requiredBytes, storage.AvailableBytes, issues);
    }

    public async Task EncryptAsync(
        VideoEncryptionRequest request,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new VideoTaskException(VideoTaskFailureCode.InvalidRequest, "密码至少需要 6 个字符。");

        var preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
        var blocker = preflight.Issues.FirstOrDefault(issue => issue.Severity == PreflightSeverity.Blocking);
        if (blocker is not null)
            throw new VideoTaskException(blocker.Code, blocker.Message);

        progress?.Report(new VideoTaskProgress(
            VideoTaskState.Ready,
            0,
            preflight.RequiredBytes,
            0,
            "预检通过，准备加密。"));

        try
        {
            await _encryptor.EncryptAsync(request, password, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw VideoTaskFailureClassifier.Map(ex, readingInput: ex is FileNotFoundException);
        }
    }

    private static VideoPreflightIssue Blocking(
        VideoTaskFailureCode code,
        string message,
        string action) =>
        new(code, PreflightSeverity.Blocking, message, action);
}
