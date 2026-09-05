using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

/// <summary>
/// 批量加密的路径分配、逐项预检和同卷累计空间应用服务。
/// </summary>
/// <remarks>
/// 这里刻意顺序预检。预检只读取少量媒体前缀，不值得为有限收益引入另一个并发模型；
/// 顺序还让批次内名称分配和同卷空间累计具有稳定、可复现的结果。
/// </remarks>
public sealed class VideoBatchEncryptionService : IVideoBatchEncryptionService
{
    private readonly IVideoEncryptionService _singleFileService;
    private readonly IOutputPathConflictResolver _pathResolver;

    /// <summary>创建不持有队列或密码的批次计划服务。</summary>
    public VideoBatchEncryptionService(
        IVideoEncryptionService singleFileService,
        IOutputPathConflictResolver pathResolver)
    {
        _singleFileService = singleFileService ?? throw new ArgumentNullException(nameof(singleFileService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    /// <inheritdoc />
    public async Task<BatchEncryptionPlan> PrepareAsync(
        IReadOnlyList<BatchEncryptionItemRequest> requests,
        OutputConflictPolicy conflictPolicy,
        int skippedSucceededCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var allocatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumeUsage = new Dictionary<string, VolumeUsage>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedEncryptionItem>(requests.Count);
        var conflictCount = 0;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OutputPathResolution resolution;
            try
            {
                resolution = _pathResolver.Resolve(
                    request.RequestedOutputPath,
                    conflictPolicy,
                    allocatedPaths);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                var invalidResult = new VideoPreflightResult(
                    0,
                    null,
                    [
                        Blocking(
                            VideoTaskFailureCode.InvalidRequest,
                            "输出路径无效。",
                            "为该项目选择有效的输出文件路径。")
                    ]);
                prepared.Add(new PreparedEncryptionItem(
                    request.ItemId,
                    ToSingleFileRequest(request, request.RequestedOutputPath),
                    invalidResult,
                    false));
                continue;
            }

            if (resolution.HadConflict)
                conflictCount++;

            var singleRequest = ToSingleFileRequest(request, resolution.OutputPath);
            var preflight = await _singleFileService
                .PreflightAsync(singleRequest, cancellationToken)
                .ConfigureAwait(false);
            var issues = preflight.Issues.ToList();

            if (resolution.HadConflict && !resolution.IsResolved)
            {
                // 单文件服务能看到磁盘冲突，但看不到仅发生在本批次 allocatedPaths 中的冲突，
                // 因此批次服务必须补充稳定阻止项，同时避免重复显示同一错误。
                if (issues.All(issue => issue.Code != VideoTaskFailureCode.OutputConflict))
                {
                    issues.Add(Blocking(
                        VideoTaskFailureCode.OutputConflict,
                        "建议输出与批次内其他项目或磁盘上的文件重名。",
                        "修改输出路径，或明确选择“安全改名”后重新检查批次。"));
                }
            }
            else if (resolution.HadConflict)
            {
                issues.Add(new VideoPreflightIssue(
                    VideoTaskFailureCode.OutputConflict,
                    PreflightSeverity.Warning,
                    $"建议输出存在冲突，计划使用“{Path.GetFileName(resolution.OutputPath)}”。",
                    "执行前请确认自动分配的最终文件名符合预期。"));
            }

            // 单文件预检分别比较可用空间会漏掉“每项都放得下、总和放不下”的批次风险。
            // 只有当前项目尚无阻止项时才占用批次预算，避免无效输入挤占后续可执行项目。
            if (issues.All(issue => issue.Severity != PreflightSeverity.Blocking))
            {
                ApplyCumulativeVolumeCheck(
                    resolution.OutputPath,
                    preflight.RequiredBytes,
                    preflight.AvailableBytes,
                    volumeUsage,
                    issues);
            }

            prepared.Add(new PreparedEncryptionItem(
                request.ItemId,
                singleRequest,
                new VideoPreflightResult(preflight.RequiredBytes, preflight.AvailableBytes, issues),
                resolution.HadConflict));
        }

        var runnable = prepared.Where(item => item.CanRun).ToArray();
        var summary = new VideoQueueBatchSummary(
            requests.Count + Math.Max(0, skippedSucceededCount),
            runnable.Length,
            conflictCount,
            prepared.Count(item => item.Preflight.Issues.Any(issue =>
                issue.Severity == PreflightSeverity.Warning)),
            prepared.Count(item => !item.CanRun),
            Math.Max(0, skippedSucceededCount),
            runnable.Aggregate(0L, (total, item) => SaturatingAdd(total, item.RequiredBytes)));

        return new BatchEncryptionPlan(
            Guid.NewGuid(),
            summary,
            prepared,
            Array.Empty<VideoPreflightIssue>());
    }

    private static VideoEncryptionRequest ToSingleFileRequest(
        BatchEncryptionItemRequest request,
        string outputPath) =>
        new(
            request.InputPath,
            outputPath,
            request.PublicTitle,
            request.PublicDescription);

    private static void ApplyCumulativeVolumeCheck(
        string outputPath,
        long requiredBytes,
        long? availableBytes,
        IDictionary<string, VolumeUsage> usageByVolume,
        ICollection<VideoPreflightIssue> issues)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
        if (string.IsNullOrWhiteSpace(root))
            return;

        usageByVolume.TryGetValue(root, out var previous);
        var cumulative = SaturatingAdd(previous.RequiredBytes, Math.Max(0, requiredBytes));
        var knownAvailable = MinKnown(previous.AvailableBytes, availableBytes);
        usageByVolume[root] = new VolumeUsage(cumulative, knownAvailable);

        if (knownAvailable is long available && cumulative > available)
        {
            issues.Add(Blocking(
                VideoTaskFailureCode.InsufficientDiskSpace,
                "该卷的剩余空间不足以容纳本批次累计到当前项目的输出。",
                "释放磁盘空间、移除前面的任务或修改部分项目的输出位置。"));
        }
    }

    private static long? MinKnown(long? left, long? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return Math.Min(left.Value, right.Value);
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

    /// <summary>
    /// 一个输出卷在当前计划中的累计预算；值类型的默认值正好表示尚未占用空间。
    /// </summary>
    private readonly record struct VolumeUsage(long RequiredBytes, long? AvailableBytes);
}
