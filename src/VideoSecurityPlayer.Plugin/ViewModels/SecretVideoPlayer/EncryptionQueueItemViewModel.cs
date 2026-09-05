using CommunityToolkit.Mvvm.ComponentModel;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 一个普通视频在当前加密 Document 队列中的公开、可编辑状态。
/// </summary>
/// <remarks>
/// 项目保存输入、建议输出和公开信息，但刻意没有密码字段。编辑回调只负责使旧预检计划
/// 失效，真实文件检查仍由批量加密应用服务完成。
/// </remarks>
public partial class EncryptionQueueItemViewModel : ObservableObject
{
    private readonly Action<EncryptionQueueItemViewModel> _requestChanged;

    /// <summary>创建具有稳定队列身份和源文件旁默认输出的项目。</summary>
    public EncryptionQueueItemViewModel(
        Guid itemId,
        string inputPath,
        string requestedOutputPath,
        Action<EncryptionQueueItemViewModel> requestChanged)
    {
        ItemId = itemId;
        InputPath = inputPath ?? throw new ArgumentNullException(nameof(inputPath));
        _requestedOutputPath = requestedOutputPath ?? throw new ArgumentNullException(nameof(requestedOutputPath));
        _requestChanged = requestChanged ?? throw new ArgumentNullException(nameof(requestChanged));
    }

    /// <summary>Document 内不随编辑变化的项目身份。</summary>
    public Guid ItemId { get; }

    /// <summary>规范化后的普通视频输入路径。</summary>
    public string InputPath { get; }

    /// <summary>列表中显示的源文件名。</summary>
    public string FileName => Path.GetFileName(InputPath);

    /// <summary>可观察运行状态；不包含领域请求和密码。</summary>
    public VideoQueueItemStatusViewModel Status { get; } = new();

    [ObservableProperty] private string _requestedOutputPath;
    [ObservableProperty] private string _publicTitle = string.Empty;
    [ObservableProperty] private string _publicDescription = string.Empty;
    [ObservableProperty] private string _preparedOutputPath = string.Empty;

    /// <summary>公开标题的 Unicode Rune 数，和 SECVID03 校验口径一致。</summary>
    public int TitleCharacterCount => EncryptedVideoContainer.CountRunes(PublicTitle);

    /// <summary>公开描述的 Unicode Rune 数，和 SECVID03 校验口径一致。</summary>
    public int DescriptionCharacterCount => EncryptedVideoContainer.CountRunes(PublicDescription);

    partial void OnRequestedOutputPathChanged(string value) => OnRequestChanged();

    partial void OnPublicTitleChanged(string value)
    {
        OnPropertyChanged(nameof(TitleCharacterCount));
        OnRequestChanged();
    }

    partial void OnPublicDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(DescriptionCharacterCount));
        OnRequestChanged();
    }

    /// <summary>生成不含密码的批量预检请求快照。</summary>
    public BatchEncryptionItemRequest CreateRequest() =>
        new(ItemId, InputPath, RequestedOutputPath, PublicTitle, PublicDescription);

    /// <summary>
    /// 应用不可变预检项目。阻止项保留在队列中供用户修正和重试，不影响其他 Ready 项。
    /// </summary>
    public void ApplyPreflight(PreparedEncryptionItem prepared)
    {
        PreparedOutputPath = prepared.Request.OutputPath;
        Status.OutputPath = prepared.Request.OutputPath;
        Status.Progress = 0;
        var blocker = prepared.Preflight.Issues.FirstOrDefault(issue =>
            issue.Severity == PreflightSeverity.Blocking);
        if (blocker is not null)
        {
            Status.State = VideoTaskState.Failed;
            Status.FailureCode = blocker.Code;
            Status.Message = $"{blocker.Message} {blocker.SuggestedAction}";
            return;
        }

        Status.State = VideoTaskState.Ready;
        Status.FailureCode = null;
        Status.Message = prepared.Preflight.Issues.Count == 0
            ? "预检通过"
            : string.Join(" ", prepared.Preflight.Issues.Select(issue =>
                $"{issue.Message} {issue.SuggestedAction}"));
    }

    /// <summary>应用经过 RunId/ItemId 校验的运行进度。</summary>
    public void ApplyProgress(VideoQueueProgress progress)
    {
        Status.State = progress.State;
        Status.Progress = progress.FilePercentage;
        Status.Message = progress.Message;
        Status.FailureCode = progress.FailureCode;
        if (progress.State == VideoTaskState.Succeeded)
            Status.OutputPath = PreparedOutputPath;
    }

    /// <summary>将失败或取消项目恢复为等待重新预检。</summary>
    public void ResetForRetry()
    {
        Status.ResetForRetry();
        PreparedOutputPath = string.Empty;
    }

    private void OnRequestChanged()
    {
        // 成功项对应的正式文件已经提交，不允许编辑使其重新进入队列。
        if (Status.State == VideoTaskState.Succeeded)
            return;

        PreparedOutputPath = string.Empty;
        Status.Progress = 0;
        Status.OutputPath = string.Empty;
        Status.FailureCode = null;
        Status.Message = string.Empty;
        Status.State = VideoTaskState.Pending;
        _requestChanged(this);
    }
}
