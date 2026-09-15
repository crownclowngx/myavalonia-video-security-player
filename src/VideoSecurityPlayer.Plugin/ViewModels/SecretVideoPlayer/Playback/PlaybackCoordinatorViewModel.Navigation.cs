using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>日常观看控制仍通过同一播放会话执行，本文件只分组交互代码，不引入第二份播放状态。</summary>
public partial class PlaybackCoordinatorViewModel
{
    private double _lastAudibleVolume = 50;
    [ObservableProperty] private string _jumpTimeText = string.Empty;
    [ObservableProperty] private string _jumpFeedback = string.Empty;
    public bool IsMuted => Volume <= 0;
    public string MuteButtonText => IsMuted ? "恢复音量" : "静音";

    /// <summary>记住当前 Document 最后一个非零音量；用户拖动滑块恢复声音时同步更新恢复目标。</summary>
    [RelayCommand(CanExecute = nameof(CanAdjustVolume))]
    private void ToggleMute() => Volume = IsMuted ? _lastAudibleVolume : 0;

    /// <summary>输入校验失败只显示提示，不发送 Seek。播放位置及播放状态仍由原会话决定。</summary>
    [RelayCommand(CanExecute = nameof(CanSeekByShortcut))]
    private async Task JumpToTimeAsync()
    {
        if (!CanSeekByShortcut()) return;
        if (!PlaybackTimePolicy.TryParse(JumpTimeText, PlaybackSnapshot.DurationMs, out var position))
        {
            JumpFeedback = "请输入片长范围内的秒数、分:秒或时:分:秒";
            return;
        }
        var result = await SeekMediaAsync(position, waitForFrame: IsPaused);
        if (!_disposed) JumpFeedback = result.Success
            ? $"已跳转至 {PlaybackResumePolicy.FormatTime(position)}" : "跳转失败，请查看播放器状态后重试";
    }
}
