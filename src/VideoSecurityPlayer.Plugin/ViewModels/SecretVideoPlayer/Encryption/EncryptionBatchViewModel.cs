using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

/// <summary>
/// Document-scoped 的 SECVID03 批量加密队列。
/// </summary>
/// <remarks>
/// ViewModel 只负责表单、队列修订、命令和当前 Document 的密码。批次路径/空间预检属于
/// <see cref="IVideoBatchEncryptionService"/>，单文件加密属于 <see cref="IVideoEncryptionService"/>，
/// 严格顺序与取消语义属于 <see cref="ISequentialVideoQueueRunner{TPreparedItem}"/>。
/// </remarks>
public partial class EncryptionBatchViewModel : ObservableObject, IDisposable
{
    private readonly IVideoEncryptionService _singleFileService;
    private readonly IVideoBatchEncryptionService _batchService;
    private readonly ISequentialVideoQueueRunner<PreparedEncryptionItem> _queueRunner;
    private readonly ObservableCollection<EncryptionQueueItemViewModel> _items = [];
    private readonly ObservableCollection<VideoPreflightIssue> _overallIssues = [];
    private readonly IDocumentLifetime _documentLifetime;

    // 运行器会在后台线程开始下一项，而 UI 允许移除尚未开始的项目。并发集合为这两个所有者
    // 提供最小共享事实，避免后台线程直接枚举 ObservableCollection。
    private readonly ConcurrentDictionary<Guid, byte> _queuedItemIds = new();

    // 预检取消与运行器的批次/单项取消刻意分离：预检没有“当前加密项”，只能整体取消。
    private CancellationTokenSource? _preflightCancellation;

    // 队列修订号防止用户编辑后启动旧计划；运行代次防止旧异步回调更新新批次或已关闭文档。
    private long _queueRevision;
    private long _preparedRevision = -1;
    private int _operationGeneration;
    private BatchEncryptionPlan? _preparedPlan;
    private bool _disposed;

    /// <summary>
    /// DI 使用的完整构造函数；三个依赖分别承担单文件用例、批次计划和顺序编排。
    /// </summary>
    public EncryptionBatchViewModel(
        IVideoEncryptionService singleFileService,
        IVideoBatchEncryptionService batchService,
        ISequentialVideoQueueRunner<PreparedEncryptionItem> queueRunner,
        IDocumentLifetime documentLifetime)
    {
        _singleFileService = singleFileService ?? throw new ArgumentNullException(nameof(singleFileService));
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _queueRunner = queueRunner ?? throw new ArgumentNullException(nameof(queueRunner));
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        Items = new ReadOnlyObservableCollection<EncryptionQueueItemViewModel>(_items);
        PreflightIssues = new ReadOnlyObservableCollection<VideoPreflightIssue>(_overallIssues);
        Queue = new EncryptionQueueViewModel(this);
    }

    /// <summary>只读队列，集合修改只能经由 Document 命令完成。</summary>
    public ReadOnlyObservableCollection<EncryptionQueueItemViewModel> Items { get; }

    /// <summary>批次级问题；单项问题保存在对应项目状态中。</summary>
    public ReadOnlyObservableCollection<VideoPreflightIssue> PreflightIssues { get; }

    /// <summary>界面可选择的两种非覆盖策略。</summary>
    public IReadOnlyList<OutputConflictPolicy> ConflictPolicies { get; } =
        Enum.GetValues<OutputConflictPolicy>();

    public int ItemCount => _items.Count;
    public bool HasItems => _items.Count > 0;
    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasPreflightIssues => _overallIssues.Count > 0;
    public bool HasPreparedPlan => IsPlanCurrent;
    public bool IsBusy => IsPreflighting || IsRunning;
    /// <summary>子 View 的统一绑定根；隐藏的 Dock Owner 仍可通过 IDockable 契约访问。</summary>
    public EncryptionBatchViewModel Owner => this;
    public EncryptionQueueViewModel Queue { get; }
    public EncryptionBatchViewModel Batch => this;

    // 旧单文件绑定和测试通过以下别名映射到当前选中项目。新批量 UI 直接绑定 SelectedItem，
    // 避免在兼容层继续复制一份领域状态。
    public string SelectedFilePath
    {
        get => SelectedItem?.InputPath ?? string.Empty;
        set => SetLegacyInput(value);
    }

    public string OutputFilePath
    {
        get => SelectedItem?.RequestedOutputPath ?? string.Empty;
        set
        {
            if (SelectedItem is not null)
                SelectedItem.RequestedOutputPath = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string VideoTitle
    {
        get => SelectedItem?.PublicTitle ?? string.Empty;
        set
        {
            if (SelectedItem is not null)
                SelectedItem.PublicTitle = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoTitleCharacterCount));
        }
    }

    public string Description
    {
        get => SelectedItem?.PublicDescription ?? string.Empty;
        set
        {
            if (SelectedItem is not null)
                SelectedItem.PublicDescription = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DescriptionCharacterCount));
        }
    }

    public int VideoTitleCharacterCount =>
        EncryptedVideoContainer.CountRunes(VideoTitle);

    public int DescriptionCharacterCount =>
        EncryptedVideoContainer.CountRunes(Description);

    /// <summary>兼容旧页面和测试的运行标志。</summary>
    public bool IsEncrypting => IsRunning;

    [ObservableProperty] private EncryptionQueueItemViewModel? _selectedItem;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private OutputConflictPolicy _conflictPolicy = OutputConflictPolicy.Block;
    [ObservableProperty] private bool _isPreflighting;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private string _statusMessage = "请添加要加密的视频文件";
    [ObservableProperty] private int _runnableCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _blockingCount;
    [ObservableProperty] private int _succeededCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _cancelledCount;

    // 以下显示属性保留旧视图契约；新队列页主要使用总体和单项进度。
    [ObservableProperty] private string _progressText = "0%";
    [ObservableProperty] private string _estimatedTimeText = string.Empty;
    [ObservableProperty] private string _processingSpeedText = string.Empty;
    [ObservableProperty] private string _fileSizeText = string.Empty;
    [ObservableProperty] private string _fileFormatText = string.Empty;
    [ObservableProperty] private string _failureCodeText = string.Empty;
    [ObservableProperty] private VideoTaskState _taskState = VideoTaskState.Pending;

    public double Progress
    {
        get => OverallProgress;
        set => OverallProgress = value;
    }

    /// <summary>
    /// 面向 UI 的布尔冲突选项，避免把内部英文枚举值直接展示给用户。
    /// </summary>
    public bool UseSafeRename
    {
        get => ConflictPolicy == OutputConflictPolicy.GenerateUniqueName;
        set => ConflictPolicy = value
            ? OutputConflictPolicy.GenerateUniqueName
            : OutputConflictPolicy.Block;
    }

    partial void OnSelectedItemChanged(EncryptionQueueItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedItem));
        OnPropertyChanged(nameof(SelectedFilePath));
        OnPropertyChanged(nameof(OutputFilePath));
        OnPropertyChanged(nameof(VideoTitle));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(VideoTitleCharacterCount));
        OnPropertyChanged(nameof(DescriptionCharacterCount));
        NotifyCommandStates();
    }

    partial void OnPasswordChanged(string value) => NotifyCommandStates();
    partial void OnConfirmPasswordChanged(string value) => NotifyCommandStates();

    partial void OnConflictPolicyChanged(OutputConflictPolicy value)
    {
        OnPropertyChanged(nameof(UseSafeRename));
        if (!_disposed)
            InvalidatePlan(resetReadyItems: true);
    }

    partial void OnIsPreflightingChanged(bool value) => OnBusyChanged();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEncrypting));
        OnBusyChanged();
    }

    partial void OnOverallProgressChanged(double value)
    {
        ProgressText = $"{value:F1}%";
        OnPropertyChanged(nameof(Progress));
    }

    /// <summary>
    /// 将文件选择器返回的普通视频加入队列，并按规范化路径大小写不敏感去重。
    /// </summary>
    public Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (IsBusy || paths.Count == 0)
            return Task.CompletedTask;

        var existing = _items.Select(item => item.InputPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                fullPath = path;
            }

            if (!existing.Add(fullPath))
                continue;

            var item = new EncryptionQueueItemViewModel(
                Guid.NewGuid(),
                fullPath,
                CreateDefaultOutputPath(fullPath),
                OnItemRequestChanged);
            _items.Add(item);
            _queuedItemIds[item.ItemId] = 0;
            SelectedItem ??= item;
            added++;
        }

        if (added > 0)
        {
            InvalidatePlan(resetReadyItems: true);
            StatusMessage = $"已添加 {added} 个文件，共 {_items.Count} 个";
        }
        else
        {
            StatusMessage = "所选文件已经在队列中";
        }

        OnQueueChanged();
        return Task.CompletedTask;
    }

    /// <summary>生成当前源文件旁的默认 SECVID03 输出路径。</summary>
    public static string CreateDefaultOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{baseName}_encrypted.secvid");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        if (SelectedItem is not { } selected)
            return;

        var isCurrent = _queueRunner.CurrentItemId == selected.ItemId;
        if (!VideoQueueInteractionPolicy.CanRemove(selected.Status.State, IsRunning, isCurrent))
            return;

        _queuedItemIds.TryRemove(selected.ItemId, out _);
        _items.Remove(selected);
        SelectedItem = _items.FirstOrDefault();
        InvalidatePlan(resetReadyItems: !IsRunning);
        OnQueueChanged();
        StatusMessage = _items.Count == 0
            ? "请添加要加密的视频文件"
            : $"队列中剩余 {_items.Count} 个文件";
    }

    private bool CanRemoveSelected() =>
        !_disposed &&
        SelectedItem is { } selected &&
        VideoQueueInteractionPolicy.CanRemove(
            selected.Status.State,
            IsRunning,
            _queueRunner.CurrentItemId == selected.ItemId);

    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private void RetrySelected()
    {
        if (SelectedItem is not { } selected)
            return;
        selected.ResetForRetry();
        InvalidatePlan(resetReadyItems: true);
        RecalculateCounts();
    }

    private bool CanRetrySelected() =>
        !_disposed && !IsBusy && SelectedItem is { } selected &&
        VideoQueueInteractionPolicy.CanRetry(selected.Status.State);

    [RelayCommand(CanExecute = nameof(CanRetryAll))]
    private void RetryAll()
    {
        foreach (var item in _items.Where(item =>
                     VideoQueueInteractionPolicy.CanRetry(item.Status.State)))
        {
            item.ResetForRetry();
        }

        InvalidatePlan(resetReadyItems: true);
        RecalculateCounts();
    }

    private bool CanRetryAll() =>
        !_disposed && !IsBusy &&
        _items.Any(item => VideoQueueInteractionPolicy.CanRetry(item.Status.State));

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private void ClearCompleted()
    {
        foreach (var item in _items.Where(item =>
                     VideoQueueInteractionPolicy.CanClearCompleted(item.Status.State)).ToArray())
        {
            _queuedItemIds.TryRemove(item.ItemId, out _);
            _items.Remove(item);
        }

        SelectedItem = _items.FirstOrDefault();
        InvalidatePlan(resetReadyItems: true);
        OnQueueChanged();
    }

    private bool CanClearCompleted() =>
        !_disposed && !IsBusy &&
        _items.Any(item => VideoQueueInteractionPolicy.CanClearCompleted(item.Status.State));

    /// <summary>
    /// 兼容旧单文件页面的“清空”命令；新页面通过“清空已完成”和逐项移除避免误删错误证据。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private void ClearAll()
    {
        _queuedItemIds.Clear();
        _items.Clear();
        _overallIssues.Clear();
        SelectedItem = null;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ResetPresentation();
        InvalidatePlan(resetReadyItems: false);
        OnQueueChanged();
        StatusMessage = "请添加要加密的视频文件";
    }

    private bool CanClearAll() => !_disposed && !IsBusy && _items.Count > 0;

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    /// <summary>
    /// 第一阶段：生成与当前队列修订绑定的不可变计划。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckBatch))]
    private async Task CheckBatchAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var generation = Interlocked.Increment(ref _operationGeneration);
        var revision = Volatile.Read(ref _queueRevision);
        var cancellation = ReplacePreflightCancellation();
        var workItems = _items.Where(item =>
            item.Status.State != VideoTaskState.Succeeded).ToArray();

        foreach (var item in workItems)
        {
            item.Status.State = VideoTaskState.Preflighting;
            item.Status.Progress = 0;
            item.Status.Message = "正在检查输入、输出和磁盘空间...";
            item.Status.FailureCode = null;
        }

        IsPreflighting = true;
        ResetSummary();
        StatusMessage = $"正在检查 {workItems.Length} 个待处理项目...";
        try
        {
            var plan = await _batchService.PrepareAsync(
                workItems.Select(item => item.CreateRequest()).ToArray(),
                ConflictPolicy,
                _items.Count(item => item.Status.State == VideoTaskState.Succeeded),
                cancellation.Token);
            if (!IsCurrent(generation) || revision != Volatile.Read(ref _queueRevision))
                return;

            var itemById = _items.ToDictionary(item => item.ItemId);
            foreach (var prepared in plan.Items)
            {
                if (itemById.TryGetValue(prepared.ItemId, out var item))
                    item.ApplyPreflight(prepared);
            }

            _overallIssues.Clear();
            foreach (var issue in plan.OverallIssues)
                _overallIssues.Add(issue);
            OnPropertyChanged(nameof(HasPreflightIssues));

            _preparedPlan = plan;
            _preparedRevision = revision;
            ApplySummary(plan.Summary);
            RecalculateCounts();
            StatusMessage = plan.Summary.RunnableCount == 0
                ? "批次检查完成，但没有可执行项目。"
                : $"检查完成：可执行 {plan.Summary.RunnableCount}，冲突 {plan.Summary.ConflictCount}，" +
                  $"警告 {plan.Summary.WarningCount}，阻止 {plan.Summary.BlockingCount}。请确认后开始执行。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCurrent(generation))
            {
                foreach (var item in workItems.Where(item =>
                             item.Status.State == VideoTaskState.Preflighting))
                    item.Status.State = VideoTaskState.Cancelled;
                StatusMessage = "批次检查已取消。";
            }
        }
        catch
        {
            if (IsCurrent(generation))
                StatusMessage = "批次检查失败：发生未预期错误，请检查输入和输出环境。";
        }
        finally
        {
            ReleasePreflightCancellation(cancellation);
            if (IsCurrent(generation))
                IsPreflighting = false;
        }
    }

    private bool CanCheckBatch() =>
        !_disposed && !IsBusy &&
        _items.Any(item => item.Status.State != VideoTaskState.Succeeded);

    /// <summary>
    /// 第二阶段：只运行与当前修订匹配且预检通过的项目。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartBatch))]
    private async Task StartBatchAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPlanCurrent || _preparedPlan is null)
            return;

        var runnable = _preparedPlan.Items.Where(item => item.CanRun).ToArray();
        if (runnable.Length == 0)
            return;

        var generation = Interlocked.Increment(ref _operationGeneration);
        var runId = Guid.NewGuid();
        IsRunning = true;
        OverallProgress = 0;
        StatusMessage = $"开始顺序加密 {runnable.Length} 个项目...";

        var progress = CreateContextProgress<VideoQueueProgress>(value =>
        {
            if (!IsCurrent(generation) || value.RunId != runId)
                return;

            var item = _items.FirstOrDefault(candidate => candidate.ItemId == value.ItemId);
            if (item is null)
                return;

            item.ApplyProgress(value);
            OverallProgress = value.OverallPercentage;
            CurrentFile = value.State == VideoTaskState.Running ? item.FileName : string.Empty;
            TaskState = value.State;
            FailureCodeText = value.FailureCode?.ToString() ?? string.Empty;
            StatusMessage = value.State == VideoTaskState.Cancelled
                ? "加密已取消，未完成的临时文件已进入清理流程。"
                : value.FailureCode is null
                    ? value.Message
                    : $"{value.Message}（错误代码：{value.FailureCode}）";
            RecalculateCounts();
            NotifyCommandStates();
        });

        try
        {
            // Password 只被闭包用于当前 RunAsync 调用；它没有进入 prepared item、运行器字段、
            // 进度或结果模型。单文件服务仍会在真正写入前重新预检。
            var result = await _queueRunner.RunAsync(
                runId,
                runnable,
                itemId => _queuedItemIds.ContainsKey(itemId),
                (item, itemProgress, token) => _singleFileService.EncryptAsync(
                    item.Request,
                    Password,
                    itemProgress,
                    token),
                progress,
                _documentLifetime.ClosingToken);

            if (!IsCurrent(generation))
                return;

            // Progress<T> 在无 UI SynchronizationContext 的测试环境中可能把最后一个回调排到
            // RunAsync 完成之后。先推进代次使这些回调失效，再写入权威批次结论。
            Interlocked.Increment(ref _operationGeneration);
            if (IsClosing)
                return;

            CurrentFile = string.Empty;
            RecalculateCounts();
            var cancelledAll = result.CancelledCount > 0 &&
                               result.SucceededCount + result.FailedCount + result.RemovedBeforeStartCount <
                               result.TotalCount;
            if (!cancelledAll)
                OverallProgress = 100;
            StatusMessage = result.CancelledCount > 0 &&
                            result.SucceededCount == 0 &&
                            result.FailedCount == 0
                ? $"加密已取消：取消 {result.CancelledCount} 个项目，已提交文件予以保留。"
                : $"批次结束：成功 {result.SucceededCount}，失败 {result.FailedCount}，" +
                  $"取消 {result.CancelledCount}。";
        }
        finally
        {
            if (!IsClosing)
                IsRunning = false;
            if (!IsClosing)
                InvalidatePlan(resetReadyItems: false);
        }
    }

    private bool CanStartBatch() =>
        !_disposed &&
        !IsBusy &&
        IsPlanCurrent &&
        _preparedPlan?.Summary.RunnableCount > 0 &&
        Password.Length >= 6 &&
        Password == ConfirmPassword;

    /// <summary>
    /// 兼容旧单文件“一次点击”入口：内部仍严格执行检查和确认两个阶段。
    /// 新批量 UI 不使用此命令。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartEncryption))]
    private async Task StartEncryptionAsync()
    {
        if (!IsPlanCurrent)
            await CheckBatchAsync();
        if (CanStartBatch())
            await StartBatchAsync();
    }

    private bool CanStartEncryption() =>
        !_disposed && !IsBusy && HasItems &&
        Password.Length >= 6 && Password == ConfirmPassword;

    [RelayCommand(CanExecute = nameof(CanCancelCurrent))]
    private void CancelCurrent() => _queueRunner.CancelCurrent();

    private bool CanCancelCurrent() =>
        !_disposed && IsRunning && _queueRunner.CurrentItemId.HasValue;

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        try
        {
            Volatile.Read(ref _preflightCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 预检恰好结束时无需重复处理。
        }
        _queueRunner.CancelAll();
    }

    private bool CanCancelAll() => !_disposed && IsBusy;

    /// <summary>兼容旧单文件页面的取消命令，单项目时与取消全部语义等价。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelEncryption))]
    private void CancelEncryption() => CancelAll();

    private bool CanCancelEncryption() => CanCancelAll();

    /// <summary>重新为兼容入口生成源文件旁建议输出。</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateOutputPath))]
    private void GenerateOutputPath()
    {
        if (SelectedItem is not null)
            SelectedItem.RequestedOutputPath = CreateDefaultOutputPath(SelectedItem.InputPath);
    }

    private bool CanGenerateOutputPath() =>
        !_disposed && !IsBusy && SelectedItem is not null;

    private bool IsPlanCurrent =>
        _preparedPlan is not null &&
        _preparedRevision == Volatile.Read(ref _queueRevision);

    private void OnItemRequestChanged(EncryptionQueueItemViewModel item)
    {
        InvalidatePlan(resetReadyItems: true);
        OnPropertyChanged(nameof(OutputFilePath));
        OnPropertyChanged(nameof(VideoTitle));
        OnPropertyChanged(nameof(Description));
        NotifyCommandStates();
    }

    private void InvalidatePlan(bool resetReadyItems)
    {
        Interlocked.Increment(ref _queueRevision);
        _preparedPlan = null;
        _preparedRevision = -1;
        ResetSummary();
        if (resetReadyItems)
        {
            foreach (var item in _items.Where(item =>
                         item.Status.State == VideoTaskState.Ready))
            {
                item.Status.State = VideoTaskState.Pending;
                item.Status.Message = string.Empty;
                item.Status.FailureCode = null;
            }
        }
        OnPropertyChanged(nameof(HasPreparedPlan));
        NotifyCommandStates();
    }

    private void ApplySummary(VideoQueueBatchSummary summary)
    {
        RunnableCount = summary.RunnableCount;
        ConflictCount = summary.ConflictCount;
        WarningCount = summary.WarningCount;
        BlockingCount = summary.BlockingCount;
        OnPropertyChanged(nameof(HasPreparedPlan));
        NotifyCommandStates();
    }

    private void ResetSummary()
    {
        RunnableCount = 0;
        ConflictCount = 0;
        WarningCount = 0;
        BlockingCount = 0;
    }

    private void RecalculateCounts()
    {
        SucceededCount = _items.Count(item => item.Status.State == VideoTaskState.Succeeded);
        FailedCount = _items.Count(item => item.Status.State == VideoTaskState.Failed);
        CancelledCount = _items.Count(item => item.Status.State == VideoTaskState.Cancelled);
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(HasItems));
        RecalculateCounts();
        NotifyCommandStates();
    }

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        CheckBatchCommand.NotifyCanExecuteChanged();
        StartBatchCommand.NotifyCanExecuteChanged();
        StartEncryptionCommand.NotifyCanExecuteChanged();
        CancelCurrentCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        CancelEncryptionCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        RetryAllCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
        GenerateOutputPathCommand.NotifyCanExecuteChanged();
    }

    private void ResetPresentation()
    {
        OverallProgress = 0;
        CurrentFile = string.Empty;
        TaskState = VideoTaskState.Pending;
        FailureCodeText = string.Empty;
        ProcessingSpeedText = string.Empty;
        EstimatedTimeText = string.Empty;
        FileSizeText = string.Empty;
        FileFormatText = string.Empty;
        ResetSummary();
    }

    private CancellationTokenSource ReplacePreflightCancellation()
    {
        // 预检有自己的“新计划替换旧计划”取消源，同时链接 Host 的永久关闭令牌。
        // 这两种取消原因都只终止当前工作，不改变已成功提交的输出文件。
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _documentLifetime.ClosingToken);
        var previous = Interlocked.Exchange(ref _preflightCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        return cancellation;
    }

    private void ReleasePreflightCancellation(CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref _preflightCancellation, null, cancellation);
        cancellation.Dispose();
    }

    private bool IsCurrent(int generation) =>
        !IsClosing && generation == Volatile.Read(ref _operationGeneration);

    private bool IsClosing => _disposed || _documentLifetime.IsClosing;

    /// <summary>
    /// Avalonia UI 线程具有 SynchronizationContext，使用 Progress 回投界面；纯单元测试没有
    /// 上下文时改为同步回调，避免线程池中的迟到回调制造并不存在于真实 UI 的数据竞争。
    /// </summary>
    private static IProgress<T> CreateContextProgress<T>(Action<T> handler) =>
        SynchronizationContext.Current is null
            ? new InlineProgress<T>(handler)
            : new Progress<T>(handler);

    private void SetLegacyInput(string value)
    {
        if (_disposed || string.IsNullOrWhiteSpace(value))
            return;

        var fullPath = Path.GetFullPath(value);
        if (SelectedItem is not null &&
            string.Equals(SelectedItem.InputPath, fullPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsBusy)
        {
            _queuedItemIds.Clear();
            _items.Clear();
            SelectedItem = null;
            AddFilesAsync([fullPath]).GetAwaiter().GetResult();
            UpdateLegacyFileInfo(fullPath);
            OnPropertyChanged(nameof(SelectedFilePath));
        }
    }

    private void UpdateLegacyFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            FileSizeText = info.Exists ? $"文件大小: {info.Length / (1024d * 1024):F2} MB" : string.Empty;
            FileFormatText = $"文件格式: {Path.GetExtension(path).ToLowerInvariant()}";
        }
        catch
        {
            FileSizeText = string.Empty;
            FileFormatText = string.Empty;
        }
    }

    /// <summary>
    /// 关闭 Document 时先使回调失效、清空密码，再发送取消。这里不等待运行器完成：
    /// 输入流、密码学上下文和 partial 的异步释放由调用链自行收尾，UI 线程不得被阻塞。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Increment(ref _operationGeneration);
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        try
        {
            Interlocked.Exchange(ref _preflightCancellation, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已完成。
        }
        _queueRunner.CancelAll();
        _queuedItemIds.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
