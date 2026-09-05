using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using MyAvaloniaManagement.PluginSdk;
using System.ComponentModel;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;

/// <summary>
/// 单文件选择、密码输入和安全媒体加载状态。
/// </summary>
/// <remarks>
/// 密码只保存在当前 Document 拥有的本实例中；释放时显式清空。选择新文件前先关闭
/// 播放媒体，保证旧文件句柄和旧异步结果都不能影响新选择。
/// </remarks>
public partial class SingleVideoSourceViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerControlViewModel _player;
    private readonly Action<string> _fileChanged;
    private readonly IDocumentLifetime _documentLifetime;
    private bool _disposed;

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private string _statusMessage = "请选择 SECVID03 加密视频文件";
    [ObservableProperty] private bool _isLoading;

    internal bool IsMediaLoaded { get; set; }
    public bool IsPlaybackAvailable => _player.IsPlaybackAvailable;

    public SingleVideoSourceViewModel(
        VideoPlayerControlViewModel player,
        Action<string> fileChanged,
        IDocumentLifetime documentLifetime)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _fileChanged = fileChanged ?? throw new ArgumentNullException(nameof(fileChanged));
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    partial void OnPasswordChanged(string value) => LoadVideoCommand.NotifyCanExecuteChanged();

    partial void OnFilePathChanged(string value)
    {
        IsMediaLoaded = false;
        _fileChanged(value);
        LoadVideoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value) => LoadVideoCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanLoadVideo))]
    private async Task LoadVideoAsync()
    {
        if (!File.Exists(FilePath) || string.IsNullOrEmpty(Password))
        {
            StatusMessage = "请选择文件并输入密码";
            return;
        }

        IsLoading = true;
        StatusMessage = "正在验证密码并打开随机读取流...";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _documentLifetime.ClosingToken);
            var success = await _player.Media.LoadAsync(FilePath, Password, linked.Token);
            if (_documentLifetime.IsClosing)
                return;
            IsMediaLoaded = success;
            StatusMessage = success
                ? "视频已加载，可以开始播放"
                : "加载失败：密码错误、文件已损坏或不是 SECVID03";
        }
        catch (OperationCanceledException) when (_documentLifetime.IsClosing)
        {
            // Host 已确认关闭；取消属于正常控制流，且关闭后的模型不能再发布错误文本。
        }
        catch (Exception ex)
        {
            IsMediaLoaded = false;
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            if (!_documentLifetime.IsClosing)
                IsLoading = false;
        }
    }

    private bool CanLoadVideo() => !_disposed && !IsLoading && File.Exists(FilePath);

    public async Task SelectFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _documentLifetime.ClosingToken);
        await _player.Media.CleanupAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        FilePath = filePath;
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerControlViewModel.IsPlaybackAvailable))
            OnPropertyChanged(nameof(IsPlaybackAvailable));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
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
