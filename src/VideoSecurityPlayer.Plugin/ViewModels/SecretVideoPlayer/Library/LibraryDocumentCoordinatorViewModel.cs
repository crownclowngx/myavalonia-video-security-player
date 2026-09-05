using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

/// <summary>
/// 文件夹视频库 Document：只协调浏览状态、公共密码和现有播放器控件。
/// </summary>
public partial class LibraryDocumentCoordinatorViewModel :
    ObservableObject,
    IPlaybackNavigationContext,
    IDisposable
{
    private bool _disposed;

    private long _playGeneration;
    private CancellationTokenSource? _playCancellation;
    private CancellationTokenSource? _autoAdvanceCancellation;
    private long _lastHandledEndedGeneration;
    private readonly IPlaybackHistoryStore? _historyStore;
    private readonly IVideoLibrarySettingsStore? _settingsStore;
    private readonly PlaybackHistoryCoordinator? _historyCoordinator;
    private readonly ISecretVideoUserDataDiagnostics? _userDataDiagnostics;
    private readonly IDocumentLifetime _documentLifetime;
    private int _initializeState;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private bool _isOpening;
    [ObservableProperty] private bool _isLibraryPaneOpen = true;
    [ObservableProperty] private bool _isLibrarySettingsExpanded;
    [ObservableProperty] private string _statusMessage = "请选择文件夹并输入公共密码";
    [ObservableProperty] private string _currentPlayingPath = string.Empty;
    [ObservableProperty] private bool _isContinuousPlaybackEnabled;
    [ObservableProperty] private bool _showClearHistoryConfirmation;

    public VideoLibraryBrowserViewModel Browser { get; }
    public VideoPlayerControlViewModel PlayerViewModel { get; }
    public LibraryPlaybackViewModel Playback { get; }
    public LibraryHistoryViewModel History { get; }
    public LibraryLayoutViewModel Layout { get; }

    /// <summary>
    /// 仅供界面摘要使用的密码状态，不包含密码正文。
    /// </summary>
    /// <remarks>
    /// 折叠面板需要让用户知道是否已经输入密码，但任何绑定、设置或历史模型都不应复制
    /// 密码内容。这里刻意只返回固定文案，避免现代摘要界面扩大敏感数据暴露面。
    /// </remarks>
    public string PasswordStateText =>
        string.IsNullOrEmpty(Password) ? "密码未输入" : "密码已输入";

    public bool CanNavigatePrevious =>
        !_disposed &&
        Browser.FindVisibleAdjacent(CurrentPlayingPath, -1) is { FilePath: var path } &&
        File.Exists(path);
    public bool CanNavigateNext =>
        !_disposed &&
        Browser.FindVisibleAdjacent(CurrentPlayingPath, 1) is { FilePath: var path } &&
        File.Exists(path);

    // CommunityToolkit 生成的异步命令以更具体的类型公开；显式实现让 UI 端口只依赖
    // BCL 的 ICommand，不把工具包类型扩散到复用播放器控件。
    ICommand IPlaybackNavigationContext.PreviousCommand => PreviousCommand;
    ICommand IPlaybackNavigationContext.NextCommand => NextCommand;

    public LibraryDocumentCoordinatorViewModel(
        VideoLibraryBrowserViewModel browser,
        VideoPlayerControlViewModel playerViewModel,
        IDocumentLifetime documentLifetime,
        IPlaybackHistoryStore? historyStore = null,
        IVideoLibrarySettingsStore? settingsStore = null,
        PlaybackHistoryCoordinator? historyCoordinator = null,
        ISecretVideoUserDataDiagnostics? userDataDiagnostics = null)
    {
        Browser = browser ?? throw new ArgumentNullException(nameof(browser));
        PlayerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _historyCoordinator = historyCoordinator;
        _userDataDiagnostics = userDataDiagnostics;
        IsLibraryPaneOpen = settingsStore?.CurrentSettings.IsLibraryPaneOpen ?? true;
        IsLibrarySettingsExpanded =
            settingsStore?.CurrentSettings.IsLibrarySettingsExpanded ?? false;
        Browser.PropertyChanged += OnBrowserPropertyChanged;
        ((INotifyCollectionChanged)Browser.VisibleItems).CollectionChanged += OnVisibleItemsChanged;
        PlayerViewModel.MediaEnded += OnMediaEnded;
        Playback = new LibraryPlaybackViewModel(this);
        History = new LibraryHistoryViewModel(this);
        Layout = new LibraryLayoutViewModel(this);
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(PasswordStateText));
        PlaySelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOpeningChanged(bool value)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPlayingPathChanged(string value) => NotifyNavigationState();

    partial void OnIsContinuousPlaybackEnabledChanged(bool value)
    {
        if (!value)
        {
            // 只取消自动推进，不打断用户手动发起的媒体切换。
            TryCancel(Interlocked.Exchange(ref _autoAdvanceCancellation, null));
        }
    }

    partial void OnIsLibraryPaneOpenChanged(bool value)
    {
        if (_settingsStore is null)
            return;
        _settingsStore.UpdateSettings(
            _settingsStore.CurrentSettings with { IsLibraryPaneOpen = value });
    }

    partial void OnIsLibrarySettingsExpandedChanged(bool value)
    {
        if (_settingsStore is null)
            return;

        // 展开状态是低敏感度的界面偏好，和侧栏开关一样共享给未来新建的 Document。
        // 不根据选目录或播放成功自动改写，避免用户操作后面板发生不可预测的跳动。
        _settingsStore.UpdateSettings(
            _settingsStore.CurrentSettings with { IsLibrarySettingsExpanded = value });
    }

    /// <summary>由 View 首次附加时调用一次，恢复最近目录但不加载或播放媒体。</summary>
    public async Task InitializeAsync()
    {
        if (IsClosing)
            return;
        if (Interlocked.Exchange(ref _initializeState, 1) != 0)
            return;
        if (!string.IsNullOrWhiteSpace(_userDataDiagnostics?.LoadWarning))
            StatusMessage = _userDataDiagnostics.LoadWarning;
        await Browser.InitializeRecentFolderAsync();
    }

    /// <summary>
    /// 切换到一个新文件夹。刷新当前文件夹应直接调用 Browser.RefreshCommand，避免停止当前视频。
    /// </summary>
    public async Task OpenFolderAsync(string folderPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsClosing || IsOpening || string.IsNullOrWhiteSpace(folderPath))
            return;

        var fullPath = Path.GetFullPath(folderPath);
        if (!string.Equals(Browser.FolderPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            await PlayerViewModel.CleanupMediaAsync();
            CurrentPlayingPath = string.Empty;
            StatusMessage = "已切换视频文件夹";
        }

        await Browser.LoadFolderAsync(fullPath);
    }

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    private async Task PlaySelectedAsync()
    {
        var item = Browser.SelectedItem;
        if (item is null)
        {
            StatusMessage = "请选择要播放的视频";
            return;
        }
        await PlayItemAsync(item, PlaybackRequestOrigin.UserLoad);
    }

    /// <summary>
    /// 激活列表中的所选媒体：恢复可信的未完成历史位置并立即开始播放。
    /// </summary>
    /// <remarks>
    /// 此命令专供双击和 Enter 使用，与“加载所选视频”刻意分离。前者代表明确播放意图，
    /// 后者仍遵守 G7 的“定位后暂停”语义，避免普通按钮操作意外自动播放。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanActivateSelected))]
    private async Task ActivateSelectedAsync()
    {
        var item = Browser.SelectedItem;
        if (item is null)
        {
            StatusMessage = "请选择要播放的视频";
            return;
        }
        await PlayItemAsync(item, PlaybackRequestOrigin.UserActivation);
    }

    private bool CanPlaySelected() => !_disposed &&
        Browser.SelectedItem is { FilePath: var path } &&
        File.Exists(path) &&
        !string.IsNullOrEmpty(Password);

    private bool CanActivateSelected() => !_disposed &&
        Browser.SelectedItem is { FilePath: var path } &&
        File.Exists(path);

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private async Task PreviousAsync()
    {
        var item = Browser.FindVisibleAdjacent(CurrentPlayingPath, -1);
        if (item is not null)
        {
            await PlayItemAsync(item, PlaybackRequestOrigin.Previous);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NextAsync()
    {
        var item = Browser.FindVisibleAdjacent(CurrentPlayingPath, 1);
        if (item is not null)
        {
            await PlayItemAsync(item, PlaybackRequestOrigin.Next);
        }
    }

    [RelayCommand]
    private void ToggleLibraryPane() => IsLibraryPaneOpen = !IsLibraryPaneOpen;

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    [RelayCommand(CanExecute = nameof(CanClearSelectedHistory))]
    private void ClearSelectedHistory()
    {
        var item = Browser.SelectedItem;
        if (item is null || _historyStore is null)
            return;
        if (string.Equals(
                item.FilePath,
                CurrentPlayingPath,
                StringComparison.OrdinalIgnoreCase))
        {
            _historyCoordinator?.SuppressCurrentGeneration();
        }
        _historyStore.Remove(item.FilePath, item.FileId, item.OriginalFileLength);
        StatusMessage = "已清除所选视频的播放历史";
    }

    private bool CanClearSelectedHistory() =>
        !_disposed &&
        _historyStore is not null &&
        Browser.SelectedItem is { HistoryState: not VideoPlaybackHistoryState.Unplayed };

    [RelayCommand]
    private void RequestClearAllHistory() => ShowClearHistoryConfirmation = true;

    [RelayCommand]
    private void CancelClearAllHistory() => ShowClearHistoryConfirmation = false;

    [RelayCommand]
    private void ConfirmClearAllHistory()
    {
        _historyCoordinator?.SuppressCurrentGeneration();
        _historyStore?.Clear();
        ShowClearHistoryConfirmation = false;
        StatusMessage = "已清空全部播放历史";
    }

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoLibraryBrowserViewModel.SelectedItem))
        {
            PlaySelectedCommand.NotifyCanExecuteChanged();
            ActivateSelectedCommand.NotifyCanExecuteChanged();
            ClearSelectedHistoryCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(VideoLibraryBrowserViewModel.SearchText) or
            nameof(VideoLibraryBrowserViewModel.VisibleItemCount))
        {
            // 自动推进必须服从用户此刻看到的筛选列表。筛选变化使未提交的自动目标失效，
            // 但不会停止已经在播放的媒体。
            TryCancel(Interlocked.Exchange(ref _autoAdvanceCancellation, null));
            NotifyNavigationState();
        }
    }

    private void OnVisibleItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyNavigationState();

    private async void OnMediaEnded(object? sender, PlaybackMediaEndedEventArgs e)
    {
        if (IsClosing ||
            !IsContinuousPlaybackEnabled ||
            e.MediaGeneration == Interlocked.Read(ref _lastHandledEndedGeneration) ||
            e.MediaGeneration != PlayerViewModel.PlaybackSnapshot.MediaGeneration)
        {
            return;
        }

        Interlocked.Exchange(ref _lastHandledEndedGeneration, e.MediaGeneration);
        var next = Browser.FindVisibleAdjacent(CurrentPlayingPath, 1);
        if (next is null)
        {
            StatusMessage = "播放完成，已到当前列表末尾";
            return;
        }

        try
        {
            await PlayItemAsync(next, PlaybackRequestOrigin.AutoAdvance);
        }
        catch (OperationCanceledException)
        {
            // 用户关闭连续播放、修改筛选或发起新播放时，自动推进被正常取消。
        }
    }

    private async Task PlayItemAsync(
        Models.SecretVideoPlayer.VideoLibraryItemViewModel item,
        PlaybackRequestOrigin origin)
    {
        if (IsClosing)
            return;
        if (string.IsNullOrEmpty(Password))
        {
            StatusMessage = "请输入公共密码";
            return;
        }
        if (!File.Exists(item.FilePath))
        {
            StatusMessage = "视频文件不存在或已被删除";
            PlaySelectedCommand.NotifyCanExecuteChanged();
            ActivateSelectedCommand.NotifyCanExecuteChanged();
            NotifyNavigationState();
            return;
        }

        Browser.SelectedItem = item;
        _historyCoordinator?.FlushCurrent();
        var generation = Interlocked.Increment(ref _playGeneration);
        // 每次播放仍有自己的“新请求替换旧请求”令牌，但最外层必须链接 Host 关闭令牌。
        // 这样关闭标签页不依赖 View 是否及时调用 Dispose，也不会让迟到的原生结果写回模型。
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _documentLifetime.ClosingToken);
        var previous = Interlocked.Exchange(ref _playCancellation, cancellation);
        TryCancel(previous);
        if (origin == PlaybackRequestOrigin.AutoAdvance)
        {
            Interlocked.Exchange(ref _autoAdvanceCancellation, cancellation);
        }
        else
        {
            TryCancel(Interlocked.Exchange(ref _autoAdvanceCancellation, null));
        }

        IsOpening = true;
        StatusMessage = $"正在验证密码并打开 {item.DisplayName}...";
        try
        {
            var history = _historyStore?.Find(
                item.FilePath,
                item.FileId,
                item.OriginalFileLength);
            var restorePosition = history is { IsCompleted: false }
                ? history.PositionMs
                : 0;
            var success = origin switch
            {
                PlaybackRequestOrigin.UserLoad =>
                    await PlayerViewModel.LoadMediaAtPositionAsync(
                        item.FilePath,
                        Password,
                        restorePosition,
                        item.FileId,
                        item.OriginalFileLength,
                        cancellation.Token),
                PlaybackRequestOrigin.UserActivation =>
                    await PlayerViewModel.LoadMediaAtPositionAndPlayAsync(
                        item.FilePath,
                        Password,
                        restorePosition,
                        item.FileId,
                        item.OriginalFileLength,
                        cancellation.Token),
                _ => await PlayerViewModel.LoadAndPlayMediaAsync(
                    item.FilePath,
                    Password,
                    cancellation.Token)
            };
            if (IsClosing || generation != Volatile.Read(ref _playGeneration))
            {
                return;
            }

            if (success)
            {
                // 路径是当前播放身份；密码和 Item 引用都不进入导航状态。
                CurrentPlayingPath = Path.GetFullPath(item.FilePath);
                var authenticatedIdentity =
                    PlayerViewModel.PlaybackSnapshot.MediaIdentity;
                var trackedSource = authenticatedIdentity is null
                    ? item.Source
                    : item.Source with
                    {
                        FileId = authenticatedIdentity.FileId,
                        OriginalFileLength = authenticatedIdentity.OriginalFileLength
                    };
                _historyCoordinator?.Track(
                    trackedSource,
                    PlayerViewModel.PlaybackSnapshot.MediaGeneration);
                var restored = restorePosition > 0 &&
                               authenticatedIdentity is not null &&
                               string.Equals(
                                   authenticatedIdentity.FileId,
                                   item.FileId,
                                   StringComparison.OrdinalIgnoreCase) &&
                               authenticatedIdentity.OriginalFileLength ==
                               item.OriginalFileLength;
                StatusMessage = origin == PlaybackRequestOrigin.UserLoad
                    ? restored
                        ? $"已恢复到上次位置，按播放键继续 {item.DisplayName}"
                        : $"已加载 {item.DisplayName}，按播放键开始"
                    : origin == PlaybackRequestOrigin.UserActivation && restored
                        ? $"已从上次位置继续播放 {item.DisplayName}"
                        : $"正在播放 {item.DisplayName}";
            }
            else
            {
                StatusMessage = PlayerViewModel.StatusMessage;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 更新的手动请求或用户操作已经接管状态。
        }
        catch
        {
            if (!IsClosing && generation == Volatile.Read(ref _playGeneration))
            {
                // 未知异常可能携带绝对路径或 LibVLC 原生文本。媒体库只显示稳定的
                // 可行动提示，详细原生异常不进入 UI、导航状态或日志。
                StatusMessage = "播放失败，请检查文件、密码和播放器状态";
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _playCancellation, null, cancellation);
            Interlocked.CompareExchange(ref _autoAdvanceCancellation, null, cancellation);
            cancellation.Dispose();
            if (!IsClosing && generation == Volatile.Read(ref _playGeneration))
            {
                IsOpening = false;
            }
        }
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanNavigatePrevious));
        OnPropertyChanged(nameof(CanNavigateNext));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private bool IsClosing => _disposed || _documentLifetime.IsClosing;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Increment(ref _playGeneration);
        TryCancel(Interlocked.Exchange(ref _playCancellation, null));
        TryCancel(Interlocked.Exchange(ref _autoAdvanceCancellation, null));
        Browser.PropertyChanged -= OnBrowserPropertyChanged;
        ((INotifyCollectionChanged)Browser.VisibleItems).CollectionChanged -= OnVisibleItemsChanged;
        PlayerViewModel.MediaEnded -= OnMediaEnded;
        Password = string.Empty;
        GC.SuppressFinalize(this);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求可能恰好已在完成路径释放。
        }
    }

    private enum PlaybackRequestOrigin
    {
        UserLoad,
        UserActivation,
        Previous,
        Next,
        AutoAdvance
    }
}
