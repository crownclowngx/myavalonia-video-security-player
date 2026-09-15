using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;

/// <summary>
/// SECVID03 公开标题和描述的展示、编辑与原地保存。
/// </summary>
/// <remarks>
/// 公开区不受密码学认证，读取失败不能阻止用户继续尝试验证视频主体。编辑草稿期间保持播放；保存时才释放文件句柄，
/// 并使用原状态恢复同一媒体。文件读写通过窄端口注入，便于验证失败与关闭路径。
/// </remarks>
public partial class PublicInfoEditorViewModel : ObservableObject
{
    private readonly VideoPlayerControlViewModel _player;
    private readonly SingleVideoSourceViewModel _source;
    private readonly IPublicVideoInfoStore _store;
    [ObservableProperty] private bool _isSaving;
    private string _rawPublicTitle = string.Empty;

    [ObservableProperty] private string _publicTitle = string.Empty;
    [ObservableProperty] private string _publicDescription = string.Empty;
    [ObservableProperty] private bool _hasPublicDescription;
    [ObservableProperty] private bool _isEditingPublicInfo;
    [ObservableProperty] private string _editableTitle = string.Empty;
    [ObservableProperty] private string _editableDescription = string.Empty;

    public int EditableTitleCharacterCount => EncryptedVideoContainer.CountRunes(EditableTitle);
    public int EditableDescriptionCharacterCount => EncryptedVideoContainer.CountRunes(EditableDescription);

    public PublicInfoEditorViewModel(
        VideoPlayerControlViewModel player,
        SingleVideoSourceViewModel source, IPublicVideoInfoStore store)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        // 来源与编辑器属于同一个 Document；忙碌变化必须同步命令状态，避免加载期间再次发起保存。
        _source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SingleVideoSourceViewModel.IsBusy))
            { EditPublicInfoCommand.NotifyCanExecuteChanged(); SavePublicInfoCommand.NotifyCanExecuteChanged(); }
        };
    }

    partial void OnEditableTitleChanged(string value)
    {
        OnPropertyChanged(nameof(EditableTitleCharacterCount));
        SavePublicInfoCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditableDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(EditableDescriptionCharacterCount));
        SavePublicInfoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEditingPublicInfoChanged(bool value) =>
        SavePublicInfoCommand.NotifyCanExecuteChanged();

    public void Read(string path)
    {
        IsEditingPublicInfo = false;
        PublicTitle = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        _rawPublicTitle = string.Empty;
        PublicDescription = string.Empty;
        HasPublicDescription = false;
        if (!File.Exists(path))
            return;

        try
        {
            var info = _store.Read(path);
            _rawPublicTitle = info.Title;
            PublicTitle = string.IsNullOrEmpty(info.Title) ? info.OriginalFileName : info.Title;
            PublicDescription = info.Description;
            HasPublicDescription = !string.IsNullOrEmpty(info.Description);
            _source.StatusMessage = "公开信息已读取，请输入密码播放";
        }
        catch (Exception ex)
        {
            PublicTitle = Path.GetFileName(path);
            PublicDescription = "描述不可读取";
            HasPublicDescription = true;
            _source.StatusMessage =
                $"公开信息不可读取，文件可能不受支持或已经损坏；仍可尝试输入密码播放: {ex.Message}";
        }

        EditPublicInfoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditPublicInfo))]
    private Task EditPublicInfoAsync()
    {
        EditableTitle = _rawPublicTitle;
        EditableDescription = PublicDescription;
        IsEditingPublicInfo = true;
        return Task.CompletedTask;
    }

    private bool CanEditPublicInfo() => !_source.IsClosing && !_source.IsBusy && File.Exists(_source.FilePath);

    partial void OnIsSavingChanged(bool value)
    {
        SavePublicInfoCommand.NotifyCanExecuteChanged();
        CancelEditPublicInfoCommand.NotifyCanExecuteChanged();
        EditPublicInfoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 保存开始时冻结文件和草稿，完成后只恢复仍属于本次目标的媒体。取消编辑从未释放媒体，
    /// 保存失败则保留草稿；关闭文档或目标已经改变时，禁止旧保存操作重新启动视频。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSavePublicInfo))]
    private async Task SavePublicInfoAsync()
    {
        if (!CanSavePublicInfo()) return;
        var path = _source.FilePath;
        var title = EditableTitle;
        var description = EditableDescription;
        var snapshot = _player.PlaybackSnapshot;
        var saved = false;
        IsSaving = true;
        _source.IsSavingPublicInfo = true;
        _source.FlushHistory();
        try
        {
            if (snapshot.HasMedia) await _player.Media.CleanupAsync(_source.ClosingToken);
            if (_source.IsClosing) return;
            await _store.UpdateAsync(path, title, description, _source.ClosingToken);
            saved = true;
            if (StillOwnsTarget(path))
            {
                Read(path);
                _source.StatusMessage = "标题和描述已保存";
            }
        }
        catch (OperationCanceledException) when (_source.IsClosing) { }
        catch
        {
            if (StillOwnsTarget(path))
                _source.StatusMessage = "保存失败，草稿已保留，请检查文件占用和写入权限后重试";
        }
        finally
        {
            if (StillOwnsTarget(path) && snapshot.HasMedia)
            {
                try
                {
                    var restored = await RestoreMediaAsync(path, snapshot);
                    _source.TrackRestoredMedia();
                    if (StillOwnsTarget(path) && !restored)
                        _source.StatusMessage = saved
                            ? "信息已保存，但恢复播放失败，请重新打开视频"
                            : "保存和恢复播放均失败，草稿已保留，请检查文件后重试";
                }
                catch
                {
                    if (StillOwnsTarget(path)) _source.StatusMessage = "恢复播放失败，请重新打开视频；未保存的草稿仍保留";
                }
            }
            if (!_source.IsClosing)
            {
                _source.IsSavingPublicInfo = false;
                IsSaving = false;
            }
        }
    }

    private bool StillOwnsTarget(string path) => !_source.IsClosing &&
        string.Equals(path, _source.FilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>恢复播放位置、是否正在观看和仍然存在的轨道；不依赖原生句柄或播放器库类型。</summary>
    private async Task<bool> RestoreMediaAsync(string path, PlaybackSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(_source.Password)) return false;
        var identity = snapshot.MediaIdentity;
        var restored = snapshot.State == PlaybackState.Playing
            ? await _player.LoadMediaAtPositionAndPlayAsync(path, _source.Password, snapshot.PositionMs,
                identity?.FileId, identity?.OriginalFileLength ?? 0, _source.ClosingToken)
            : await _player.LoadMediaAtPositionAsync(path, _source.Password, snapshot.PositionMs,
                identity?.FileId, identity?.OriginalFileLength ?? 0, _source.ClosingToken);
        if (!restored || !StillOwnsTarget(path)) return false;
        if (snapshot.PositionMs > 0 && Math.Abs(_player.PlaybackSnapshot.PositionMs - snapshot.PositionMs) > 1500) return false;
        var controlsRestored = (await _player.SetPlaybackRateAsync(snapshot.Controls.Rate, _source.ClosingToken)).Success;
        if (!StillOwnsTarget(path)) return false;
        var controls = _player.PlaybackSnapshot.Controls;
        if (snapshot.Controls.SelectedAudioTrackId is { } audio && controls.AudioTracks.Any(t => t.Id == audio))
            controlsRestored &= (await _player.SelectAudioTrackAsync(audio, _source.ClosingToken)).Success;
        if (!StillOwnsTarget(path)) return false;
        if (snapshot.Controls.SelectedSubtitleTrackId is { } subtitle &&
            (subtitle == -1 || controls.SubtitleTracks.Any(t => t.Id == subtitle)))
            controlsRestored &= (await _player.SelectSubtitleTrackAsync(subtitle, _source.ClosingToken)).Success;
        return controlsRestored;
    }

    private bool CanSavePublicInfo() =>
        !_source.IsClosing && !_source.IsLoading && !IsSaving && IsEditingPublicInfo &&
        EditableTitleCharacterCount <= EncryptedVideoContainer.MaxTitleRunes &&
        EditableDescriptionCharacterCount <= EncryptedVideoContainer.MaxDescriptionRunes;

    private bool CanCancelEdit() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
    private void CancelEditPublicInfo() => IsEditingPublicInfo = false;
}
