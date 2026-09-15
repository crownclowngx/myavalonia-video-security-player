using CommunityToolkit.Mvvm.ComponentModel;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Models.SecretVideoPlayer;

/// <summary>
/// 视频库列表中的单个 SECVID03 文件。
/// </summary>
public sealed class VideoLibraryItemViewModel : ObservableObject
{
    public VideoLibraryItemViewModel(
        VideoLibraryScanResult result,
        VideoPlaybackHistoryEntry? history = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        Source = result;
        FilePath = result.FilePath;
        FileNameWithoutExtension = result.FileNameWithoutExtension;
        PublicTitle = result.PublicTitle;
        PublicDescription = result.PublicDescription;
        MetadataState = result.State;
        ErrorMessage = result.ErrorMessage;
        LastWriteTimeUtc = result.LastWriteTimeUtc;
        FileLength = result.FileLength;
        OriginalFileLength = result.OriginalFileLength;
        FileId = result.FileId;
        LastPlayedUtc = history?.LastPlayedUtc;
        HistoryPositionMs = history?.PositionMs ?? 0;
        HistoryDurationMs = history?.DurationMs ?? 0;
        HistoryState = history is null
            ? VideoPlaybackHistoryState.Unplayed
            : history.State;
    }

    internal VideoLibraryScanResult Source { get; }
    public string FilePath { get; }
    public string FileNameWithoutExtension { get; }
    public string PublicTitle { get; }
    public string PublicDescription { get; }
    public VideoLibraryMetadataState MetadataState { get; }
    public string ErrorMessage { get; }
    public DateTimeOffset LastWriteTimeUtc { get; }
    public long FileLength { get; }
    public long OriginalFileLength { get; }
    public string FileId { get; }
    public DateTimeOffset? LastPlayedUtc { get; }
    public long HistoryPositionMs { get; }
    public long HistoryDurationMs { get; }
    public VideoPlaybackHistoryState HistoryState { get; }
    public bool HasError => MetadataState == VideoLibraryMetadataState.Failed;
    public bool HasPublicTitle => !string.IsNullOrWhiteSpace(PublicTitle);

    public string DisplayName => string.IsNullOrWhiteSpace(PublicTitle)
        ? FileNameWithoutExtension
        : $"{FileNameWithoutExtension}（{PublicTitle}）";

    public string ModifiedTimeText => LastWriteTimeUtc == default
        ? string.Empty
        : LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LastPlayedTimeText => LastPlayedUtc is null
        ? "未播放"
        : LastPlayedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private string _currentPlaybackText = string.Empty;
    public string CurrentPlaybackText => _currentPlaybackText;
    public bool IsCurrentMedia => _currentPlaybackText.Length > 0;
    public bool HasHistoryProgress => HistoryDurationMs > 0;
    public double HistoryPercentage => HasHistoryProgress
        ? Math.Clamp(100.0 * HistoryPositionMs / HistoryDurationMs, 0, 100) : 0;
    public string HistoryProgressText => HasHistoryProgress
        ? $"{PlaybackResumePolicy.FormatTime(HistoryPositionMs)} / {PlaybackResumePolicy.FormatTime(HistoryDurationMs)}"
        : string.Empty;

    /// <summary>实时状态由文档协调器投影，历史快照保持不变，防止“未看完”被误认为正在播放。</summary>
    public void SetPlaybackState(PlaybackState? state)
    {
        var text = state switch
        {
            PlaybackState.Playing => "正在播放",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Ready or PlaybackState.Stopped => "当前视频",
            _ => string.Empty
        };
        if (SetProperty(ref _currentPlaybackText, text, nameof(CurrentPlaybackText)))
            OnPropertyChanged(nameof(IsCurrentMedia));
    }

    public string HistoryStateText => HistoryState switch
    {
        VideoPlaybackHistoryState.InProgress => "未看完",
        VideoPlaybackHistoryState.Completed => "已看完",
        _ => "未播放"
    };
}
