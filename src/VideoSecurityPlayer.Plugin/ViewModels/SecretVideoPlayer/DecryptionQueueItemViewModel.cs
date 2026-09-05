using CommunityToolkit.Mvvm.ComponentModel;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 一个 SECVID03 候选在当前解密 Document 队列中的公开状态。
/// </summary>
/// <remarks>
/// 候选公开信息和共用运行状态通过组合保持独立；ItemId 负责拒绝迟到进度。类型不持有密码，
/// 也不执行名称净化、预检或文件写入。
/// </remarks>
public partial class DecryptionQueueItemViewModel : ObservableObject
{
    /// <summary>使用新队列身份创建候选；保留给 G2 测试和简单调用方。</summary>
    public DecryptionQueueItemViewModel(DecryptionCandidate candidate)
        : this(Guid.NewGuid(), candidate)
    {
    }

    /// <summary>使用调用方提供的稳定身份创建候选。</summary>
    public DecryptionQueueItemViewModel(Guid itemId, DecryptionCandidate candidate)
    {
        ItemId = itemId;
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Status.State = candidate.IsValid ? VideoTaskState.Pending : VideoTaskState.Failed;
        Status.Message = candidate.ValidationMessage;
        Status.FailureCode = candidate.FailureCode;
    }

    /// <summary>Document 队列内稳定身份。</summary>
    public Guid ItemId { get; }

    /// <summary>加解密共用的可观察状态，不包含密码。</summary>
    public VideoQueueItemStatusViewModel Status { get; } = new();

    [ObservableProperty] private DecryptionCandidate _candidate;

    public string InputPath => Candidate.InputPath;
    public string EncryptedFileName => Candidate.EncryptedFileName;
    public string PublicTitle => Candidate.PublicTitle;
    public bool HasPublicTitle => !string.IsNullOrWhiteSpace(PublicTitle);

    // 以下代理保留 G2 测试和现有 XAML 契约；状态的唯一存储仍是组合对象 Status。
    public VideoTaskState State
    {
        get => Status.State;
        set
        {
            if (Status.State == value)
                return;
            Status.State = value;
            RaiseStatusProxyProperties();
        }
    }

    public double Progress
    {
        get => Status.Progress;
        set
        {
            Status.Progress = value;
            OnPropertyChanged();
        }
    }

    public string Message
    {
        get => Status.Message;
        set
        {
            Status.Message = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMessage));
        }
    }

    public string OutputPath
    {
        get => Status.OutputPath;
        set
        {
            Status.OutputPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOutputPath));
        }
    }

    public VideoTaskFailureCode? FailureCode
    {
        get => Status.FailureCode;
        set
        {
            Status.FailureCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FailureCodeText));
            OnPropertyChanged(nameof(HasFailureCode));
        }
    }

    public string FailureCodeText => Status.FailureCodeText;
    public bool HasFailureCode => Status.HasFailureCode;
    public string StateText => Status.StateText;
    public bool HasMessage => Status.HasMessage;
    public bool HasOutputPath => Status.HasOutputPath;
    public bool IsRunning => Status.IsRunning;

    partial void OnCandidateChanged(DecryptionCandidate value)
    {
        OnPropertyChanged(nameof(InputPath));
        OnPropertyChanged(nameof(EncryptedFileName));
        OnPropertyChanged(nameof(PublicTitle));
        OnPropertyChanged(nameof(HasPublicTitle));
    }

    /// <summary>使用重新读取的公开信息替换候选并恢复等待状态。</summary>
    public void ApplyInspection(DecryptionCandidate candidate)
    {
        Candidate = candidate;
        Progress = 0;
        OutputPath = string.Empty;
        FailureCode = candidate.FailureCode;
        Message = candidate.ValidationMessage;
        State = candidate.IsValid ? VideoTaskState.Pending : VideoTaskState.Failed;
    }

    /// <summary>应用带相同 ItemId 的不可变预检项目。</summary>
    public void ApplyPreflight(CandidateDecryptionPreflight preflight)
    {
        OutputPath = preflight.OutputPath;
        var blocker = preflight.Result.Issues.FirstOrDefault(issue =>
            issue.Severity == PreflightSeverity.Blocking);
        if (blocker is not null)
        {
            State = VideoTaskState.Failed;
            FailureCode = blocker.Code;
            Message = $"{blocker.Message} {blocker.SuggestedAction}";
            return;
        }

        State = VideoTaskState.Ready;
        FailureCode = null;
        Message = preflight.Result.Issues.Count == 0
            ? "预检通过"
            : string.Join(" ", preflight.Result.Issues.Select(issue =>
                $"{issue.Message} {issue.SuggestedAction}"));
    }

    /// <summary>应用通过 RunId/ItemId 校验后的公共队列进度。</summary>
    public void ApplyProgress(VideoQueueProgress progress)
    {
        State = progress.State;
        Progress = progress.FilePercentage;
        Message = progress.Message;
        FailureCode = progress.FailureCode;
    }

    /// <summary>将失败或取消项目恢复为等待重新检查。</summary>
    public void ResetForRetry()
    {
        if (!VideoQueueInteractionPolicy.CanRetry(State))
            return;

        Status.ResetForRetry();
        RaiseStatusProxyProperties();
    }

    private void RaiseStatusProxyProperties()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HasOutputPath));
        OnPropertyChanged(nameof(FailureCodeText));
        OnPropertyChanged(nameof(HasFailureCode));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(FailureCode));
    }
}
