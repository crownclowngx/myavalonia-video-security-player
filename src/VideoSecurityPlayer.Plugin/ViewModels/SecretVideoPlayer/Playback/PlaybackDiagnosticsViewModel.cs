using CommunityToolkit.Mvvm.ComponentModel;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>拥有诊断导出的防重入、取消与状态；不接触保存路径、密码或播放资源。</summary>
/// <remarks>
/// 导出器由装配边界提供，每次导出显式接收失败快照，避免依赖整个协调器。
/// 生命周期锁只保护导出令牌所有权，不跨 await；关闭负责取消，异步操作负责最终释放令牌源。
/// 属性更新延续调用方 UI 上下文；关闭后的完成和保存回调不再发送属性通知。
/// </remarks>
public sealed partial class PlaybackDiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly object _sync = new();
    private IPlaybackDiagnosticExporter? _exporter;
    private CancellationTokenSource? _activeExport;
    private bool _disposed;
    private bool _isExportingDiagnostics;
    [ObservableProperty] private string _diagnosticsStatusMessage = string.Empty;
    public bool CanExportDiagnostics => !_disposed && _exporter is not null && !IsExportingDiagnostics;

    /// <summary>兼容属性仍可设置；关闭后的清理只更新字段，避免向已卸载视图发送通知。</summary>
    public bool IsExportingDiagnostics
    {
        get => _isExportingDiagnostics;
        set
        {
            if (_disposed) _isExportingDiagnostics = value;
            else if (SetProperty(ref _isExportingDiagnostics, value))
                OnPropertyChanged(nameof(CanExportDiagnostics));
        }
    }

    internal void Configure(IPlaybackDiagnosticExporter exporter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        OnPropertyChanged(nameof(CanExportDiagnostics));
    }

    public async Task<ReadOnlyMemory<byte>> CreateJsonAsync(
        PlaybackFailure? failure, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource operation;
        IPlaybackDiagnosticExporter exporter;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            exporter = _exporter ?? throw new InvalidOperationException("诊断导出器尚未配置。");
            if (_activeExport is not null) throw new InvalidOperationException("诊断正在导出。");
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeExport = operation;
            IsExportingDiagnostics = true;
            DiagnosticsStatusMessage = string.Empty;
        }
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var json = await exporter.CreateJsonAsync(failure, operation.Token).ConfigureAwait(true);
            // 导出器即便忽略取消，也不能让已关闭文档继续进入保存流程。
            operation.Token.ThrowIfCancellationRequested();
            return json;
        }
        finally
        {
            lock (_sync)
            {
                _activeExport = null;
                operation.Dispose();
                IsExportingDiagnostics = false;
            }
        }
    }

    internal void ReportSucceeded()
    {
        if (!_disposed) DiagnosticsStatusMessage = "脱敏诊断已导出";
    }

    internal void ReportFailed()
    {
        if (!_disposed) DiagnosticsStatusMessage = "无法写入所选位置";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _activeExport?.Cancel();
        }
    }
}
