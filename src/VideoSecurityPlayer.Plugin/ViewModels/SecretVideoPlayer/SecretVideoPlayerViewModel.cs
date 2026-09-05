using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 单文件播放器 Document 的兼容组合外壳。
/// </summary>
/// <remarks>
/// 新界面分别绑定 <see cref="Source"/> 和 <see cref="PublicInfo"/>；旧公开属性与命令继续
/// 转发到唯一状态所有者，保证宿主、既有测试和外部绑定在 G8 前无需破坏式迁移。
/// </remarks>
public sealed class SecretVideoPlayerViewModel : ObservableObject, IPluginDocument, IDisposable
{
    private bool _disposed;
    private string _title = "加密视频播放器";

    public VideoPlayerControlViewModel PlayerViewModel { get; }
    public SingleVideoSourceViewModel Source { get; }
    public PublicInfoEditorViewModel PublicInfo { get; }

    public SecretVideoPlayerViewModel(
        VideoPlayerControlViewModel playerViewModel,
        IDocumentLifetime documentLifetime)
    {
        PlayerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        ArgumentNullException.ThrowIfNull(documentLifetime);
        Source = new SingleVideoSourceViewModel(PlayerViewModel, OnFileChanged, documentLifetime);
        PublicInfo = new PublicInfoEditorViewModel(PlayerViewModel, Source);
        Source.PropertyChanged += OnSourcePropertyChanged;
        PublicInfo.PropertyChanged += OnPublicInfoPropertyChanged;
    }

    /// <inheritdoc />
    public DocumentPresentationState Presentation => new(_title);

    /// <inheritdoc />
    public event EventHandler? PresentationChanged;

    /// <inheritdoc />
    public ValueTask InitializeAsync(
        DocumentActivation activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation)
        {
            // 播放器不声明持久化 Codec；恢复文件只能由可持久化 Document 接收，不能在这里降级为新建。
            throw new NotSupportedException("加密视频播放器只支持新建激活。");
        }

        var title = string.IsNullOrWhiteSpace(activation.Title) ? "加密视频播放器" : activation.Title;
        if (!string.Equals(_title, title, StringComparison.Ordinal))
        {
            _title = title;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }

    public string FilePath { get => Source.FilePath; set => Source.FilePath = value; }
    public string Password { get => Source.Password; set => Source.Password = value; }
    public bool ShowPassword { get => Source.ShowPassword; set => Source.ShowPassword = value; }
    public string StatusMessage { get => Source.StatusMessage; set => Source.StatusMessage = value; }
    public bool IsLoading => Source.IsLoading;
    public string PublicTitle => PublicInfo.PublicTitle;
    public string PublicDescription => PublicInfo.PublicDescription;
    public bool HasPublicDescription => PublicInfo.HasPublicDescription;
    public bool IsEditingPublicInfo => PublicInfo.IsEditingPublicInfo;
    public string EditableTitle { get => PublicInfo.EditableTitle; set => PublicInfo.EditableTitle = value; }
    public string EditableDescription
    {
        get => PublicInfo.EditableDescription;
        set => PublicInfo.EditableDescription = value;
    }
    public int EditableTitleCharacterCount => PublicInfo.EditableTitleCharacterCount;
    public int EditableDescriptionCharacterCount => PublicInfo.EditableDescriptionCharacterCount;

    public IAsyncRelayCommand LoadVideoCommand => Source.LoadVideoCommand;
    public IRelayCommand TogglePasswordVisibilityCommand => Source.TogglePasswordVisibilityCommand;
    public IAsyncRelayCommand EditPublicInfoCommand => PublicInfo.EditPublicInfoCommand;
    public IRelayCommand SavePublicInfoCommand => PublicInfo.SavePublicInfoCommand;
    public IRelayCommand CancelEditPublicInfoCommand => PublicInfo.CancelEditPublicInfoCommand;

    public Task SelectFileAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        Source.SelectFileAsync(filePath, cancellationToken);

    private void OnFileChanged(string path)
    {
        PublicInfo?.Read(path);
        PublicInfo?.EditPublicInfoCommand.NotifyCanExecuteChanged();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null)
            OnPropertyChanged(e.PropertyName);
        if (e.PropertyName is nameof(Source.FilePath) or nameof(Source.IsLoading))
            PublicInfo.EditPublicInfoCommand.NotifyCanExecuteChanged();
    }

    private void OnPublicInfoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null)
            OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Source.PropertyChanged -= OnSourcePropertyChanged;
        PublicInfo.PropertyChanged -= OnPublicInfoPropertyChanged;
        Source.Dispose();
        GC.SuppressFinalize(this);
    }
}
