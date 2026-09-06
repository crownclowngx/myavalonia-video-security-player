namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>将后端报告的轨道 ID 投影为当前可见选择，不发送原生命令、不改变轨道列表。</summary>
/// <remarks>
/// 轨道 ID 不是列表下标。未知音轨保留“未选择”，未知字幕优先回退到真实存在的关闭项 -1；
/// 若列表没有关闭项，也保留未选择。媒体初次加载与表面恢复失败共用此规则，避免两处回退漂移。
/// 是否尝试恢复、失败提示以及快照提交时机仍由持有统一操作门的播放会话决定。
/// </remarks>
internal static class PlaybackTrackSelectionPolicy
{
    public static int? SelectAudio(IReadOnlyList<PlaybackTrackOption> tracks, int currentId) =>
        tracks.Any(option => option.Id == currentId) ? currentId : null;

    public static int? SelectSubtitle(IReadOnlyList<PlaybackTrackOption> tracks, int currentId) =>
        SelectAudio(tracks, currentId) ?? SelectAudio(tracks, -1);
}
