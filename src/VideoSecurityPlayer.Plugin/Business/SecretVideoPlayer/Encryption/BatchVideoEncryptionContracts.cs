using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

/// <summary>
/// 批量加密预检的单项请求。
/// </summary>
/// <remarks>
/// <see cref="ItemId"/> 是 Document 队列身份，其他字段是生成 SECVID03 所需的公开请求。
/// 此类型及其派生计划严禁增加密码成员；密码只能在执行单项时由 Document 直接传给
/// <see cref="IVideoEncryptionService.EncryptAsync"/>。
/// </remarks>
public sealed record BatchEncryptionItemRequest(
    Guid ItemId,
    string InputPath,
    string RequestedOutputPath,
    string PublicTitle,
    string PublicDescription);

/// <summary>
/// 已完成批次预检、可以交给严格顺序运行器的加密项目。
/// </summary>
/// <remarks>
/// <see cref="Request"/> 保存的是计划确定的最终输出路径。它只是不可变预检快照，
/// 执行前仍由 G2 单文件服务复检，不能把本类型视为文件锁或覆盖授权。
/// </remarks>
public sealed record PreparedEncryptionItem(
    Guid ItemId,
    VideoEncryptionRequest Request,
    VideoPreflightResult Preflight,
    bool HadOutputConflict) : IPreparedVideoQueueItem
{
    /// <inheritdoc />
    public long RequiredBytes => Math.Max(0, Preflight.RequiredBytes);

    /// <summary>该项目在当前计划中是否允许执行。</summary>
    public bool CanRun => Preflight.CanProceed;
}

/// <summary>
/// “检查批次”产生的不可变加密计划。
/// </summary>
/// <remarks>
/// PlanId 用于诊断和测试计划替换，队列修订号由 ViewModel 持有。计划不保存密码，
/// 因而可以安全地在用户修改密码时继续使用；任何会改变输出或公开信息的编辑必须使计划失效。
/// </remarks>
public sealed record BatchEncryptionPlan(
    Guid PlanId,
    VideoQueueBatchSummary Summary,
    IReadOnlyList<PreparedEncryptionItem> Items,
    IReadOnlyList<VideoPreflightIssue> OverallIssues);

/// <summary>
/// 批量加密的计划应用服务。
/// </summary>
/// <remarks>
/// 本服务只负责编排批次级路径和空间预检，不执行密码学，也不持有 Document 队列状态。
/// 单文件格式验证和真实执行继续委托给 <see cref="IVideoEncryptionService"/>。
/// </remarks>
public interface IVideoBatchEncryptionService
{
    /// <summary>
    /// 顺序检查未成功项目并生成不可变执行计划。
    /// </summary>
    /// <param name="requests">当前队列修订对应的请求快照。</param>
    /// <param name="conflictPolicy">用户在检查前明确选择的非覆盖冲突策略。</param>
    /// <param name="skippedSucceededCount">当前队列中将被跳过的已成功项目数。</param>
    /// <param name="cancellationToken">取消检查，不会启动任何加密。</param>
    Task<BatchEncryptionPlan> PrepareAsync(
        IReadOnlyList<BatchEncryptionItemRequest> requests,
        OutputConflictPolicy conflictPolicy,
        int skippedSucceededCount,
        CancellationToken cancellationToken = default);
}
