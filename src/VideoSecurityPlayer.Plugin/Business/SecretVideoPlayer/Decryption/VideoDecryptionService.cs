using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;

/// <summary>
/// 批量解密用例：候选检查、输出预检、顺序执行、单项失败隔离和取消汇总。
/// </summary>
public sealed class VideoDecryptionService : IVideoDecryptionService
{
    private const int InspectionConcurrency = 4;
    private readonly ISecvid03Decryptor _decryptor;
    private readonly DecryptionOutputPathResolver _outputPathResolver;
    private readonly IStoragePreflightProbe _storageProbe;

    public VideoDecryptionService(
        ISecvid03Decryptor decryptor,
        DecryptionOutputPathResolver outputPathResolver,
        IStoragePreflightProbe storageProbe)
    {
        _decryptor = decryptor ?? throw new ArgumentNullException(nameof(decryptor));
        _outputPathResolver = outputPathResolver ?? throw new ArgumentNullException(nameof(outputPathResolver));
        _storageProbe = storageProbe ?? throw new ArgumentNullException(nameof(storageProbe));
    }

    public async Task<IReadOnlyList<DecryptionCandidate>> InspectAsync(
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        var uniquePaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                fullPath = path;
            }

            if (seenPaths.Add(fullPath))
                uniquePaths.Add(fullPath);
        }

        using var gate = new SemaphoreSlim(InspectionConcurrency);
        var tasks = uniquePaths.Select(async (path, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var candidate = await Task.Run(() => InspectOne(path), cancellationToken).ConfigureAwait(false);
                return (index, candidate);
            }
            finally
            {
                gate.Release();
            }
        });

        var inspected = await Task.WhenAll(tasks).ConfigureAwait(false);
        return inspected.OrderBy(item => item.index).Select(item => item.candidate).ToArray();
    }

    public async Task<BatchDecryptionPreflightResult> PreflightAsync(
        IReadOnlyList<DecryptionCandidate> candidates,
        string outputDirectory,
        CancellationToken cancellationToken = default) =>
        await PreflightAsync(
                candidates.Select(candidate =>
                    new DecryptionQueueRequest(Guid.NewGuid(), candidate)).ToArray(),
                outputDirectory,
                OutputConflictPolicy.GenerateUniqueName,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<BatchDecryptionPreflightResult> PreflightAsync(
        IReadOnlyList<DecryptionQueueRequest> requests,
        string outputDirectory,
        OutputConflictPolicy conflictPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var overallIssues = new List<VideoPreflightIssue>();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            overallIssues.Add(Blocking(
                VideoTaskFailureCode.InvalidRequest,
                "输出目录不能为空。",
                "请选择一个已经存在的输出目录。"));
            return EmptyPreflight(requests, overallIssues);
        }

        var storage = await _storageProbe
            .CheckAsync(outputDirectory, 0, createDirectory: false, cancellationToken)
            .ConfigureAwait(false);
        overallIssues.AddRange(storage.Issues);
        if (overallIssues.Any(issue => issue.Severity == PreflightSeverity.Blocking))
            return EmptyPreflight(requests, overallIssues, storage.AvailableBytes);

        var allocatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CandidateDecryptionPreflight>(requests.Count);
        long cumulativeRequired = 0;
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = request.Candidate;
            var issues = new List<VideoPreflightIssue>();
            var outputPath = string.Empty;
            if (!candidate.IsValid)
            {
                issues.Add(Blocking(
                    candidate.FailureCode ?? VideoTaskFailureCode.InvalidFormat,
                    string.IsNullOrWhiteSpace(candidate.ValidationMessage)
                        ? "文件未通过结构预检。"
                        : candidate.ValidationMessage,
                    "修复输入问题后重新预检，或从队列移除该文件。"));
            }
            else
            {
                try
                {
                    var resolution = _outputPathResolver.ResolvePath(
                        outputDirectory,
                        candidate,
                        conflictPolicy,
                        allocatedPaths);
                    outputPath = resolution.OutputPath;
                    if (resolution.HadConflict && !resolution.IsResolved)
                    {
                        issues.Add(Blocking(
                            VideoTaskFailureCode.OutputConflict,
                            "计划输出与批次内其他项目或磁盘上的文件重名。",
                            "更换输出目录，或明确选择“安全改名”后重新检查批次。"));
                    }
                    else if (resolution.HadConflict)
                    {
                        issues.Add(new VideoPreflightIssue(
                            VideoTaskFailureCode.OutputConflict,
                            PreflightSeverity.Warning,
                            $"输出重名，计划使用“{Path.GetFileName(outputPath)}”。",
                            "执行前请确认自动分配的最终文件名符合预期。"));
                    }
                }
                catch (Exception ex) when (
                    ex is VideoTaskException or ArgumentException or NotSupportedException or PathTooLongException)
                {
                    var taskException = ex as VideoTaskException;
                    issues.Add(Blocking(
                        taskException?.FailureCode ?? VideoTaskFailureCode.InvalidRequest,
                        taskException?.Message ?? "无法为解密视频分配有效输出路径。",
                        "更换输出目录后重试。"));
                }

                // 所有解密项共享一个输出目录，因此累计空间按队列顺序计算。失败项仍保留其
                // 声明长度用于稳定总体进度，但无效候选不会在此分支占用空间预算。
                var itemLength = Math.Max(0, candidate.OriginalFileLength);
                if (itemLength > long.MaxValue - cumulativeRequired)
                {
                    cumulativeRequired = long.MaxValue;
                    issues.Add(Blocking(
                        VideoTaskFailureCode.InsufficientDiskSpace,
                        "批次声明的输出总长度超出可处理范围。",
                        "减少批次文件数量后重试。"));
                }
                else
                {
                    cumulativeRequired += itemLength;
                }
                if (storage.AvailableBytes is long available && cumulativeRequired > available)
                {
                    issues.Add(Blocking(
                        VideoTaskFailureCode.InsufficientDiskSpace,
                        "剩余可用空间不足以导出此文件。",
                        "释放磁盘空间、移除前面的任务或更换输出目录。"));
                }
            }

            items.Add(new CandidateDecryptionPreflight(
                request.ItemId,
                candidate,
                outputPath,
                new VideoPreflightResult(
                    Math.Max(0, candidate.OriginalFileLength),
                    storage.AvailableBytes,
                    issues)));
        }

        return new BatchDecryptionPreflightResult(
            new VideoPreflightResult(cumulativeRequired, storage.AvailableBytes, overallIssues),
            items);
    }

    /// <inheritdoc />
    public async Task DecryptAsync(
        CandidateDecryptionPreflight item,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(password))
            throw new VideoTaskException(VideoTaskFailureCode.InvalidRequest, "密码不能为空。");

        var blocker = item.Result.Issues.FirstOrDefault(issue =>
            issue.Severity == PreflightSeverity.Blocking);
        if (blocker is not null)
            throw new VideoTaskException(blocker.Code, blocker.Message);

        // 密码只穿过当前调用链进入单文件解密器。输出路径来自不可变计划，但预检不是锁：
        // 如果执行前目标被其他进程创建，OutputFileTransaction 仍会以 OutputConflict 失败。
        await _decryptor.DecryptAsync(
                item.Candidate.InputPath,
                item.OutputPath,
                password,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchDecryptionResult> DecryptBatchAsync(
        IReadOnlyList<DecryptionCandidate> candidates,
        string outputDirectory,
        string password,
        IProgress<BatchDecryptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(password))
            throw new VideoTaskException(VideoTaskFailureCode.InvalidRequest, "密码不能为空。");

        var preflight = await PreflightAsync(candidates, outputDirectory, cancellationToken).ConfigureAwait(false);
        var globalBlocker = preflight.Overall.Issues.FirstOrDefault(issue =>
            issue.Severity == PreflightSeverity.Blocking);
        if (globalBlocker is not null)
            throw new VideoTaskException(globalBlocker.Code, globalBlocker.Message);

        var totalBytes = preflight.Overall.RequiredBytes;
        var outputPaths = new List<string>();
        long completedBytes = 0;
        var succeeded = 0;
        var failed = 0;

        for (var index = 0; index < preflight.Items.Count; index++)
        {
            var item = preflight.Items[index];
            var candidate = item.Candidate;
            if (cancellationToken.IsCancellationRequested)
            {
                ReportRemainingCancelled(preflight.Items, index, completedBytes, totalBytes, progress);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var blocker = item.Result.Issues.FirstOrDefault(issue =>
                issue.Severity == PreflightSeverity.Blocking);
            if (blocker is not null)
            {
                failed++;
                completedBytes = SaturatingAdd(completedBytes, candidate.OriginalFileLength);
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Failed,
                    0,
                    completedBytes,
                    totalBytes,
                    blocker.Message,
                    blocker.Code));
                continue;
            }

            progress?.Report(CreateProgress(
                candidate,
                item.OutputPath,
                VideoTaskState.Running,
                0,
                completedBytes,
                totalBytes,
                "正在验证密码并准备解密..."));

            long currentProcessed = 0;
            var fileProgress = new InlineProgress<VideoTaskProgress>(value =>
            {
                currentProcessed = Math.Clamp(value.ProcessedBytes, 0, candidate.OriginalFileLength);
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Running,
                    currentProcessed,
                    completedBytes,
                    totalBytes,
                    value.Message));
            });

            try
            {
                await _decryptor.DecryptAsync(
                    candidate.InputPath,
                    item.OutputPath,
                    password,
                    fileProgress,
                    cancellationToken).ConfigureAwait(false);

                completedBytes = SaturatingAdd(completedBytes, candidate.OriginalFileLength);
                succeeded++;
                outputPaths.Add(item.OutputPath);
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Succeeded,
                    candidate.OriginalFileLength,
                    completedBytes - candidate.OriginalFileLength,
                    totalBytes,
                    "解密完成"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Cancelled,
                    currentProcessed,
                    completedBytes,
                    totalBytes,
                    "已取消",
                    VideoTaskFailureCode.Cancelled));
                ReportRemainingCancelled(preflight.Items, index + 1, completedBytes, totalBytes, progress);
                throw;
            }
            catch (VideoTaskException ex)
            {
                failed++;
                completedBytes = SaturatingAdd(completedBytes, candidate.OriginalFileLength);
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Failed,
                    currentProcessed,
                    completedBytes - currentProcessed,
                    totalBytes,
                    ex.Message,
                    ex.FailureCode));
            }
            catch
            {
                failed++;
                completedBytes = SaturatingAdd(completedBytes, candidate.OriginalFileLength);
                progress?.Report(CreateProgress(
                    candidate,
                    item.OutputPath,
                    VideoTaskState.Failed,
                    currentProcessed,
                    completedBytes - currentProcessed,
                    totalBytes,
                    "解密时发生未预期错误。",
                    VideoTaskFailureCode.Unknown));
            }
        }

        return new BatchDecryptionResult(
            preflight.Items.Count,
            succeeded,
            failed,
            0,
            outputPaths);
    }

    private static DecryptionCandidate InspectOne(string inputPath)
    {
        var encryptedName = Path.GetFileName(inputPath);
        if (!string.Equals(Path.GetExtension(inputPath), ".secvid", StringComparison.OrdinalIgnoreCase))
            return Invalid(inputPath, encryptedName, "仅支持 .secvid 文件。", VideoTaskFailureCode.InvalidFormat);
        if (!File.Exists(inputPath))
            return Invalid(inputPath, encryptedName, "文件不存在或已被删除。", VideoTaskFailureCode.InputUnavailable);

        try
        {
            var info = EncryptedVideoContainer.ReadPublicInfo(inputPath);
            return new DecryptionCandidate(
                inputPath,
                encryptedName,
                info.OriginalFileName,
                info.OriginalExtension,
                info.Title,
                info.OriginalFileLength,
                true,
                string.Empty);
        }
        catch (InvalidDataException)
        {
            return Invalid(
                inputPath,
                encryptedName,
                "不是有效的 SECVID03，或公开信息已损坏。",
                VideoTaskFailureCode.InvalidFormat);
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(inputPath, encryptedName, "没有读取权限。", VideoTaskFailureCode.PermissionDenied);
        }
        catch (IOException)
        {
            return Invalid(
                inputPath,
                encryptedName,
                "文件被占用、已删除或发生磁盘错误。",
                VideoTaskFailureCode.InputUnavailable);
        }
        catch
        {
            return Invalid(inputPath, encryptedName, "文件预检失败。", VideoTaskFailureCode.Unknown);
        }
    }

    private static DecryptionCandidate Invalid(
        string path,
        string encryptedName,
        string message,
        VideoTaskFailureCode failureCode) =>
        new(path, encryptedName, string.Empty, string.Empty, string.Empty, 0, false, message, failureCode);

    private static BatchDecryptionPreflightResult EmptyPreflight(
        IReadOnlyList<DecryptionQueueRequest> requests,
        IReadOnlyList<VideoPreflightIssue> overallIssues,
        long? availableBytes = null) =>
        new(
            new VideoPreflightResult(0, availableBytes, overallIssues),
            requests.Select(request => new CandidateDecryptionPreflight(
                request.ItemId,
                request.Candidate,
                string.Empty,
                new VideoPreflightResult(
                    Math.Max(0, request.Candidate.OriginalFileLength),
                    availableBytes,
                    Array.Empty<VideoPreflightIssue>()))).ToArray());

    private static BatchDecryptionProgress CreateProgress(
        DecryptionCandidate candidate,
        string outputPath,
        VideoTaskState state,
        long fileProcessed,
        long completedBeforeFile,
        long totalBytes,
        string message,
        VideoTaskFailureCode? failureCode = null)
    {
        var filePercentage = candidate.OriginalFileLength == 0
            ? state == VideoTaskState.Succeeded ? 100 : 0
            : Math.Clamp(fileProcessed * 100d / candidate.OriginalFileLength, 0, 100);
        var overallProcessed = SaturatingAdd(completedBeforeFile, fileProcessed);
        var overallPercentage = totalBytes == 0
            ? state == VideoTaskState.Succeeded ? 100 : 0
            : Math.Clamp(overallProcessed * 100d / totalBytes, 0, 100);
        return new BatchDecryptionProgress(
            candidate.InputPath,
            outputPath,
            state,
            fileProcessed,
            candidate.OriginalFileLength,
            filePercentage,
            overallPercentage,
            message,
            failureCode);
    }

    private static void ReportRemainingCancelled(
        IReadOnlyList<CandidateDecryptionPreflight> items,
        int startIndex,
        long completedBytes,
        long totalBytes,
        IProgress<BatchDecryptionProgress>? progress)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            progress?.Report(CreateProgress(
                items[index].Candidate,
                items[index].OutputPath,
                VideoTaskState.Cancelled,
                0,
                completedBytes,
                totalBytes,
                "未开始，批次已取消",
                VideoTaskFailureCode.Cancelled));
        }
    }

    private static VideoPreflightIssue Blocking(
        VideoTaskFailureCode code,
        string message,
        string action) =>
        new(code, PreflightSeverity.Blocking, message, action);

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
