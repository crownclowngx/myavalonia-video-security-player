using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Models.SecretVideoPlayer;

/// <summary>
/// 视频库列表中的单个 SECVID03 文件。
/// </summary>
public sealed class VideoLibraryItemViewModel
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

    public string HistoryStateText => HistoryState switch
    {
        VideoPlaybackHistoryState.InProgress => "播放中",
        VideoPlaybackHistoryState.Completed => "已看完",
        _ => "未播放"
    };
}
