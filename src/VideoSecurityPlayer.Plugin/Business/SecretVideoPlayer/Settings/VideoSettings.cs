using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

/// <summary>新文档使用的播放默认策略；固定值与播放器最近实际使用值分开保存。</summary>
public sealed record PlaybackDefaults(bool RememberVolume = true, int FixedVolume = 50,
    bool RememberRate = true, float FixedRate = 1, bool Resume = true, int SeekSeconds = 5);

/// <summary>新媒体库的默认值。修改当前库的排序或目录不会反向修改此配置。</summary>
public sealed record LibraryDefaults(bool RestoreFolder = true, bool Recursive = false,
    VideoLibrarySortField SortField = VideoLibrarySortField.FileName,
    VideoLibrarySortDirection SortDirection = VideoLibrarySortDirection.Ascending,
    bool PaneOpen = true, bool SettingsExpanded = false, double PaneWidth = 400);

/// <summary>自动选择按 Common、内置的顺序预检；自定义只接受明确选择的目录。</summary>
public enum RuntimeSourceMode { Auto, Custom }

/// <summary>已保存的运行库选择，不代表当前进程实际绑定的运行库。</summary>
public sealed record RuntimeSelection(RuntimeSourceMode Mode = RuntimeSourceMode.Auto, string Directory = "");

/// <summary>插件级默认配置的不可变快照；不包含密码、播放器或任何原生对象。</summary>
public sealed record VideoSettings(PlaybackDefaults Playback, LibraryDefaults Library,
    bool RecordHistory, RuntimeSelection Runtime)
{
    public static VideoSettings Default { get; } = new(new(), new(), true, new());
}

/// <summary>播放器只读取已提交的配置；快照在文档创建时固定。</summary>
public interface IVideoSettingsReader
{
    VideoSettings CurrentVideoSettings { get; }
    event EventHandler? SettingsChanged;
}

/// <summary>保存结果只表达稳定的用户反馈，不泄露磁盘异常中的路径或内容。</summary>
public sealed record SettingsSaveResult(bool Success, string Message);

/// <summary>设置界面的编辑端口。每个方法只修改一个分组，避免旧文档覆盖新配置。</summary>
public interface IVideoSettingsEditor
{
    Task<SettingsSaveResult> SavePlaybackAsync(PlaybackDefaults value, CancellationToken token = default);
    Task<SettingsSaveResult> SaveLibraryAsync(LibraryDefaults value, CancellationToken token = default);
    Task<SettingsSaveResult> SaveRecordingAsync(bool enabled, CancellationToken token = default);
    Task<SettingsSaveResult> SaveRuntimeAsync(RuntimeSelection value, CancellationToken token = default);
    Task<SettingsSaveResult> RetryAsync(CancellationToken token = default);
}

/// <summary>维护操作在实际写入成功后才发布已清理的数据；不接触媒体文件。</summary>
public interface IUserDataMaintenance
{
    Task<SettingsSaveResult> ClearHistoryAsync(CancellationToken token = default);
    Task<SettingsSaveResult> RemoveHistoryAsync(string path, string fileId, long length, CancellationToken token = default);
    Task<SettingsSaveResult> RemoveRecentFolderAsync(string? path, CancellationToken token = default);
}

/// <summary>在回调开始时捕获的记录令牌；不能在最终提交前重新捕获来绕过清除屏障。</summary>
public readonly record struct RecordingToken(long Revision, bool Enabled);

/// <summary>历史协调器的写入端口。存储必须在提交的同一锁内检查令牌。</summary>
public interface IPlaybackHistoryWriter
{
    event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryInvalidated;
    RecordingToken CaptureRecordingToken();
    bool TryUpsert(VideoPlaybackHistoryEntry entry, RecordingToken token);
}

/// <summary>记录屏障的纯规则，禁用与清除后到达的旧请求均不可提交。</summary>
public static class RecordingPolicy
{
    public static bool CanWrite(RecordingToken observed, RecordingToken current) =>
        observed.Enabled && current.Enabled && observed.Revision == current.Revision;
}

/// <summary>默认值校验与新文档选值规则，无文件系统或 UI 依赖。</summary>
public static class VideoDefaultsPolicy
{
    public static float NormalizeRate(float value) => value is 0.5f or 0.75f or 1f or 1.25f or 1.5f or 2f ? value : 1f;

    public static PlaybackDefaults Normalize(PlaybackDefaults value) => value with
    {
        FixedVolume = Math.Clamp(value.FixedVolume, 0, 100),
        FixedRate = NormalizeRate(value.FixedRate),
        SeekSeconds = value.SeekSeconds is 5 or 10 or 30 ? value.SeekSeconds : 5
    };

    public static LibraryDefaults Normalize(LibraryDefaults value) => value with
    {
        SortField = Enum.IsDefined(value.SortField) ? value.SortField : VideoLibrarySortField.FileName,
        SortDirection = Enum.IsDefined(value.SortDirection) ? value.SortDirection : VideoLibrarySortDirection.Ascending,
        PaneWidth = double.IsFinite(value.PaneWidth) ? Math.Clamp(value.PaneWidth, 340, 600) : 400
    };

    public static PlaybackPreferences Resolve(PlaybackDefaults value, PlaybackPreferences recent)
    {
        value = Normalize(value);
        return new(Math.Clamp(value.RememberVolume ? recent.Volume : value.FixedVolume, 0, 100),
            NormalizeRate(value.RememberRate ? recent.Rate : value.FixedRate));
    }

    public static VideoLibrarySettings Resolve(LibraryDefaults value, VideoLibrarySettings recent)
    {
        value = Normalize(value);
        return recent with { RecentFolder = value.RestoreFolder ? recent.RecentFolder : "",
            IncludeSubdirectories = value.Recursive, SortField = value.SortField, SortDirection = value.SortDirection,
            IsLibraryPaneOpen = value.PaneOpen, IsLibrarySettingsExpanded = value.SettingsExpanded,
            LibraryPaneWidth = value.PaneWidth };
    }
}
