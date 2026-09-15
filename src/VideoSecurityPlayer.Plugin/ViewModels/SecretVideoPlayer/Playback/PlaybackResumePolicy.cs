using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>单文件与媒体库共用的续播规则，只处理历史数值与用户意图。</summary>
/// <remarks>
/// 本策略不读取文件、不保存密码，也不操作播放器。历史身份由调用方通过历史存储查询，
/// 加载时仍交给播放会话再次认证；把纯规则放在这里，避免两个入口产生不同的续播行为。
/// </remarks>
public static class PlaybackResumePolicy
{
    /// <summary>完成、无效或显式从头播放的历史不恢复；未知总长允许使用有效位置。</summary>
    public static long GetPosition(VideoPlaybackHistoryEntry? history, bool fromStart = false) =>
        fromStart || history is null || history.IsCompleted || history.PositionMs <= 0 ||
        (history.DurationMs > 0 && history.PositionMs >= history.DurationMs)
            ? 0 : history.PositionMs;

    /// <summary>超过一小时的时间包含小时，避免长视频的位置显示发生回绕。</summary>
    public static string FormatTime(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public static string PlayLabel(long positionMs) => positionMs > 0
        ? $"继续播放 · {FormatTime(positionMs)}" : "播放";
}
