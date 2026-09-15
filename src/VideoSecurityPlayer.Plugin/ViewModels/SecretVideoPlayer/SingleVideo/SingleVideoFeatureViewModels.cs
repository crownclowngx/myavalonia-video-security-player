using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using MyAvaloniaManagement.PluginSdk;
using System.ComponentModel;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;

/// <summary>单文件选择、密码、加载取消和历史恢复的文档级协调器。</summary>
/// <remarks>
/// 文件扫描和历史通过窄接口注入；本组件只协调一次用户意图，不直接访问原生播放器。
/// 密码与取消源只属于当前文档。代次检查同时覆盖选文件和加载，防止迟到结果覆盖新选择。
/// </remarks>
public partial class SingleVideoSourceViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerControlViewModel _player;
    private readonly Action<string> _fileChanged;
    private readonly IDocumentLifetime _documentLifetime;
    private readonly IVideoLibraryScanner? _scanner;
    private readonly IPlaybackHistoryStore? _history;
    private readonly PlaybackHistoryCoordinator? _historyCoordinator;
    private VideoLibraryScanResult? _sourceInfo;
    private CancellationTokenSource? _loadCancellation;
    private long _generation;
    private bool _disposed;

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private string _statusMessage = "请选择加密视频文件";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isMediaLoaded;
    [ObservableProperty] private bool _isSourceExpanded = true;

    public bool IsPlaybackAvailable => _player.IsPlaybackAvailable;
    internal bool IsClosing => _disposed || _documentLifetime.IsClosing;
    internal CancellationToken ClosingToken => _documentLifetime.ClosingToken;
    public string PlayButtonText => PlaybackResumePolicy.PlayLabel(PlaybackResumePolicy.GetPosition(FindHistory()));

    public SingleVideoSourceViewModel(
        VideoPlayerControlViewModel player,
        Action<string> fileChanged,
        IDocumentLifetime documentLifetime,
        IVideoLibraryScanner? scanner = null,
        IPlaybackHistoryStore? history = null,
        PlaybackHistoryCoordinator? historyCoordinator = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _fileChanged = fileChanged ?? throw new ArgumentNullException(nameof(fileChanged));
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        _scanner = scanner;
        _history = history;
        _historyCoordinator = historyCoordinator;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        if (_history is not null) _history.HistoryChanged += OnHistoryChanged;
    }

    partial void OnPasswordChanged(string value) => NotifyCommands();
    partial void OnIsLoadingChanged(bool value) => NotifyCommands();

    partial void OnFilePathChanged(string value)
    {
        CancelCurrentRequest();
        _sourceInfo = null;
        IsMediaLoaded = false;
        IsSourceExpanded = true;
        _fileChanged(value);
        NotifyCommands();
    }

    /// <summary>保留仅加载契约，主观看入口使用 PlayVideoCommand。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenVideo))]
    private Task LoadVideoAsync() => OpenAsync(startPlayback: false, fromStart: false);

    [RelayCommand(CanExecute = nameof(CanOpenVideo))]
    private Task PlayVideoAsync() => OpenAsync(startPlayback: true, fromStart: false);

    [RelayCommand(CanExecute = nameof(CanOpenVideo))]
    private Task PlayFromStartAsync() => OpenAsync(startPlayback: true, fromStart: true);

    private bool CanOpenVideo() => !IsClosing && !IsLoading && File.Exists(FilePath);

    private async Task OpenAsync(bool startPlayback, bool fromStart)
    {
        if (!CanOpenVideo()) return;
        if (string.IsNullOrEmpty(Password))
        {
            IsSourceExpanded = true;
            StatusMessage = "请输入播放密码后继续";
            return;
        }
        CancelCurrentRequest();
        var generation = _generation;
        var path = FilePath;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ClosingToken);
        _loadCancellation = cancellation;
        IsLoading = true;
        StatusMessage = "正在验证并打开视频…";
        try
        {
            _historyCoordinator?.FlushCurrent();
            var info = _scanner is null ? null : await _scanner.ReadFileAsync(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _generation || IsClosing) return;
            _sourceInfo = info;
            var position = PlaybackResumePolicy.GetPosition(FindHistory(), fromStart);
            var success = startPlayback
                ? await _player.LoadMediaAtPositionAndPlayAsync(path, Password, position,
                    info?.FileId, info?.OriginalFileLength ?? 0, cancellation.Token)
                : await _player.LoadMediaAtPositionAsync(path, Password, position,
                    info?.FileId, info?.OriginalFileLength ?? 0, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested || IsClosing) return;
            IsMediaLoaded = success || _player.PlaybackSnapshot.HasMedia;
            if (success)
            {
                var identity = _player.PlaybackSnapshot.MediaIdentity;
                if (info is not null && identity is not null)
                    _historyCoordinator?.Track(info with { FileId = identity.FileId,
                        OriginalFileLength = identity.OriginalFileLength }, _player.PlaybackSnapshot.MediaGeneration);
                IsSourceExpanded = false;
                StatusMessage = startPlayback ? "正在播放" : "视频已加载，保持暂停";
            }
            else
            {
                IsSourceExpanded = true;
                StatusMessage = _player.StatusMessage;
            }
            OnPropertyChanged(nameof(PlayButtonText));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            if (!IsClosing && generation == _generation)
                StatusMessage = "打开视频失败，请检查文件是否可访问后重试";
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            if (!IsClosing && generation == _generation) IsLoading = false;
        }
    }

    private VideoPlaybackHistoryEntry? FindHistory() => _sourceInfo is { } info
        ? _history?.Find(info.FilePath, info.FileId, info.OriginalFileLength) : null;

    /// <summary>取消不卸载原有效媒体；旧操作的完成分支通过代次检查拒绝发布状态。</summary>
    [RelayCommand]
    private void CancelLoad()
    {
        CancelCurrentRequest();
        StatusMessage = "已取消打开视频";
    }

    private void CancelCurrentRequest()
    {
        _generation++;
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        IsLoading = false;
    }

    public async Task SelectFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelCurrentRequest();
        var generation = _generation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ClosingToken);
        _historyCoordinator?.FlushCurrent();
        await _player.Media.CleanupAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (generation != _generation || IsClosing) return;
        FilePath = filePath;
        generation = _generation;
        var info = _scanner is null ? null : await _scanner.ReadFileAsync(filePath, linked.Token);
        if (generation != _generation || IsClosing) return;
        _sourceInfo = info;
        OnPropertyChanged(nameof(PlayButtonText));
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerControlViewModel.IsPlaybackAvailable))
            OnPropertyChanged(nameof(IsPlaybackAvailable));
    }

    private void OnHistoryChanged(object? sender, PlaybackHistoryChangedEventArgs e) =>
        OnPropertyChanged(nameof(PlayButtonText));

    private void NotifyCommands()
    {
        LoadVideoCommand.NotifyCanExecuteChanged();
        PlayVideoCommand.NotifyCanExecuteChanged();
        PlayFromStartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlayButtonText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentRequest();
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        if (_history is not null) _history.HistoryChanged -= OnHistoryChanged;
        Password = string.Empty;
        IsMediaLoaded = false;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// SECVID03 公开标题和描述的展示、编辑与原地保存。
/// </summary>
/// <remarks>
/// 公开区不受密码学认证，读取失败不能阻止用户继续尝试验证视频主体。进入编辑前必须先
/// 释放播放器持有的文件句柄，这是本组件与播放组件之间唯一的资源协调点。
/// </remarks>
public partial class PublicInfoEditorViewModel : ObservableObject
{
    private readonly VideoPlayerControlViewModel _player;
    private readonly SingleVideoSourceViewModel _source;
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
        SingleVideoSourceViewModel source)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _source = source ?? throw new ArgumentNullException(nameof(source));
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
            var info = EncryptedVideoContainer.ReadPublicInfo(path);
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
    private async Task EditPublicInfoAsync()
    {
        if (_source.IsMediaLoaded)
        {
            await _player.Media.CleanupAsync();
            _source.IsMediaLoaded = false;
        }

        EditableTitle = _rawPublicTitle;
        EditableDescription = PublicDescription;
        IsEditingPublicInfo = true;
    }

    private bool CanEditPublicInfo() => !_source.IsLoading && File.Exists(_source.FilePath);

    [RelayCommand(CanExecute = nameof(CanSavePublicInfo))]
    private void SavePublicInfo()
    {
        try
        {
            EncryptedVideoContainer.UpdatePublicInfo(
                _source.FilePath,
                EditableTitle,
                EditableDescription);
            IsEditingPublicInfo = false;
            Read(_source.FilePath);
            _source.StatusMessage = "标题和描述已原地保存，视频密文未移动";
        }
        catch (Exception ex)
        {
            _source.StatusMessage = $"保存公开信息失败: {ex.Message}";
        }
    }

    private bool CanSavePublicInfo() =>
        IsEditingPublicInfo &&
        EditableTitleCharacterCount <= EncryptedVideoContainer.MaxTitleRunes &&
        EditableDescriptionCharacterCount <= EncryptedVideoContainer.MaxDescriptionRunes;

    [RelayCommand]
    private void CancelEditPublicInfo() => IsEditingPublicInfo = false;
}
