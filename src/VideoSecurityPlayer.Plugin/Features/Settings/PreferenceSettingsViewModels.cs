using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>播放默认值的编辑草稿；仅向编辑端口提交，不直接调整任何播放器。</summary>
public sealed partial class PlaybackSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IVideoSettingsEditor _editor;
    [ObservableProperty] private bool _rememberVolume;
    [ObservableProperty] private int _fixedVolume;
    [ObservableProperty] private bool _rememberRate;
    [ObservableProperty] private float _fixedRate;
    [ObservableProperty] private bool _resume;
    [ObservableProperty] private int _seekSeconds;
    public IReadOnlyList<float> Rates { get; } = [0.5f, 0.75f, 1, 1.25f, 1.5f, 2];
    public IReadOnlyList<int> SeekSteps { get; } = [5, 10, 30];
    public SettingsSaveFeedback Save { get; } = new();
    public PlaybackSettingsViewModel(IVideoSettingsReader reader, IVideoSettingsEditor editor)
    {
        _editor = editor;
        var value = reader.CurrentVideoSettings.Playback;
        (_rememberVolume, _fixedVolume, _rememberRate, _fixedRate, _resume, _seekSeconds) =
            (value.RememberVolume, value.FixedVolume, value.RememberRate, value.FixedRate, value.Resume, value.SeekSeconds);
    }
    private void Persist()
    {
        var value = new PlaybackDefaults(RememberVolume, FixedVolume, RememberRate, FixedRate, Resume, SeekSeconds);
        _ = Save.RunAsync(token => _editor.SavePlaybackAsync(value, token));
    }
    partial void OnRememberVolumeChanged(bool value) => Persist();
    partial void OnFixedVolumeChanged(int value) => Persist();
    partial void OnRememberRateChanged(bool value) => Persist();
    partial void OnFixedRateChanged(float value) => Persist();
    partial void OnResumeChanged(bool value) => Persist();
    partial void OnSeekSecondsChanged(int value) => Persist();
    public void Dispose() => Save.Dispose();
}

/// <summary>媒体库默认策略的编辑草稿；布局重置只作用于之后新建的媒体库。</summary>
public sealed partial class LibrarySettingsViewModel : ObservableObject, IDisposable
{
    private readonly IVideoSettingsEditor _editor;
    private LibraryDefaults _layout;
    [ObservableProperty] private bool _restoreFolder;
    [ObservableProperty] private bool _recursive;
    [ObservableProperty] private VideoLibrarySortField _sortField;
    [ObservableProperty] private VideoLibrarySortDirection _sortDirection;
    public IReadOnlyList<VideoLibrarySortField> SortFields { get; } = Enum.GetValues<VideoLibrarySortField>();
    public IReadOnlyList<VideoLibrarySortDirection> SortDirections { get; } = Enum.GetValues<VideoLibrarySortDirection>();
    public SettingsSaveFeedback Save { get; } = new();
    public LibrarySettingsViewModel(IVideoSettingsReader reader, IVideoSettingsEditor editor)
    {
        _editor = editor;
        _layout = reader.CurrentVideoSettings.Library;
        (_restoreFolder, _recursive, _sortField, _sortDirection) = (_layout.RestoreFolder, _layout.Recursive, _layout.SortField, _layout.SortDirection);
    }
    private void Persist()
    {
        var value = _layout with { RestoreFolder = RestoreFolder, Recursive = Recursive, SortField = SortField, SortDirection = SortDirection };
        _ = Save.RunAsync(token => _editor.SaveLibraryAsync(value, token));
    }
    partial void OnRestoreFolderChanged(bool value) => Persist();
    partial void OnRecursiveChanged(bool value) => Persist();
    partial void OnSortFieldChanged(VideoLibrarySortField value) => Persist();
    partial void OnSortDirectionChanged(VideoLibrarySortDirection value) => Persist();
    [RelayCommand]
    private void ResetLayout() { _layout = _layout with { PaneOpen = true, PaneWidth = 400, SettingsExpanded = false }; Persist(); }
    public void Dispose() => Save.Dispose();
}
