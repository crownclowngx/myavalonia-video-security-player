using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

/// <summary>
/// Document-scoped 的 SECVID03 批量解密队列。
/// </summary>
/// <remarks>
/// 候选检查、名称净化和单项解密仍属于 <see cref="IVideoDecryptionService"/>；本类型只拥有
/// 当前 Document 的队列修订、命令和公共密码。顺序、取消当前与取消全部由公共运行器保证。
/// </remarks>
public partial class DecryptionBatchViewModel : ObservableObject, IDisposable
{
    private readonly IVideoDecryptionService _decryptionService;
    private readonly ISequentialVideoQueueRunner<CandidateDecryptionPreflight> _queueRunner;
    private readonly ObservableCollection<DecryptionQueueItemViewModel> _items = [];
    private readonly ObservableCollection<VideoPreflightIssue> _preflightIssues = [];
    private readonly IDocumentLifetime _documentLifetime;

    // 运行器在后台检查项目是否仍存在，不能直接跨线程枚举 UI 的 ObservableCollection。
    private readonly ConcurrentDictionary<Guid, byte> _queuedItemIds = new();

    // Inspect/Preflight 只有整体取消语义；真正运行时的当前项/批次取消由运行器分别拥有。
    private CancellationTokenSource? _preflightCancellation;

    // 修订号阻止编辑后启动旧输出计划；代次阻止旧回调写入新批次或已关闭 Document。
    private readonly BatchOperationVersion _version = new();
    private BatchDecryptionPreflightResult? _preparedPlan;
    private bool _disposed;

    /// <summary>生产 DI 使用的完整构造函数。</summary>
    public DecryptionBatchViewModel(
        IVideoDecryptionService decryptionService,
        ISequentialVideoQueueRunner<CandidateDecryptionPreflight> queueRunner,
        IDocumentLifetime documentLifetime)
    {
        _decryptionService = decryptionService ?? throw new ArgumentNullException(nameof(decryptionService));
        _queueRunner = queueRunner ?? throw new ArgumentNullException(nameof(queueRunner));
        _documentLifetime = documentLifetime ?? throw new ArgumentNullException(nameof(documentLifetime));
        Items = new ReadOnlyObservableCollection<DecryptionQueueItemViewModel>(_items);
        PreflightIssues = new ReadOnlyObservableCollection<VideoPreflightIssue>(_preflightIssues);
        Queue = new DecryptionQueueViewModel(this);
    }

    public ReadOnlyObservableCollection<DecryptionQueueItemViewModel> Items { get; }
    public ReadOnlyObservableCollection<VideoPreflightIssue> PreflightIssues { get; }
    public int ItemCount => _items.Count;
    public bool HasItems => _items.Count > 0;
    public bool HasPreflightIssues => _preflightIssues.Count > 0;
    public bool HasPreparedPlan => IsPlanCurrent;
    public bool IsBusy => IsInspecting || IsPreflighting || IsRunning;
    /// <summary>子 View 的统一绑定根；隐藏的 Dock Owner 仍可通过 IDockable 契约访问。</summary>
    public DecryptionBatchViewModel Owner => this;
    public DecryptionQueueViewModel Queue { get; }
    public DecryptionBatchViewModel Batch => this;

    [ObservableProperty] private DecryptionQueueItemViewModel? _selectedItem;
    [ObservableProperty] private string _outputDirectory = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private OutputConflictPolicy _conflictPolicy =
        OutputConflictPolicy.GenerateUniqueName;
    [ObservableProperty] private bool _isInspecting;
    [ObservableProperty] private bool _isPreflighting;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private string _statusMessage = "请添加要解密的 SECVID03 视频";
    [ObservableProperty] private int _runnableCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _blockingCount;
    [ObservableProperty] private int _succeededCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _cancelledCount;

    /// <summary>解密保留 G2 自动改名的默认行为，同时允许用户选择严格阻止。</summary>
    public bool UseSafeRename
    {
        get => ConflictPolicy == OutputConflictPolicy.GenerateUniqueName;
        set => ConflictPolicy = value
            ? OutputConflictPolicy.GenerateUniqueName
            : OutputConflictPolicy.Block;
    }

    partial void OnSelectedItemChanged(DecryptionQueueItemViewModel? value) =>
        NotifyCommandStates();

    partial void OnOutputDirectoryChanged(string value)
    {
        if (!_disposed)
            InvalidatePlan(resetReadyItems: true);
    }

    partial void OnPasswordChanged(string value) => NotifyCommandStates();

    partial void OnConflictPolicyChanged(OutputConflictPolicy value)
    {
        OnPropertyChanged(nameof(UseSafeRename));
        if (!_disposed)
            InvalidatePlan(resetReadyItems: true);
    }

    partial void OnIsInspectingChanged(bool value) => OnBusyStateChanged();
    partial void OnIsPreflightingChanged(bool value) => OnBusyStateChanged();
    partial void OnIsRunningChanged(bool value) => OnBusyStateChanged();

    /// <summary>
    /// 读取公开信息并把新文件加入队列。候选检查可有限并发，但真实解密始终严格顺序。
    /// </summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (IsBusy || paths.Count == 0)
            return;

        var existingPaths = _items
            .Select(item => item.InputPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = paths
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return path; }
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && existingPaths.Add(path))
            .ToArray();
        if (newPaths.Length == 0)
        {
            StatusMessage = "所选文件已经在队列中";
            return;
        }

        var generation = _version.AdvanceOperation();
        var cancellation = ReplacePreflightCancellation();
        IsInspecting = true;
        StatusMessage = "正在读取视频公开信息...";
        try
        {
            var candidates = await _decryptionService.InspectAsync(newPaths, cancellation.Token);
            if (!IsCurrent(generation))
                return;

            foreach (var candidate in candidates)
            {
                var item = new DecryptionQueueItemViewModel(Guid.NewGuid(), candidate);
                _items.Add(item);
                _queuedItemIds[item.ItemId] = 0;
                SelectedItem ??= item;
            }

            InvalidatePlan(resetReadyItems: true);
            OnQueueChanged();
            StatusMessage = $"已添加 {candidates.Count} 个文件，共 {_items.Count} 个";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCurrent(generation))
                StatusMessage = "文件检查已取消";
        }
        catch
        {
            if (IsCurrent(generation))
                StatusMessage = "添加文件失败：无法读取所选文件。";
        }
        finally
        {
            ReleasePreflightCancellation(cancellation);
            if (IsCurrent(generation))
                IsInspecting = false;
        }
    }

    /// <summary>由窗口级文件夹选择器设置统一输出目录。</summary>
    public void SetOutputDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsBusy && !string.IsNullOrWhiteSpace(path))
            OutputDirectory = Path.GetFullPath(path);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        if (SelectedItem is not { } selected)
            return;
        if (!VideoQueueInteractionPolicy.CanRemove(
                selected.State,
                IsRunning,
                _queueRunner.CurrentItemId == selected.ItemId))
            return;

        _queuedItemIds.TryRemove(selected.ItemId, out _);
        _items.Remove(selected);
        SelectedItem = _items.FirstOrDefault();
        InvalidatePlan(resetReadyItems: !IsRunning);
        OnQueueChanged();
        StatusMessage = _items.Count == 0
            ? "请添加要解密的 SECVID03 视频"
            : $"队列中剩余 {_items.Count} 个文件";
    }

    private bool CanRemoveSelected() =>
        !_disposed &&
        SelectedItem is { } selected &&
        VideoQueueInteractionPolicy.CanRemove(
            selected.State,
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
        VideoQueueInteractionPolicy.CanRetry(selected.State);

    [RelayCommand(CanExecute = nameof(CanRetryAll))]
    private void RetryAll()
    {
        foreach (var item in _items.Where(item =>
                     VideoQueueInteractionPolicy.CanRetry(item.State)))
            item.ResetForRetry();

        InvalidatePlan(resetReadyItems: true);
        RecalculateCounts();
    }

    private bool CanRetryAll() =>
        !_disposed && !IsBusy &&
        _items.Any(item => VideoQueueInteractionPolicy.CanRetry(item.State));

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private void ClearCompleted()
    {
        foreach (var item in _items.Where(item =>
                     VideoQueueInteractionPolicy.CanClearCompleted(item.State)).ToArray())
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
        _items.Any(item => VideoQueueInteractionPolicy.CanClearCompleted(item.State));

    /// <summary>兼容 G2 页面和测试的全清命令；G5 UI 使用“清空已完成”。</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        _queuedItemIds.Clear();
        _items.Clear();
        _preflightIssues.Clear();
        SelectedItem = null;
        OverallProgress = 0;
        CurrentFile = string.Empty;
        ResetSummary();
        OnPropertyChanged(nameof(HasPreflightIssues));
        InvalidatePlan(resetReadyItems: false);
        OnQueueChanged();
        StatusMessage = "请添加要解密的 SECVID03 视频";
    }

    private bool CanClear() => !_disposed && !IsBusy && _items.Count > 0;

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    /// <summary>
    /// 第一阶段重新读取未成功候选并生成与队列修订绑定的输出计划。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckBatch))]
    private async Task CheckBatchAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workItems = _items.Where(item => item.State != VideoTaskState.Succeeded).ToArray();
        if (workItems.Length == 0)
            return;

        var generation = _version.AdvanceOperation();
        var revision = _version.Revision;
        var cancellation = ReplacePreflightCancellation();
        IsPreflighting = true;
        ResetSummary();
        _preflightIssues.Clear();
        OnPropertyChanged(nameof(HasPreflightIssues));
        foreach (var item in workItems)
        {
            item.State = VideoTaskState.Preflighting;
            item.Progress = 0;
            item.Message = "正在重新读取公开信息并检查输出...";
            item.FailureCode = null;
        }

        StatusMessage = $"正在检查 {workItems.Length} 个待处理项目...";
        try
        {
            var refreshed = await _decryptionService.InspectAsync(
                workItems.Select(item => item.InputPath).ToArray(),
                cancellation.Token);
            if (!IsCurrent(generation))
                return;

            var refreshedByPath = refreshed.ToDictionary(
                candidate => candidate.InputPath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in workItems)
            {
                if (refreshedByPath.TryGetValue(item.InputPath, out var candidate))
                    item.ApplyInspection(candidate);
            }

            var plan = await _decryptionService.PreflightAsync(
                workItems.Select(item => new DecryptionQueueRequest(item.ItemId, item.Candidate)).ToArray(),
                OutputDirectory,
                ConflictPolicy,
                cancellation.Token);
            if (!IsCurrent(generation) || !_version.TryAcceptPlan(generation, revision))
                return;

            foreach (var issue in plan.Overall.Issues)
                _preflightIssues.Add(issue);
            OnPropertyChanged(nameof(HasPreflightIssues));

            var itemById = _items.ToDictionary(item => item.ItemId);
            var globalBlocker = plan.Overall.Issues.FirstOrDefault(issue =>
                issue.Severity == PreflightSeverity.Blocking);
            foreach (var prepared in plan.Items)
            {
                if (itemById.TryGetValue(prepared.ItemId, out var item))
                {
                    if (globalBlocker is null)
                    {
                        item.ApplyPreflight(prepared);
                    }
                    else
                    {
                        item.State = VideoTaskState.Failed;
                        item.FailureCode = globalBlocker.Code;
                        item.Message = $"{globalBlocker.Message} {globalBlocker.SuggestedAction}";
                    }
                }
            }

            _preparedPlan = plan;
            ApplySummary(CreateSummary(plan));
            RecalculateCounts();
            StatusMessage = RunnableCount == 0
                ? "批次检查完成，但没有可执行项目。"
                : $"检查完成：可执行 {RunnableCount}，冲突 {ConflictCount}，" +
                  $"警告 {WarningCount}，阻止 {BlockingCount}。请确认后开始执行。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCurrent(generation))
            {
                foreach (var item in workItems.Where(item =>
                             item.State == VideoTaskState.Preflighting))
                    item.State = VideoTaskState.Cancelled;
                StatusMessage = "批次检查已取消。";
            }
        }
        catch (VideoTaskException ex)
        {
            if (IsCurrent(generation))
                StatusMessage = $"{ex.Message}（错误代码：{ex.FailureCode}）";
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
        !string.IsNullOrWhiteSpace(OutputDirectory) &&
        _items.Any(item => item.State != VideoTaskState.Succeeded);

    /// <summary>第二阶段严格顺序执行当前不可变计划。</summary>
    [RelayCommand(CanExecute = nameof(CanStartBatch))]
    private async Task StartBatchAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPlanCurrent || _preparedPlan is null)
            return;

        var runnable = _preparedPlan.Items.Where(item => item.CanRun).ToArray();
        if (runnable.Length == 0)
            return;

        var generation = _version.AdvanceOperation();
        var runId = Guid.NewGuid();
        IsRunning = true;
        OverallProgress = 0;
        StatusMessage = $"开始顺序解密 {runnable.Length} 个项目...";
        var progress = CreateContextProgress<VideoQueueProgress>(value =>
        {
            if (!IsCurrent(generation) || value.RunId != runId)
                return;

            var item = _items.FirstOrDefault(candidate => candidate.ItemId == value.ItemId);
            if (item is null)
                return;

            item.ApplyProgress(value);
            OverallProgress = value.OverallPercentage;
            CurrentFile = value.State == VideoTaskState.Running ? item.EncryptedFileName : string.Empty;
            StatusMessage = value.FailureCode is null
                ? value.Message
                : $"{value.Message}（错误代码：{value.FailureCode}）";
            RecalculateCounts();
            NotifyCommandStates();
        });

        try
        {
            // Password 只用于当前调用闭包，不进入 prepared item、运行器、进度或结果。
            var result = await _queueRunner.RunAsync(
                runId,
                runnable,
                itemId => _queuedItemIds.ContainsKey(itemId),
                (item, itemProgress, token) => _decryptionService.DecryptAsync(
                    item,
                    Password,
                    itemProgress,
                    token),
                progress,
                _documentLifetime.ClosingToken);
            if (!IsCurrent(generation))
                return;

            // 使已经排队、但晚于 RunAsync 返回的 Progress<T> 回调失效，批次结论才是最后
            // 一次用户可见更新。真实 Avalonia UI 仍由 Progress<T> 保证线程切换。
            _version.AdvanceOperation();
            if (IsClosing)
                return;

            CurrentFile = string.Empty;
            RecalculateCounts();
            var cancelledAll = result.CancelledCount > 0 &&
                               result.SucceededCount + result.FailedCount + result.RemovedBeforeStartCount <
                               result.TotalCount;
            if (!cancelledAll)
                OverallProgress = 100;
            StatusMessage =
                $"批次结束：成功 {result.SucceededCount}，失败 {result.FailedCount}，取消 {result.CancelledCount}。";
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
        !_disposed && !IsBusy && IsPlanCurrent &&
        _preparedPlan?.HasRunnableItems == true &&
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>G2 一次点击兼容入口；内部仍执行 G5 的检查和确认两个阶段。</summary>
    [RelayCommand(CanExecute = nameof(CanStartDecryption))]
    private async Task StartDecryptionAsync()
    {
        if (!IsPlanCurrent)
            await CheckBatchAsync();
        if (CanStartBatch())
            await StartBatchAsync();
    }

    private bool CanStartDecryption() =>
        !_disposed && !IsBusy &&
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(OutputDirectory) &&
        _items.Any(item => item.State != VideoTaskState.Succeeded);

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
            // 检查恰好结束时无需重复取消。
        }
        _queueRunner.CancelAll();
    }

    private bool CanCancelAll() => !_disposed && IsBusy;

    /// <summary>兼容 G2 页面“取消”命令。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => CancelAll();

    private bool CanCancel() => CanCancelAll();

    private VideoQueueBatchSummary CreateSummary(BatchDecryptionPreflightResult plan)
    {
        var runnable = plan.Overall.CanProceed
            ? plan.Items.Where(item => item.CanRun).ToArray()
            : [];
        return new VideoQueueBatchSummary(
            _items.Count,
            runnable.Length,
            plan.Items.Count(item => item.Result.Issues.Any(issue =>
                issue.Code == VideoTaskFailureCode.OutputConflict)),
            plan.Items.Count(item => item.Result.Issues.Any(issue =>
                issue.Severity == PreflightSeverity.Warning)),
            plan.Overall.CanProceed
                ? plan.Items.Count(item => !item.CanRun)
                : plan.Items.Count,
            _items.Count(item => item.State == VideoTaskState.Succeeded),
            runnable.Aggregate(0L, (total, item) =>
                item.RequiredBytes > long.MaxValue - total ? long.MaxValue : total + item.RequiredBytes));
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

    private bool IsPlanCurrent =>
        _preparedPlan is not null &&
        _version.HasCurrentPlan;

    private void InvalidatePlan(bool resetReadyItems)
    {
        _version.InvalidatePlan();
        _preparedPlan = null;
        ResetSummary();
        if (resetReadyItems)
        {
            foreach (var item in _items.Where(item => item.State == VideoTaskState.Ready))
            {
                item.State = VideoTaskState.Pending;
                item.Message = string.Empty;
                item.FailureCode = null;
            }
        }
        OnPropertyChanged(nameof(HasPreparedPlan));
        NotifyCommandStates();
    }

    private void RecalculateCounts()
    {
        SucceededCount = _items.Count(item => item.State == VideoTaskState.Succeeded);
        FailedCount = _items.Count(item => item.State == VideoTaskState.Failed);
        CancelledCount = _items.Count(item => item.State == VideoTaskState.Cancelled);
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(HasItems));
        RecalculateCounts();
        NotifyCommandStates();
    }

    private void OnBusyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        CheckBatchCommand.NotifyCanExecuteChanged();
        StartBatchCommand.NotifyCanExecuteChanged();
        StartDecryptionCommand.NotifyCanExecuteChanged();
        CancelCurrentCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        RetryAllCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource ReplacePreflightCancellation()
    {
        // Inspect 与 Preflight 共用本次计划令牌，并链接 Host 的关闭信号。这样关闭文档时
        // 不需要依赖 View 主动发命令，输出路径检查和公开信息读取都会协作退出。
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
        !IsClosing && _version.IsCurrentOperation(generation);

    private bool IsClosing => _disposed || _documentLifetime.IsClosing;

    /// <summary>
    /// 真实 UI 使用当前 SynchronizationContext 串行更新绑定；无上下文测试同步执行回调，
    /// 避免线程池调度造成与产品线程模型不一致的迟到写入。
    /// </summary>
    private static IProgress<T> CreateContextProgress<T>(Action<T> handler) =>
        SynchronizationContext.Current is null
            ? new InlineProgress<T>(handler)
            : new Progress<T>(handler);

    /// <summary>
    /// Document 关闭先使回调失效并清空密码，再发送取消；不在 UI 线程同步等待输入流、
    /// 密钥上下文和 partial 的异步清理。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _version.AdvanceOperation();
        Password = string.Empty;
        try
        {
            Interlocked.Exchange(ref _preflightCancellation, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已经完成。
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
