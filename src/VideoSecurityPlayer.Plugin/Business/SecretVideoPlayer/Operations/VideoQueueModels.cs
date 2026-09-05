namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

/// <summary>
/// 队列预检阶段处理输出重名的策略。
/// </summary>
/// <remarks>
/// G5 刻意不提供覆盖选项。无论选择严格阻止还是安全改名，最终提交仍必须使用
/// <c>overwrite: false</c>，使预检之后出现的文件系统竞争以失败结束，而不是破坏已有文件。
/// </remarks>
public enum OutputConflictPolicy
{
    /// <summary>发现批次内或磁盘上的重名时阻止该项目。</summary>
    Block,

    /// <summary>在计划阶段分配数字后缀；不会在执行阶段静默重新分配名称。</summary>
    GenerateUniqueName
}

/// <summary>
/// 可由严格顺序运行器处理的不可变预检项目。
/// </summary>
/// <remarks>
/// 接口只暴露队列身份和进度权重，不包含密码，也不要求加密与解密共享领域模型。
/// </remarks>
public interface IPreparedVideoQueueItem
{
    /// <summary>当前 Document 队列内稳定且唯一的项目标识。</summary>
    Guid ItemId { get; }

    /// <summary>用于总体进度计算的预期输出字节数。</summary>
    long RequiredBytes { get; }
}

/// <summary>
/// 两阶段执行中“检查批次”生成的用户可见汇总。
/// </summary>
/// <remarks>
/// 可执行数与阻止数互斥；冲突数表示原请求发生过冲突，可能已经被安全改名解决；
/// 警告数按包含至少一个警告的项目计数，因此各列不要求相加等于总数。
/// </remarks>
public sealed record VideoQueueBatchSummary(
    int TotalCount,
    int RunnableCount,
    int ConflictCount,
    int WarningCount,
    int BlockingCount,
    int SkippedSucceededCount,
    long RunnableBytes);

/// <summary>
/// 顺序队列向 Document 发布的稳定进度快照。
/// </summary>
/// <remarks>
/// 路径可能被编辑、规范化或重复使用，因此不能作为进度身份。<see cref="RunId"/> 与
/// <see cref="ItemId"/> 共同保证旧批次和已移除项目的迟到回调不会污染当前 UI。
/// </remarks>
public sealed record VideoQueueProgress(
    Guid RunId,
    Guid ItemId,
    VideoTaskState State,
    long ProcessedBytes,
    long TotalBytes,
    double FilePercentage,
    double OverallPercentage,
    string Message,
    VideoTaskFailureCode? FailureCode = null);

/// <summary>
/// 一次顺序队列运行的非敏感结果。
/// </summary>
public sealed record VideoQueueRunResult(
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    int RemovedBeforeStartCount);

/// <summary>
/// 负责严格顺序执行已经预检的项目，并提供“取消当前”和“取消全部”两种不同语义。
/// </summary>
/// <typeparam name="TPreparedItem">加密或解密各自的不可变预检项目类型。</typeparam>
/// <remarks>
/// 实现是 Document-scoped 的单消费者：它只编排调用，不认识密码、SECVID03 或 UI。
/// 密码只能由 Document 捕获在 <paramref name="executeAsync"/> 的同步调用链中。
/// </remarks>
public interface ISequentialVideoQueueRunner<TPreparedItem>
    where TPreparedItem : IPreparedVideoQueueItem
{
    /// <summary>当前是否有批次占用此 Document 的运行器。</summary>
    bool IsRunning { get; }

    /// <summary>当前正在执行的项目；预检或空闲时为 <see langword="null"/>。</summary>
    Guid? CurrentItemId { get; }

    /// <summary>
    /// 按给定顺序逐项执行不可变计划。
    /// </summary>
    /// <param name="runId">本次运行身份，由 Document 用于拒绝旧回调。</param>
    /// <param name="items">开始执行时的不可变计划快照。</param>
    /// <param name="isStillQueued">开始每项前确认该项未被用户从等待队列移除。</param>
    /// <param name="executeAsync">加密或解密单项策略；不得把密码保存到项目模型。</param>
    /// <param name="progress">Document 级稳定进度接收器。</param>
    /// <param name="cancellationToken">Document 关闭等外部生命周期取消。</param>
    Task<VideoQueueRunResult> RunAsync(
        Guid runId,
        IReadOnlyList<TPreparedItem> items,
        Func<Guid, bool> isStillQueued,
        Func<TPreparedItem, IProgress<VideoTaskProgress>, CancellationToken, Task> executeAsync,
        IProgress<VideoQueueProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只取消当前项目。当前项目完成资源清理后，运行器继续处理下一等待项。
    /// </summary>
    bool CancelCurrent();

    /// <summary>
    /// 取消当前项目并停止整个批次；已经成功提交的文件不受影响。
    /// </summary>
    void CancelAll();
}

/// <summary>
/// 加密和解密页面共用的纯队列交互规则。
/// </summary>
/// <remarks>
/// 将状态判断集中为无状态策略，避免两个 ViewModel 在后续迭代中产生不同的重试、
/// 移除和清理语义。
/// </remarks>
public static class VideoQueueInteractionPolicy
{
    /// <summary>判断项目在当前运行状态下能否被显式移除。</summary>
    public static bool CanRemove(VideoTaskState state, bool queueIsRunning, bool isCurrent) =>
        state != VideoTaskState.Succeeded &&
        state != VideoTaskState.Running &&
        !isCurrent &&
        (!queueIsRunning || state is VideoTaskState.Pending or VideoTaskState.Ready);

    /// <summary>只有失败或取消项目可以回到等待状态。</summary>
    public static bool CanRetry(VideoTaskState state) =>
        state is VideoTaskState.Failed or VideoTaskState.Cancelled;

    /// <summary>“清空已完成”只清理已经成功提交的项目。</summary>
    public static bool CanClearCompleted(VideoTaskState state) =>
        state == VideoTaskState.Succeeded;
}
