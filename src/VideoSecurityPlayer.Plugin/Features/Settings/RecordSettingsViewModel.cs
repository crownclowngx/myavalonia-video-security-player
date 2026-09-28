using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>记录策略与清理的界面协调器；只维护确认状态和投影，不缓存第二份历史。</summary>
public sealed partial class RecordSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IVideoSettingsReader _reader;
    private readonly IVideoSettingsEditor _editor;
    private readonly IPlaybackHistoryStore _history;
    private readonly IVideoLibrarySettingsStore _library;
    private readonly IUserDataMaintenance _maintenance;
    private readonly CapturedUiScheduler _ui = new();
    private bool _disposed;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _confirmHistory;
    [ObservableProperty] private bool _confirmFolders;
    [ObservableProperty] private string? _selectedFolder;
    public int HistoryCount => _history.GetAll().Count;
    public IReadOnlyList<string> RecentFolders => _library.CurrentSettings.RecentFolders ?? [];
    public SettingsSaveFeedback Save { get; } = new();
    public SettingsSaveFeedback Maintenance { get; } = new();

    public RecordSettingsViewModel(IVideoSettingsReader reader, IVideoSettingsEditor editor, IPlaybackHistoryStore history,
        IVideoLibrarySettingsStore library, IUserDataMaintenance maintenance)
    {
        (_reader, _editor, _history, _library, _maintenance) = (reader, editor, history, library, maintenance);
        _enabled = reader.CurrentVideoSettings.RecordHistory;
        _history.HistoryChanged += HistoryChanged;
        _reader.SettingsChanged += Refresh;
    }
    partial void OnEnabledChanged(bool value) => _ = Save.RunAsync(async token =>
    {
        var result = await _editor.SaveRecordingAsync(value, token).ConfigureAwait(false);
        return !result.Success && !value
            ? result with { Message = "本次已停止记录，重启后的偏好尚未保存；" + result.Message } : result;
    }, debounce: false);

    [RelayCommand] private void RequestClearHistory() => ConfirmHistory = true;
    [RelayCommand] private void CancelClearHistory() => ConfirmHistory = false;
    [RelayCommand] private void RequestClearFolders() => ConfirmFolders = true;
    [RelayCommand] private void CancelClearFolders() => ConfirmFolders = false;
    [RelayCommand] private Task ClearHistoryAsync() => Maintenance.RunAsync(async token =>
    {
        var result = await _maintenance.ClearHistoryAsync(token).ConfigureAwait(false);
        if (result.Success) _ui.Post(() => { if (!_disposed) ConfirmHistory = false; });
        return result.Success ? result with { Message = "已清空观看记录，视频文件未改变" } : result;
    }, false);
    [RelayCommand] private Task ClearFoldersAsync() => Maintenance.RunAsync(async token =>
    {
        var result = await _maintenance.RemoveRecentFolderAsync(null, token).ConfigureAwait(false);
        if (result.Success) _ui.Post(() => { if (!_disposed) ConfirmFolders = false; });
        return result.Success ? result with { Message = "已清空最近目录，当前库保持打开" } : result;
    }, false);
    [RelayCommand] private Task RemoveFolderAsync()
    {
        var path = SelectedFolder;
        return path is null ? Task.CompletedTask : Maintenance.RunAsync(token => _maintenance.RemoveRecentFolderAsync(path, token), false);
    }
    private void HistoryChanged(object? sender, PlaybackHistoryChangedEventArgs e) => Refresh(sender, e);
    private void Refresh(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(HistoryCount)); OnPropertyChanged(nameof(RecentFolders));
        if (!RecentFolders.Contains(SelectedFolder)) SelectedFolder = null;
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _history.HistoryChanged -= HistoryChanged;
        _reader.SettingsChanged -= Refresh;
        Save.Dispose(); Maintenance.Dispose();
    }
}
