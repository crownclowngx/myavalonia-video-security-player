using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Constants.SecretVideoPlayer;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>
/// 播放器展示模型。只把用户意图和原生表面通知转交给播放会话，不编排 LibVLC 生命周期。
/// </summary>
public partial class PlaybackCoordinatorViewModel : ObservableObject, IDisposable
{
    private readonly ISecureVideoPlaybackSession _session;
    private readonly IPlaybackSurfaceSession _surfaceSession;
    private readonly IPlaybackPlatformStatus _platformStatus;
    private readonly IPlaybackBackendInitializer _backendInitializer;
    private readonly IPlaybackPreferenceStore? _preferenceStore;
    private readonly CapturedUiScheduler _uiScheduler = new();
    private readonly CapturedUiPeriodicTimer _positionTimer;
    private IPlaybackDiagnosticExporter? _diagnosticExporter;
    private bool _applyingSnapshot;
    private int _diagnosticExportGate;
    private long _lastEndedGeneration;
    private long _fullscreenRequestRevision;
    private bool _disposed;

    public event EventHandler<PlaybackMediaEndedEventArgs>? MediaEnded;
    public event EventHandler<FullscreenPresentationRequestedEventArgs>? FullscreenPresentationRequested;

    public bool IsPlaying => CurrentState == PlayerStateEnum.Playing;
    public bool IsPaused => CurrentState == PlayerStateEnum.Paused;
    public IReadOnlyList<float> AvailableRates { get; } =
        [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];

    [ObservableProperty] private string _currentTime = "00:00:00";
    [ObservableProperty] private string _totalTime = "00:00:00";
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _volume = 50;
    [ObservableProperty] private bool _isSeekable;
    [ObservableProperty] private string _bufferInfo = string.Empty;
    [ObservableProperty] private PlayerStateEnum _currentState = PlayerStateEnum.Stopped;
    [ObservableProperty] private bool _isSliderBeingDragged;
    [ObservableProperty] private string _statusMessage = "播放器就绪";
    [ObservableProperty] private bool _isVideoSurfaceReady;
    [ObservableProperty] private bool _isMediaTransitioning;
    [ObservableProperty] private PlaybackFailure? _lastFailure;
    [ObservableProperty] private bool _isPlaybackAvailable;
    [ObservableProperty] private string _deploymentIssueText = string.Empty;
    [ObservableProperty] private string _deploymentCheckedPath = string.Empty;
    [ObservableProperty] private string _deploymentSuggestedAction = string.Empty;
    [ObservableProperty] private float _selectedRate = 1.0f;
    [ObservableProperty] private IReadOnlyList<PlaybackTrackOption> _audioTracks =
        Array.Empty<PlaybackTrackOption>();
    [ObservableProperty] private PlaybackTrackOption? _selectedAudioTrack;
    [ObservableProperty] private IReadOnlyList<PlaybackTrackOption> _subtitleTracks =
        Array.Empty<PlaybackTrackOption>();
    [ObservableProperty] private PlaybackTrackOption? _selectedSubtitleTrack;
    [ObservableProperty] private bool _hasAudioTracks;
    [ObservableProperty] private bool _hasSubtitleTracks;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private bool _isFullscreenTransitioning;
    [ObservableProperty] private bool _isExportingDiagnostics;
    [ObservableProperty] private string _diagnosticsStatusMessage = string.Empty;

    /// <summary>仅供播放器 View 的表面协调器绑定，不包含任何原生句柄。</summary>
    public IPlaybackSurfaceSession? SurfaceSession => _disposed ? null : _surfaceSession;

    public PlaybackPlatformCapabilities PlatformCapabilities =>
        _platformStatus.Capabilities;

    public bool SupportsEmbeddedFullscreen =>
        PlatformCapabilities.SupportsEmbeddedFullscreen;

    public bool SupportsAudioTrackSelection =>
        PlatformCapabilities.SupportsAudioTrackSelection;

    public bool SupportsSubtitleTrackSelection =>
        PlatformCapabilities.SupportsSubtitleTrackSelection;

    /// <summary>供宿主状态展示和 G3 脱敏集成门禁读取的只读快照。</summary>
    public PlaybackSnapshot PlaybackSnapshot => _session.Snapshot;
    public bool CanExportDiagnostics =>
        _diagnosticExporter is not null && !IsExportingDiagnostics;

    public PlaybackStateViewModel State { get; }
    public PlaybackTransportViewModel Transport { get; }
    public PlaybackOptionsViewModel Options { get; }
    public PlaybackMediaViewModel Media { get; }
    public PlaybackPresentationViewModel Presentation { get; }

    public PlaybackCoordinatorViewModel(
        ISecureVideoPlaybackSession session,
        IPlaybackSurfaceSession surfaceSession,
        IPlaybackPlatformStatus platformStatus,
        IPlaybackBackendInitializer backendInitializer,
        IPlaybackPreferenceStore? preferenceStore = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _surfaceSession = surfaceSession ??
                          throw new ArgumentNullException(nameof(surfaceSession));
        _platformStatus = platformStatus ??
                          throw new ArgumentNullException(nameof(platformStatus));
        _backendInitializer = backendInitializer ??
                              throw new ArgumentNullException(nameof(backendInitializer));
        _preferenceStore = preferenceStore;
        _session.Changed += OnPlaybackChanged;
        var preferences = preferenceStore?.CurrentPreferences ?? PlaybackPreferences.Default;
        _session.ApplyInitialPreferences(preferences.Volume, preferences.Rate);
        Volume = preferences.Volume;
        SelectedRate = preferences.Rate;

        _positionTimer = _uiScheduler.CreatePeriodicTimer(
            TimeSpan.FromMilliseconds(100),
            OnPositionTimerTick);
        State = new PlaybackStateViewModel(this);
        Transport = new PlaybackTransportViewModel(this);
        Options = new PlaybackOptionsViewModel(this);
        Media = new PlaybackMediaViewModel(this);
        Presentation = new PlaybackPresentationViewModel(this);
        RefreshDeploymentStatus();
    }

    partial void OnCurrentStateChanged(PlayerStateEnum value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        NotifyCommandStates();
    }

    partial void OnIsExportingDiagnosticsChanged(bool value) =>
        OnPropertyChanged(nameof(CanExportDiagnostics));

    /// <summary>
    /// 由插件装配边界注入诊断导出器，避免把内部诊断端口扩散到公开构造器。
    /// </summary>
    internal void ConfigureDiagnosticExporter(IPlaybackDiagnosticExporter exporter)
    {
        _diagnosticExporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        OnPropertyChanged(nameof(CanExportDiagnostics));
    }

    /// <summary>在内存中创建完整脱敏 JSON；本方法不接触保存路径。</summary>
    public async Task<ReadOnlyMemory<byte>> CreateDiagnosticJsonAsync(
        CancellationToken cancellationToken = default)
    {
        if (_diagnosticExporter is null)
            throw new InvalidOperationException("诊断导出器尚未配置。");
        if (Interlocked.CompareExchange(ref _diagnosticExportGate, 1, 0) != 0)
            throw new InvalidOperationException("诊断正在导出。");

        IsExportingDiagnostics = true;
        DiagnosticsStatusMessage = string.Empty;
        try
        {
            return await _diagnosticExporter
                .CreateJsonAsync(LastFailure, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            IsExportingDiagnostics = false;
            Volatile.Write(ref _diagnosticExportGate, 0);
        }
    }

    internal void ReportDiagnosticExportSucceeded() =>
        DiagnosticsStatusMessage = "脱敏诊断已导出";

    internal void ReportDiagnosticExportFailed() =>
        DiagnosticsStatusMessage = "无法写入所选位置";

    partial void OnVolumeChanged(double value)
    {
        if (!_disposed && !IsMediaTransitioning)
        {
            _session.SetVolume((int)value);
            _preferenceStore?.UpdatePreferences(new PlaybackPreferences(
                (int)Math.Clamp(value, 0, 100),
                SelectedRate));
        }
    }

    partial void OnIsVideoSurfaceReadyChanged(bool value) => NotifyCommandStates();
    partial void OnIsMediaTransitioningChanged(bool value) => NotifyCommandStates();
    partial void OnIsPlaybackAvailableChanged(bool value) => NotifyCommandStates();
    partial void OnIsFullscreenTransitioningChanged(bool value) => NotifyCommandStates();

    partial void OnSelectedRateChanged(float value)
    {
        if (!_applyingSnapshot && CanChangeAdvancedControl())
        {
            _ = SetRateFromSelectionAsync(value);
        }
    }

    partial void OnSelectedAudioTrackChanged(PlaybackTrackOption? value)
    {
        if (!_applyingSnapshot &&
            SupportsAudioTrackSelection &&
            value is not null &&
            CanChangeAdvancedControl())
        {
            _ = SelectAudioTrackFromSelectionAsync(value.Id);
        }
    }

    partial void OnSelectedSubtitleTrackChanged(PlaybackTrackOption? value)
    {
        if (!_applyingSnapshot &&
            SupportsSubtitleTrackSelection &&
            value is not null &&
            CanChangeAdvancedControl())
        {
            _ = SelectSubtitleTrackFromSelectionAsync(value.Id);
        }
    }

    [RelayCommand]
    private void RetryDeploymentCheck() => RefreshDeploymentStatus();

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        var result = await _session.PlayAsync();
        ApplyFailure(result);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        var result = await _session.PauseAsync();
        ApplyFailure(result);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        StatusMessage = "正在停止...";
        var result = await _session.StopAsync();
        ApplyFailure(result);
    }

    [RelayCommand(CanExecute = nameof(CanTogglePlayPause))]
    private Task TogglePlayPauseAsync() => IsPlaying ? PauseAsync() : PlayAsync();

    [RelayCommand(CanExecute = nameof(CanSeekByShortcut))]
    private async Task SeekBackwardAsync()
    {
        var result = await _session.SeekRelativeAsync(-5_000);
        ApplyFailure(result);
    }

    [RelayCommand(CanExecute = nameof(CanSeekByShortcut))]
    private async Task SeekForwardAsync()
    {
        var result = await _session.SeekRelativeAsync(5_000);
        ApplyFailure(result);
    }

    [RelayCommand(CanExecute = nameof(CanAdjustVolume))]
    private void IncreaseVolume() => Volume = Math.Clamp(Volume + 5, 0, 100);

    [RelayCommand(CanExecute = nameof(CanAdjustVolume))]
    private void DecreaseVolume() => Volume = Math.Clamp(Volume - 5, 0, 100);

    [RelayCommand(CanExecute = nameof(CanToggleFullscreen))]
    private void ToggleFullscreen()
    {
        var revision = Interlocked.Increment(ref _fullscreenRequestRevision);
        IsFullscreenTransitioning = true;
        FullscreenPresentationRequested?.Invoke(
            this,
            new FullscreenPresentationRequestedEventArgs(revision, !IsFullscreen));
    }

    /// <summary>
    /// 由 View 在视觉树和原生表面迁移完成后提交真实结果。
    /// 请求修订号可阻止较慢的旧“进入全屏”覆盖更新的退出请求。
    /// </summary>
    public void CompleteFullscreenPresentation(
        long revision,
        bool isFullscreen,
        PlaybackFailure? failure = null)
    {
        if (_disposed || revision != Volatile.Read(ref _fullscreenRequestRevision))
        {
            return;
        }

        IsFullscreen = isFullscreen;
        IsFullscreenTransitioning = false;
        if (failure is not null)
        {
            LastFailure = failure;
            StatusMessage = failure.Message;
        }
        NotifyCommandStates();
    }

    /// <summary>视图卸载时使所有尚未完成的全屏回调立即过期。</summary>
    public void ResetFullscreenPresentation()
    {
        Interlocked.Increment(ref _fullscreenRequestRevision);
        IsFullscreen = false;
        IsFullscreenTransitioning = false;
    }

    [RelayCommand]
    private void StartSliderDrag()
    {
        IsSliderBeingDragged = true;
        _positionTimer.Stop();
    }

    [RelayCommand]
    private async Task EndSliderDragAsync()
    {
        if (!IsSliderBeingDragged)
        {
            return;
        }

        IsSliderBeingDragged = false;
        var snapshot = _session.Snapshot;
        var requested = snapshot.DurationMs <= 0
            ? 0
            : (long)Math.Round(snapshot.DurationMs * Math.Clamp(Position, 0, 100) / 100d);
        var result = await _session.SeekAsync(requested, waitForFrame: IsPaused);
        ApplyFailure(result);
        if (IsPlaying)
        {
            _positionTimer.Start();
        }
    }

    public Task<bool> LoadMediaAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken = default) =>
        SwitchMediaAsync(
            filePath,
            password,
            startPlayback: false,
            initialPositionMs: 0,
            expectedIdentity: null,
            cancellationToken);

    /// <summary>
    /// 加载并恢复历史位置但不播放；真正的加载与 Seek 由播放会话作为一个原子意图执行。
    /// </summary>
    public Task<bool> LoadMediaAtPositionAsync(
        string filePath,
        string password,
        long positionMs,
        string? expectedFileId = null,
        long expectedOriginalFileLength = 0,
        CancellationToken cancellationToken = default) =>
        SwitchMediaAsync(
            filePath,
            password,
            startPlayback: false,
            initialPositionMs: Math.Max(0, positionMs),
            string.IsNullOrWhiteSpace(expectedFileId)
                ? null
                : new PlaybackMediaIdentity(
                    expectedFileId,
                    expectedOriginalFileLength),
            cancellationToken);

    /// <summary>
    /// 原子加载媒体、恢复可信历史位置并立即播放。
    /// </summary>
    /// <remarks>
    /// 此入口只表达用户已经通过双击或 Enter 明确发出的播放意图。认证、身份复核、Seek
    /// 与 Play 由播放会话串行完成，ViewModel 不自行拼接操作，避免迟到请求启动错误媒体。
    /// </remarks>
    public Task<bool> LoadMediaAtPositionAndPlayAsync(
        string filePath,
        string password,
        long positionMs,
        string? expectedFileId = null,
        long expectedOriginalFileLength = 0,
        CancellationToken cancellationToken = default) =>
        SwitchMediaAsync(
            filePath,
            password,
            startPlayback: true,
            initialPositionMs: Math.Max(0, positionMs),
            string.IsNullOrWhiteSpace(expectedFileId)
                ? null
                : new PlaybackMediaIdentity(
                    expectedFileId,
                    expectedOriginalFileLength),
            cancellationToken);

    public Task<bool> LoadAndPlayMediaAsync(string filePath, string password) =>
        LoadAndPlayMediaAsync(filePath, password, CancellationToken.None);

    public Task<bool> LoadAndPlayMediaAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken) =>
        SwitchMediaAsync(
            filePath,
            password,
            startPlayback: true,
            initialPositionMs: 0,
            expectedIdentity: null,
            cancellationToken);

    public async Task CleanupMediaAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "正在清理当前视频...";
        var result = await _session.ReleaseAsync(cancellationToken);
        if (result.Success)
        {
            StatusMessage = "播放器就绪";
        }
        else
        {
            ApplyFailure(result);
        }
    }

    /// <summary>供宿主快捷操作和集成验收使用的毫秒 Seek 入口。</summary>
    public async Task<PlaybackOperationResult> SeekMediaAsync(
        long positionMs,
        bool waitForFrame = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _session.SeekAsync(positionMs, waitForFrame, cancellationToken);
        ApplyFailure(result);
        return result;
    }

    private async Task SetRateFromSelectionAsync(float rate)
    {
        var result = await SetPlaybackRateAsync(rate);
        if (!result.Success)
        {
            ApplySnapshot(_session.Snapshot, result.Failure);
        }
    }

    /// <summary>供组合 ViewModel、快捷入口和真实集成门禁使用的稳定倍速入口。</summary>
    public async Task<PlaybackOperationResult> SetPlaybackRateAsync(
        float rate,
        CancellationToken cancellationToken = default)
    {
        var result = await _session.SetRateAsync(rate, cancellationToken);
        if (result.Success)
        {
            _preferenceStore?.UpdatePreferences(new PlaybackPreferences(
                (int)Math.Clamp(Volume, 0, 100),
                rate));
        }
        ApplyFailure(result);
        return result;
    }

    private async Task SelectAudioTrackFromSelectionAsync(int trackId)
    {
        var result = await SelectAudioTrackAsync(trackId);
        if (!result.Success)
        {
            ApplySnapshot(_session.Snapshot, result.Failure);
        }
    }

    public async Task<PlaybackOperationResult> SelectAudioTrackAsync(
        int trackId,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsAudioTrackSelection)
        {
            var unavailable = PlaybackOperationResult.Failed(new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "当前平台不支持音轨选择。"));
            ApplyFailure(unavailable);
            return unavailable;
        }

        var result = await _session.SelectAudioTrackAsync(trackId, cancellationToken);
        ApplyFailure(result);
        return result;
    }

    private async Task SelectSubtitleTrackFromSelectionAsync(int trackId)
    {
        var result = await SelectSubtitleTrackAsync(trackId);
        if (!result.Success)
        {
            ApplySnapshot(_session.Snapshot, result.Failure);
        }
    }

    public async Task<PlaybackOperationResult> SelectSubtitleTrackAsync(
        int trackId,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsSubtitleTrackSelection)
        {
            var unavailable = PlaybackOperationResult.Failed(new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "当前平台不支持字幕轨选择。"));
            ApplyFailure(unavailable);
            return unavailable;
        }

        var result = await _session.SelectSubtitleTrackAsync(trackId, cancellationToken);
        ApplyFailure(result);
        return result;
    }

    private async Task<bool> SwitchMediaAsync(
        string filePath,
        string password,
        bool startPlayback,
        long initialPositionMs,
        PlaybackMediaIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (!IsPlaybackAvailable)
        {
            RefreshDeploymentStatus();
            if (!IsPlaybackAvailable)
            {
                var unavailableResult = PlaybackOperationResult.Failed(
                    PlaybackFailureMapper.MapDeployment(_platformStatus.Check()));
                ApplyFailure(unavailableResult);
                return false;
            }
        }

        // 自动播放必须作为一个完整的业务意图交给播放服务。若 ViewModel 自己执行
        // LoadAsync -> PlayAsync，两个调用之间可能插入 Stop 或另一条 Load，造成旧意图
        // 意外启动新媒体；组合接口用同一个代次令牌保证“认证、提交、启动”不可被拆开。
        PlaybackOperationResult result;
        if (startPlayback)
        {
            result = initialPositionMs > 0
                ? await _session.LoadAtPositionAndPlayAsync(
                    filePath,
                    password,
                    initialPositionMs,
                    expectedIdentity,
                    cancellationToken)
                : await _session.LoadAndPlayAsync(
                    filePath,
                    password,
                    cancellationToken);
        }
        else
        {
            result = initialPositionMs > 0
                ? await _session.LoadAtPositionAsync(
                    filePath,
                    password,
                    initialPositionMs,
                    expectedIdentity,
                    cancellationToken)
                : await _session.LoadAsync(filePath, password, cancellationToken);
        }
        ApplyFailure(result);
        if (result.Success)
        {
            LastFailure = null;
        }
        return result.Success;
    }

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs e)
    {
        _uiScheduler.Post(() =>
        {
            if (_disposed ||
                e.Snapshot.MediaGeneration < _session.Snapshot.MediaGeneration)
            {
                return;
            }

            ApplySnapshot(e.Snapshot, e.Failure);
        });
    }

    private void ApplySnapshot(PlaybackSnapshot snapshot, PlaybackFailure? failure)
    {
        IsVideoSurfaceReady = snapshot.SurfaceGeneration > 0;
        IsMediaTransitioning = snapshot.IsTransitioning;
        IsSeekable = snapshot.IsSeekable && !snapshot.IsTransitioning;
        Volume = snapshot.Volume;
        TotalTime = FormatTime(snapshot.DurationMs);
        if (!IsSliderBeingDragged)
        {
            Position = snapshot.DurationMs <= 0
                ? 0
                : Math.Clamp((double)snapshot.PositionMs / snapshot.DurationMs * 100d, 0, 100);
            CurrentTime = FormatTime(snapshot.PositionMs);
        }

        CurrentState = snapshot.State switch
        {
            PlaybackState.Playing => PlayerStateEnum.Playing,
            PlaybackState.Paused => PlayerStateEnum.Paused,
            PlaybackState.Ended => PlayerStateEnum.Ended,
            PlaybackState.Faulted => PlayerStateEnum.Error,
            _ => PlayerStateEnum.Stopped
        };

        _applyingSnapshot = true;
        try
        {
            SelectedRate = snapshot.Controls.Rate;
            AudioTracks = SupportsAudioTrackSelection
                ? snapshot.Controls.AudioTracks
                : Array.Empty<PlaybackTrackOption>();
            SubtitleTracks = SupportsSubtitleTrackSelection
                ? snapshot.Controls.SubtitleTracks
                : Array.Empty<PlaybackTrackOption>();
            SelectedAudioTrack = AudioTracks.FirstOrDefault(
                option => option.Id == snapshot.Controls.SelectedAudioTrackId);
            SelectedSubtitleTrack = SubtitleTracks.FirstOrDefault(
                option => option.Id == snapshot.Controls.SelectedSubtitleTrackId);
            HasAudioTracks = AudioTracks.Count > 0;
            HasSubtitleTracks = SubtitleTracks.Any(option => option.Id >= 0);
        }
        finally
        {
            _applyingSnapshot = false;
        }

        if (snapshot.State == PlaybackState.Ended &&
            snapshot.MediaGeneration > 0 &&
            snapshot.MediaGeneration != Volatile.Read(ref _lastEndedGeneration))
        {
            Volatile.Write(ref _lastEndedGeneration, snapshot.MediaGeneration);
            MediaEnded?.Invoke(
                this,
                new PlaybackMediaEndedEventArgs(snapshot.MediaGeneration));
        }

        if (failure is not null)
        {
            LastFailure = failure;
            StatusMessage = failure.Message;
        }
        else if (snapshot.Activity != PlaybackActivity.Idle)
        {
            // Activity 描述“当前正在做什么”，State 描述“播放器最终处于什么状态”。
            // 两者分离后，即使旧视频仍在 Playing，也可以明确告诉用户候选视频正在后台解析。
            StatusMessage = snapshot.Activity switch
            {
                PlaybackActivity.PreparingCandidate => "正在验证并解析新视频…",
                PlaybackActivity.WaitingForPlayer => "播放器正在完成上一操作…",
                PlaybackActivity.StoppingCurrent => "正在停止当前视频…",
                PlaybackActivity.AttachingCandidate => "正在切换媒体…",
                PlaybackActivity.StartingPlayback => "正在启动新视频…",
                PlaybackActivity.Stopping => "正在停止并释放解码资源…",
                PlaybackActivity.ReleasingOldMedia => "新视频已启动，正在后台清理旧资源…",
                _ => StatusMessage
            };
        }
        else
        {
            StatusMessage = snapshot.State switch
            {
                PlaybackState.Empty => "播放器就绪",
                PlaybackState.Ready => "媒体加载完成，可以开始播放",
                PlaybackState.Playing => "正在播放",
                PlaybackState.Paused => "已暂停",
                PlaybackState.Stopped => "已停止",
                PlaybackState.Ended => "播放完成",
                PlaybackState.Faulted => "播放错误",
                PlaybackState.Disposed => "播放器已关闭",
                _ => StatusMessage
            };
        }

        // 切换或停止过程中停止位置轮询，避免 UI 定时器继续向已排队的原生操作施压；
        // Dispatcher 本身仍保持响应，待活动回到 Idle 后会按最终状态自动恢复轮询。
        if (snapshot.State == PlaybackState.Playing && !snapshot.IsTransitioning)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
        NotifyCommandStates();
    }

    private void ApplyFailure(PlaybackOperationResult result)
    {
        if (!result.Success && result.Failure is not null &&
            result.Failure.Code != PlaybackFailureCode.Cancelled)
        {
            LastFailure = result.Failure;
            StatusMessage = result.Failure.Message;
            if (result.Failure.Code == PlaybackFailureCode.DeploymentUnavailable)
            {
                var deployment = _platformStatus.Check();
                IsPlaybackAvailable = false;
                DeploymentIssueText =
                    $"[{result.Failure.DiagnosticCode ?? "DEPLOYMENT_UNAVAILABLE"}] {result.Failure.Message}";
                DeploymentCheckedPath = deployment.RuntimeDirectory;
                DeploymentSuggestedAction = result.Failure.SuggestedAction ??
                    "请重新部署插件并重启宿主。";
            }
        }
    }

    private void OnPositionTimerTick()
    {
        if (!_disposed && !IsSliderBeingDragged)
        {
            ApplySnapshot(_session.Snapshot, null);
        }
    }

    private bool CanPlay() =>
        !_disposed &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        IsVideoSurfaceReady &&
        CurrentState != PlayerStateEnum.Playing &&
        _session.Snapshot.HasMedia;

    private bool CanPause() =>
        !_disposed &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        CurrentState == PlayerStateEnum.Playing;

    private bool CanStop() =>
        !_disposed &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        CurrentState is PlayerStateEnum.Playing or PlayerStateEnum.Paused;

    private bool CanTogglePlayPause() => IsPlaying ? CanPause() : CanPlay();

    private bool CanSeekByShortcut() =>
        !_disposed &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        IsSeekable &&
        _session.Snapshot.HasMedia;

    private bool CanAdjustVolume() =>
        !_disposed && IsPlaybackAvailable && !IsMediaTransitioning;

    private bool CanChangeAdvancedControl() =>
        !_disposed &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        _session.Snapshot.HasMedia;

    private bool CanToggleFullscreen() =>
        !_disposed &&
        SupportsEmbeddedFullscreen &&
        IsPlaybackAvailable &&
        !IsMediaTransitioning &&
        !IsFullscreenTransitioning &&
        IsVideoSurfaceReady &&
        _session.Snapshot.HasMedia;

    private void NotifyCommandStates()
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        SeekBackwardCommand.NotifyCanExecuteChanged();
        SeekForwardCommand.NotifyCanExecuteChanged();
        IncreaseVolumeCommand.NotifyCanExecuteChanged();
        DecreaseVolumeCommand.NotifyCanExecuteChanged();
        ToggleFullscreenCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDeploymentStatus()
    {
        if (_disposed)
        {
            return;
        }

        var capabilities = _platformStatus.Capabilities;
        var result = _platformStatus.Check();
        IsPlaybackAvailable =
            capabilities.IsSupported &&
            capabilities.SupportsNativeVideoOutput &&
            result.IsReady;
        if (IsPlaybackAvailable)
        {
            try
            {
                // G3 的 100 次真实 HWND 生命周期证明：MediaPlayer 必须在 View 首次绑定前
                // 准备好，不能等到线程池中的媒体解析阶段再动态替换原生输出。
                // 因此这里采用“自检门控的页面启动初始化”：坏部署仍可打开诊断页，
                // 完整部署则在页面绑定前恢复 G3 已验证的原生对象构造顺序。
                _backendInitializer.Initialize();
                DeploymentIssueText = string.Empty;
                DeploymentCheckedPath = string.Empty;
                DeploymentSuggestedAction = string.Empty;
                if (_session.Snapshot.State == PlaybackState.Empty)
                {
                    StatusMessage = "播放器部署自检通过";
                }
            }
            catch (PlaybackDeploymentException ex)
            {
                var failure = PlaybackFailureMapper.MapDeployment(ex.Result);
                IsPlaybackAvailable = false;
                DeploymentIssueText = $"[{failure.DiagnosticCode}] {failure.Message}";
                DeploymentCheckedPath = ex.Result.RuntimeDirectory;
                DeploymentSuggestedAction = failure.SuggestedAction ?? "请重新部署插件并重启宿主。";
                StatusMessage = failure.Message;
            }
            return;
        }

        // 探针刻意聚合全部问题，UI 也不能退化成只显示第一项，否则用户修复一个文件后
        // 还要反复重检才能发现下一个缺失项。路径和建议去重后逐行展示，既保留定位信息，
        // 又避免多个 codec 问题重复刷出同一个“重新部署完整目录”提示。
        DeploymentIssueText = result.Issues.Count == 0
            ? $"[UnsupportedPlatform] {capabilities.UnsupportedReason ?? "当前平台不支持原生视频输出。"}"
            : string.Join(
                Environment.NewLine,
                result.Issues.Select(issue => $"[{issue.Code}] {issue.Summary}"));
        DeploymentCheckedPath = string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => issue.CheckedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        DeploymentSuggestedAction = string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => issue.SuggestedAction)
                .Distinct(StringComparer.Ordinal));
        StatusMessage = result.Issues.FirstOrDefault()?.Summary
                        ?? capabilities.UnsupportedReason
                        ?? "当前平台不支持原生视频输出。";
    }

    private static string FormatTime(long timeMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, timeMs)).ToString(@"hh\:mm\:ss");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _positionTimer.Dispose();
        _session.Changed -= OnPlaybackChanged;
        MediaEnded = null;
        FullscreenPresentationRequested = null;
        GC.SuppressFinalize(this);
    }
}
