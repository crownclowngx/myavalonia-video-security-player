using CommunityToolkit.Mvvm.ComponentModel;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 加密和解密队列项目共用的可观察运行状态。
/// </summary>
/// <remarks>
/// 状态与领域请求使用组合而非继承：队列交互可以共享，输入检查、输出分配和公开信息仍由
/// 加密/解密各自模型负责。此对象不持有密码，也不执行文件操作。
/// </remarks>
public partial class VideoQueueItemStatusViewModel : ObservableObject
{
    [ObservableProperty] private VideoTaskState _state = VideoTaskState.Pending;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private VideoTaskFailureCode? _failureCode;

    /// <summary>面向中文界面的稳定状态文本。</summary>
    public string StateText => State switch
    {
        VideoTaskState.Pending => "等待",
        VideoTaskState.Preflighting => "预检中",
        VideoTaskState.Ready => "就绪",
        VideoTaskState.Running => "处理中",
        VideoTaskState.Succeeded => "完成",
        VideoTaskState.Failed => "失败",
        VideoTaskState.Cancelled => "已取消",
        _ => string.Empty
    };

    /// <summary>仅 Running 状态显示单项进度条。</summary>
    public bool IsRunning => State == VideoTaskState.Running;

    /// <summary>成功项显示正式输出，其他状态优先显示解释消息。</summary>
    public bool HasOutputPath =>
        State == VideoTaskState.Succeeded && !string.IsNullOrWhiteSpace(OutputPath);

    /// <summary>成功项不重复显示完成消息，减少百文件队列的视觉噪声。</summary>
    public bool HasMessage =>
        State != VideoTaskState.Succeeded && !string.IsNullOrWhiteSpace(Message);

    /// <summary>稳定失败代码可以复制给测试或故障报告，但不暴露原始异常。</summary>
    public bool HasFailureCode => FailureCode.HasValue;

    /// <summary>失败代码的绑定文本。</summary>
    public string FailureCodeText => FailureCode?.ToString() ?? string.Empty;

    partial void OnStateChanged(VideoTaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasOutputPath));
        OnPropertyChanged(nameof(HasMessage));
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(HasOutputPath));

    partial void OnFailureCodeChanged(VideoTaskFailureCode? value)
    {
        OnPropertyChanged(nameof(HasFailureCode));
        OnPropertyChanged(nameof(FailureCodeText));
    }

    /// <summary>
    /// 将失败或取消项目恢复为未检查状态；成功项目必须由调用者显式跳过。
    /// </summary>
    public void ResetForRetry()
    {
        if (!VideoQueueInteractionPolicy.CanRetry(State))
            return;

        State = VideoTaskState.Pending;
        Progress = 0;
        Message = string.Empty;
        OutputPath = string.Empty;
        FailureCode = null;
    }
}
