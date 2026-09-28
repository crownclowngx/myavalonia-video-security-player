using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>插件级设置 Tool 的组合根。四个子模型分别负责各组交互，不持有 scoped 播放器。</summary>
/// <remarks>SDK 负责单例生命周期；Hide 或 View 重建不释放本模型，不重复创建订阅和草稿。</remarks>
public sealed partial class VideoSettingsTool : ObservableObject, IDisposable
{
    private bool _disposed;
    public PlaybackSettingsViewModel Playback { get; }
    public LibrarySettingsViewModel Library { get; }
    public RecordSettingsViewModel Records { get; }
    public RuntimeSettingsViewModel Runtime { get; }
    public string LoadWarning { get; }
    [ObservableProperty] private bool _playbackExpanded = true;
    [ObservableProperty] private bool _libraryExpanded;
    [ObservableProperty] private bool _recordsExpanded;
    [ObservableProperty] private bool _runtimeExpanded;
    public VideoSettingsTool(IVideoSettingsReader reader, IVideoSettingsEditor editor, IPlaybackHistoryStore history,
        IVideoLibrarySettingsStore library, IUserDataMaintenance maintenance, IPlaybackRuntimeResolver resolver,
        IPlaybackRuntimeStatus runtime, IEnvironmentDiagnostics diagnostics, ISecretVideoUserDataDiagnostics loadDiagnostics)
    {
        Playback = new(reader, editor); Library = new(reader, editor);
        Records = new(reader, editor, history, library, maintenance);
        Runtime = new(reader, editor, resolver, runtime, diagnostics);
        LoadWarning = loadDiagnostics.LoadWarning;
    }
    [RelayCommand]
    public void ShowRuntime() => RuntimeExpanded = true;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Playback.Dispose(); Library.Dispose(); Records.Dispose(); Runtime.Dispose();
    }
}
