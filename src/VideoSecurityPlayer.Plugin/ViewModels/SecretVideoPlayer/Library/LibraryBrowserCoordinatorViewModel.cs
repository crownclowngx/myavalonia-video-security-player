using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Models.SecretVideoPlayer;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

/// <summary>
/// 把媒体目录快照投影为可搜索、筛选和排序的 Avalonia 列表。
/// </summary>
/// <remarks>
/// 文件系统监听和事件合并属于 <see cref="IVideoLibraryCatalogSession"/>；本类型只维护
/// UI 所需的字典和可见投影，因此测试排序时不需要真实 FileSystemWatcher。
/// </remarks>
public partial class LibraryBrowserCoordinatorViewModel : ObservableObject, IDisposable
{
    private readonly IVideoLibraryCatalogSession _catalog;
    private readonly IVideoLibrarySettingsStore _settingsStore;
    private readonly IPlaybackHistoryStore _historyStore;
    private readonly CapturedUiScheduler _uiScheduler = new();
    private readonly Dictionary<string, VideoLibraryItemViewModel> _items =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly RangeObservableCollection<VideoLibraryItemViewModel> _visibleItems = [];
    private readonly object _projectionSync = new();
    private readonly IDocumentLifetime _documentLifetime;
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _filterCancellation;
    private long _catalogGeneration;
    private bool _applyingPersistedSettings;
    private bool _disposed;

    [ObservableProperty] private string _folderPath = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private VideoLibraryItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _processedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _visibleItemCount;
    [ObservableProperty] private string _statusMessage = "请选择包含 .secvid 文件的文件夹";
    [ObservableProperty] private bool _includeSubdirectories;
    [ObservableProperty] private VideoLibrarySortField _sortField;
    [ObservableProperty] private VideoLibrarySortDirection _sortDirection;
    [ObservableProperty] private VideoLibraryStatusFilter _statusFilter;

    public ReadOnlyObservableCollection<VideoLibraryItemViewModel> VisibleItems { get; }
    public IReadOnlyList<VideoLibrarySortField> AvailableSortFields { get; } =
        Enum.GetValues<VideoLibrarySortField>();
    public IReadOnlyList<VideoLibrarySortDirection> AvailableSortDirections { get; } =
        Enum.GetValues<VideoLibrarySortDirection>();
    public IReadOnlyList<VideoLibraryStatusFilter> AvailableStatusFilters { get; } =
        Enum.GetValues<VideoLibraryStatusFilter>();
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);
    public bool HasVisibleItems => VisibleItemCount > 0;

    /// <summary>指示搜索框是否包含可清除的文本。</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public LibraryCatalogViewModel Catalog { get; }
    public LibraryQueryViewModel Query { get; }

    public LibraryBrowserCoordinatorViewModel(
        IVideoLibraryScanner scanner,
        IDocumentLifetime documentLifetime,
        IVideoLibrarySettingsStore? settingsStore = null,
        IPlaybackHistoryStore? historyStore = null,
        IVideoLibraryCatalogSession? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        _catalog = catalog ?? new VideoLibraryCatalogSession(scanner);
        var fallback = new VolatileUserDataStore();
        _settingsStore = settingsStore ?? fallback;
        _historyStore = historyStore ?? fallback;
        VisibleItems = new ReadOnlyObservableCollection<VideoLibraryItemViewModel>(_visibleItems);

        _applyingPersistedSettings = true;
        var settings = _settingsStore.CurrentSettings;
        IncludeSubdirectories = settings.IncludeSubdirectories;
        SortField = settings.SortField;
        SortDirection = settings.SortDirection;
        StatusFilter = settings.StatusFilter;
        _applyingPersistedSettings = false;
        _historyStore.HistoryChanged += OnHistoryChanged;
        Catalog = new LibraryCatalogViewModel(this);
        Query = new LibraryQueryViewModel(this);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        ClearSearchCommand.NotifyCanExecuteChanged();
        ScheduleProjection();
    }

    partial void OnFolderPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasFolder));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnVisibleItemCountChanged(int value) =>
        OnPropertyChanged(nameof(HasVisibleItems));

    partial void OnIncludeSubdirectoriesChanged(bool value)
    {
        PersistSettings();
        if (!_applyingPersistedSettings && HasFolder)
            _ = StartCatalogAsync(clearItems: true);
    }

    partial void OnSortFieldChanged(VideoLibrarySortField value)
    {
        PersistSettings();
        ScheduleProjection();
    }

    partial void OnSortDirectionChanged(VideoLibrarySortDirection value)
    {
        PersistSettings();
        ScheduleProjection();
    }

    partial void OnStatusFilterChanged(VideoLibraryStatusFilter value)
    {
        PersistSettings();
        ScheduleProjection();
    }

    /// <summary>从持久化设置自动恢复最近目录，但不选择或加载任何媒体。</summary>
    public Task InitializeRecentFolderAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var recentFolder = _settingsStore.CurrentSettings.RecentFolder;
        if (string.IsNullOrWhiteSpace(recentFolder))
            return Task.CompletedTask;
        if (!Directory.Exists(recentFolder))
        {
            FolderPath = recentFolder;
            StatusMessage = "最近使用的文件夹已不存在，请重新选择";
            return Task.CompletedTask;
        }
        return LoadFolderAsync(recentFolder);
    }

    public Task LoadFolderAsync(string folderPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("视频文件夹不能为空。", nameof(folderPath));

        FolderPath = Path.GetFullPath(folderPath);
        PersistSettings();
        return StartCatalogAsync(clearItems: true);
    }

    public VideoLibraryItemViewModel? FindVisibleAdjacent(
        string? currentPlayingPath,
        int offset)
    {
        if (string.IsNullOrWhiteSpace(currentPlayingPath) || offset is not (-1 or 1))
            return null;
        lock (_projectionSync)
        {
            var index = _visibleItems
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => string.Equals(
                    pair.item.FilePath,
                    currentPlayingPath,
                    StringComparison.OrdinalIgnoreCase))
                .index;
            if (index == 0 &&
                !string.Equals(
                    _visibleItems.ElementAtOrDefault(0)?.FilePath,
                    currentPlayingPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var target = index + offset;
            return target >= 0 && target < _visibleItems.Count
                ? _visibleItems[target]
                : null;
        }
    }

    /// <summary>
    /// 原子捕获当前可见投影，供性能门禁和无 UI 调度器的宿主只读使用。
    /// </summary>
    /// <remarks>
    /// 产品 UI 仍绑定同一个只读 ObservableCollection；快照仅隔离 watcher 批次与
    /// 搜索防抖恰好同时完成时的枚举竞态，不改变通知或排序语义。
    /// </remarks>
    internal IReadOnlyList<VideoLibraryItemViewModel> CaptureVisibleItems()
    {
        lock (_projectionSync)
            return _visibleItems.ToArray();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => StartCatalogAsync(clearItems: true);

    private bool CanRefresh() => !_disposed && HasFolder;

    /// <summary>
    /// 清除当前内存投影的搜索条件，不重新扫描磁盘或重建目录监听会话。
    /// </summary>
    /// <remarks>
    /// 搜索属于纯展示状态。复用 <see cref="SearchText"/> 的防抖投影路径，可以保持选择恢复、
    /// 排序和范围集合通知语义一致，同时避免现代搜索框的清除按钮意外触发昂贵 I/O。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSearchText))]
    private void ClearSearch() => SearchText = string.Empty;

    private async Task StartCatalogAsync(bool clearItems)
    {
        var generation = Interlocked.Increment(ref _catalogGeneration);
        ReplaceCancellation(ref _catalogCancellation, out var cancellation);
        if (clearItems)
        {
            lock (_projectionSync)
            {
                _items.Clear();
                _visibleItems.ReplaceAll(Array.Empty<VideoLibraryItemViewModel>());
                SelectedItem = null;
                ProcessedCount = 0;
                FailedCount = 0;
                VisibleItemCount = 0;
            }
        }
        IsScanning = true;
        StatusMessage = "正在扫描，已读取 0 个";

        try
        {
            var options = new VideoLibraryScanOptions(IncludeSubdirectories);
            await foreach (var batch in _catalog
                               .ObserveAsync(FolderPath, options, cancellation.Token)
                               .WithCancellation(cancellation.Token))
            {
                if (IsClosing || generation != Volatile.Read(ref _catalogGeneration))
                    return;
                ApplyBatch(batch);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 切换目录、选项或关闭 Document 是正常取消，旧代次不能再更新投影。
        }
        finally
        {
            if (!IsClosing && generation == Volatile.Read(ref _catalogGeneration))
                IsScanning = false;
            if (ReferenceEquals(_catalogCancellation, cancellation))
                _catalogCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyBatch(VideoLibraryCatalogBatch batch)
    {
        lock (_projectionSync)
        {
            var selectedPath = SelectedItem?.FilePath;
            if (batch.ReplaceAll)
                _items.Clear();
            foreach (var path in batch.RemovedPaths)
                _items.Remove(Path.GetFullPath(path));
            foreach (var result in batch.Upserts)
            {
                var history = _historyStore.Find(
                    result.FilePath,
                    result.FileId,
                    result.OriginalFileLength);
                _items[Path.GetFullPath(result.FilePath)] =
                    new VideoLibraryItemViewModel(result, history);
            }
            IsScanning = batch.IsScanning;
            StatusMessage = batch.StatusMessage;
            ProcessedCount = _items.Count;
            FailedCount = _items.Values.Count(item => item.HasError);
            ApplyProjection(selectedPath);
        }
    }

    private void ScheduleProjection()
    {
        if (IsClosing)
            return;
        ReplaceCancellation(ref _filterCancellation, out var cancellation);
        _ = ApplyProjectionAfterDelayAsync(cancellation);
    }

    private async Task ApplyProjectionAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellation.Token);
            if (!IsClosing)
                ApplyProjection(SelectedItem?.FilePath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
                _filterCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyProjection(string? selectedPath)
    {
        lock (_projectionSync)
        {
            var query = SearchText.Trim();
            var items = _items.Values
                .Where(item => MatchesSearch(item, query) && MatchesStatus(item))
                .OrderBy(item => item, CreateComparer())
                .ToArray();
            _visibleItems.ReplaceAll(items);
            SelectedItem = selectedPath is null
                ? null
                : items.FirstOrDefault(item => string.Equals(
                    item.FilePath,
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase));
            VisibleItemCount = items.Length;
        }
    }

    private bool MatchesStatus(VideoLibraryItemViewModel item) => StatusFilter switch
    {
        VideoLibraryStatusFilter.Available => !item.HasError,
        VideoLibraryStatusFilter.MetadataFailed => item.HasError,
        VideoLibraryStatusFilter.Unplayed =>
            item.HistoryState == VideoPlaybackHistoryState.Unplayed,
        VideoLibraryStatusFilter.InProgress =>
            item.HistoryState == VideoPlaybackHistoryState.InProgress,
        VideoLibraryStatusFilter.Completed =>
            item.HistoryState == VideoPlaybackHistoryState.Completed,
        _ => true
    };

    private static bool MatchesSearch(VideoLibraryItemViewModel item, string query) =>
        query.Length == 0 ||
        item.FileNameWithoutExtension.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.PublicTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.PublicDescription.Contains(query, StringComparison.OrdinalIgnoreCase);

    private IComparer<VideoLibraryItemViewModel> CreateComparer() =>
        Comparer<VideoLibraryItemViewModel>.Create((left, right) =>
        {
            int comparison;
            if (SortField == VideoLibrarySortField.LastPlayedTime)
            {
                // 未播放项无论升降序都放在最后，避免切换方向时空值压过真正历史。
                if (left.LastPlayedUtc is null || right.LastPlayedUtc is null)
                {
                    comparison = left.LastPlayedUtc is null
                        ? right.LastPlayedUtc is null ? 0 : 1
                        : -1;
                    return comparison != 0 ? comparison : TieBreak(left, right);
                }
                comparison = left.LastPlayedUtc.Value.CompareTo(right.LastPlayedUtc.Value);
            }
            else
            {
                comparison = SortField switch
                {
                    VideoLibrarySortField.PublicTitle => StringComparer.OrdinalIgnoreCase.Compare(
                        string.IsNullOrWhiteSpace(left.PublicTitle)
                            ? left.FileNameWithoutExtension
                            : left.PublicTitle,
                        string.IsNullOrWhiteSpace(right.PublicTitle)
                            ? right.FileNameWithoutExtension
                            : right.PublicTitle),
                    VideoLibrarySortField.ModifiedTime =>
                        left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc),
                    _ => StringComparer.OrdinalIgnoreCase.Compare(
                        left.FileNameWithoutExtension,
                        right.FileNameWithoutExtension)
                };
            }

            if (comparison != 0 && SortDirection == VideoLibrarySortDirection.Descending)
                comparison = -comparison;
            return comparison != 0 ? comparison : TieBreak(left, right);
        });

    private static int TieBreak(
        VideoLibraryItemViewModel left,
        VideoLibraryItemViewModel right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);

    private void OnHistoryChanged(object? sender, PlaybackHistoryChangedEventArgs e)
    {
        void Apply()
        {
            if (IsClosing)
                return;
            lock (_projectionSync)
            {
                foreach (var (path, item) in _items.ToArray())
                {
                    if (e.Kind != PlaybackHistoryChangeKind.Cleared &&
                        !string.Equals(
                            item.FilePath,
                            e.FilePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var history = _historyStore.Find(
                        item.FilePath,
                        item.FileId,
                        item.OriginalFileLength);
                    _items[path] = new VideoLibraryItemViewModel(item.Source, history);
                }
                ApplyProjection(SelectedItem?.FilePath);
            }
        }

        _uiScheduler.Post(Apply);
    }

    private void PersistSettings()
    {
        if (_applyingPersistedSettings || IsClosing)
            return;
        var current = _settingsStore.CurrentSettings;
        _settingsStore.UpdateSettings(current with
        {
            RecentFolder = FolderPath,
            IncludeSubdirectories = IncludeSubdirectories,
            SortField = SortField,
            SortDirection = SortDirection,
            StatusFilter = StatusFilter
        });
    }

    private void ReplaceCancellation(
        ref CancellationTokenSource? field,
        out CancellationTokenSource replacement)
    {
        var previous = field;
        // 扫描与筛选仍可彼此替换，但二者共同服从 Host 的 Document 关闭信号。
        // 关闭后即使目录会话晚到，也只能结束任务，不能再修改可见列表。
        replacement = CancellationTokenSource.CreateLinkedTokenSource(
            _documentLifetime.ClosingToken);
        field = replacement;
        previous?.Cancel();
    }

    private bool IsClosing => _disposed || _documentLifetime.IsClosing;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _catalogGeneration);
        _catalogCancellation?.Cancel();
        _filterCancellation?.Cancel();
        _catalogCancellation = null;
        _filterCancellation = null;
        _historyStore.HistoryChanged -= OnHistoryChanged;
        GC.SuppressFinalize(this);
    }

    private sealed class VolatileUserDataStore :
        IVideoLibrarySettingsStore,
        IPlaybackHistoryStore
    {
        private readonly List<VideoPlaybackHistoryEntry> _history = [];
        public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;
        public VideoLibrarySettings CurrentSettings { get; private set; } =
            VideoLibrarySettings.Default;
        public void UpdateSettings(VideoLibrarySettings settings) => CurrentSettings = settings;
        public VideoPlaybackHistoryEntry? Find(string filePath, string fileId, long originalFileLength) =>
            _history.FirstOrDefault(item =>
                string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FileId, fileId, StringComparison.OrdinalIgnoreCase) &&
                item.OriginalFileLength == originalFileLength);
        public IReadOnlyList<VideoPlaybackHistoryEntry> GetAll() => _history.ToArray();
        public void Upsert(VideoPlaybackHistoryEntry entry)
        {
            Remove(entry.FilePath, entry.FileId, entry.OriginalFileLength);
            _history.Add(entry);
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(
                    PlaybackHistoryChangeKind.Upserted,
                    entry.FilePath));
        }
        public void Remove(string filePath, string fileId, long originalFileLength)
        {
            _history.RemoveAll(item =>
                string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FileId, fileId, StringComparison.OrdinalIgnoreCase) &&
                item.OriginalFileLength == originalFileLength);
        }
        public void Clear()
        {
            _history.Clear();
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(PlaybackHistoryChangeKind.Cleared));
        }
    }
}
