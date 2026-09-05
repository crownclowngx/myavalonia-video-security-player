using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;

public sealed record DecryptionCandidate(
    string InputPath,
    string EncryptedFileName,
    string OriginalFileName,
    string OriginalExtension,
    string PublicTitle,
    long OriginalFileLength,
    bool IsValid,
    string ValidationMessage,
    VideoTaskFailureCode? FailureCode = null);

/// <summary>
/// 把解密候选与当前 Document 队列的稳定身份关联。
/// </summary>
/// <remarks>
/// 同一路径可能在失败后被重新添加，路径本身不能可靠地区分迟到进度；ItemId 只服务于
/// 队列生命周期，不写入磁盘，也不包含密码。
/// </remarks>
public sealed record DecryptionQueueRequest(
    Guid ItemId,
    DecryptionCandidate Candidate);

/// <summary>
/// 已分配安全输出路径并完成预检的解密项目。
/// </summary>
public sealed record CandidateDecryptionPreflight(
    Guid ItemId,
    DecryptionCandidate Candidate,
    string OutputPath,
    VideoPreflightResult Result) : IPreparedVideoQueueItem
{
    /// <summary>兼容 G2 调用方的构造函数；新队列必须传入真实 ItemId。</summary>
    public CandidateDecryptionPreflight(
        DecryptionCandidate candidate,
        string outputPath,
        VideoPreflightResult result)
        : this(Guid.Empty, candidate, outputPath, result)
    {
    }

    /// <inheritdoc />
    public long RequiredBytes => Math.Max(0, Result.RequiredBytes);

    /// <summary>当前计划是否允许执行此项目。</summary>
    public bool CanRun => Result.CanProceed;
}

/// <summary>批量解密预检结果；不包含密码。</summary>
public sealed record BatchDecryptionPreflightResult(
    VideoPreflightResult Overall,
    IReadOnlyList<CandidateDecryptionPreflight> Items)
{
    /// <summary>
    /// 全局输出环境必须可用，且至少一个单项通过预检，批次才允许开始。
    /// </summary>
    public bool HasRunnableItems =>
        Overall.CanProceed && Items.Any(item => item.Result.CanProceed);
}

/// <summary>
/// G2 兼容批次进度。G5 UI 使用带 RunId/ItemId 的 <see cref="VideoQueueProgress"/>。
/// </summary>
public sealed record BatchDecryptionProgress(
    string InputPath,
    string OutputPath,
    VideoTaskState State,
    long ProcessedBytes,
    long TotalBytes,
    double FilePercentage,
    double OverallPercentage,
    string Message,
    VideoTaskFailureCode? FailureCode = null);

/// <summary>
/// G2 兼容批次结果。G5 UI 使用公共 <see cref="VideoQueueRunResult"/>。
/// </summary>
public sealed record BatchDecryptionResult(
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    IReadOnlyList<string> OutputPaths);
