using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;

public interface ISecvid03Decryptor
{
    Task DecryptAsync(
        string inputPath,
        string outputPath,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IVideoDecryptionService
{
    Task<IReadOnlyList<DecryptionCandidate>> InspectAsync(
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken = default);

    Task<BatchDecryptionPreflightResult> PreflightAsync(
        IReadOnlyList<DecryptionCandidate> candidates,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用稳定队列身份和显式非覆盖策略生成 G5 执行计划。
    /// </summary>
    async Task<BatchDecryptionPreflightResult> PreflightAsync(
        IReadOnlyList<DecryptionQueueRequest> requests,
        string outputDirectory,
        OutputConflictPolicy conflictPolicy,
        CancellationToken cancellationToken = default)
    {
        // 默认实现只用于兼容已有测试替身和外部轻量适配器。生产 VideoDecryptionService
        // 覆盖此方法以真正执行显式冲突策略；旧适配器仍可获得稳定 ItemId。
        var legacy = await PreflightAsync(
                requests.Select(request => request.Candidate).ToArray(),
                outputDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var items = legacy.Items.Select((item, index) => new CandidateDecryptionPreflight(
            index < requests.Count ? requests[index].ItemId : item.ItemId,
            item.Candidate,
            item.OutputPath,
            item.Result)).ToArray();
        return new BatchDecryptionPreflightResult(legacy.Overall, items);
    }

    /// <summary>
    /// 执行一个已经预检的解密项目。密码只允许由当前 Document 在调用时传入。
    /// </summary>
    async Task DecryptAsync(
        CandidateDecryptionPreflight item,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 兼容适配把单项调用桥接到旧批次接口。生产服务覆盖此实现，直接使用计划中的
        // 最终输出路径，避免再次进行输出分配。
        var directory = Path.GetDirectoryName(item.OutputPath) ?? string.Empty;
        var bridge = progress is null
            ? null
            : new Progress<BatchDecryptionProgress>(value => progress.Report(
                new VideoTaskProgress(
                    value.State,
                    value.ProcessedBytes,
                    value.TotalBytes,
                    value.FilePercentage,
                    value.Message,
                    value.FailureCode)));
        await DecryptBatchAsync(
                [item.Candidate],
                directory,
                password,
                bridge,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// G2 兼容批次入口。G5 Document 使用公共顺序运行器调用单项 <see cref="DecryptAsync"/>。
    /// </summary>
    Task<BatchDecryptionResult> DecryptBatchAsync(
        IReadOnlyList<DecryptionCandidate> candidates,
        string outputDirectory,
        string password,
        IProgress<BatchDecryptionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
