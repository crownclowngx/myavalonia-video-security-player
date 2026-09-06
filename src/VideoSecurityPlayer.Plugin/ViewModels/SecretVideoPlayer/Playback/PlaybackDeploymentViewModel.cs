using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>拥有部署检查状态，只依赖平台探针和后端初始化端口，不访问播放协调器。</summary>
/// <remarks>
/// 检查与初始化必须同步完成：真实 HWND 门禁要求在 View 首次绑定之前准备好播放器。
/// 本组件不持有、释放注入的后端；文档作用域仍负责原生资源生命周期。
/// Checked 只表示一次检查已完成，由上层决定是否将部署提示展示为当前播放状态。
/// </remarks>
public sealed partial class PlaybackDeploymentViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackPlatformStatus _platform;
    private readonly IPlaybackBackendInitializer _initializer;
    private bool _disposed;

    [ObservableProperty] private bool _isPlaybackAvailable;
    [ObservableProperty] private string _deploymentIssueText = string.Empty;
    [ObservableProperty] private string _deploymentCheckedPath = string.Empty;
    [ObservableProperty] private string _deploymentSuggestedAction = string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;
    public event EventHandler? Checked;

    public PlaybackDeploymentViewModel(
        IPlaybackPlatformStatus platform, IPlaybackBackendInitializer initializer)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    }

    [RelayCommand]
    private void RetryDeploymentCheck() => Check();

    public void Check()
    {
        if (_disposed) return;
        var capabilities = _platform.Capabilities;
        var result = _platform.Check();
        IsPlaybackAvailable = capabilities.IsSupported &&
                              capabilities.SupportsNativeVideoOutput && result.IsReady;
        if (IsPlaybackAvailable)
        {
            try
            {
                _initializer.Initialize();
                DeploymentIssueText = string.Empty;
                DeploymentCheckedPath = string.Empty;
                DeploymentSuggestedAction = string.Empty;
                StatusMessage = "播放器部署自检通过";
            }
            catch (PlaybackDeploymentException ex)
            {
                ApplyFailure(PlaybackFailureMapper.MapDeployment(ex.Result), ex.Result);
            }
        }
        else
        {
            // 展示全部问题。路径按 Windows 路径语义去重，建议按原文去重，避免反复修复才发现遗漏项。
            DeploymentIssueText = result.Issues.Count == 0
                ? $"[UnsupportedPlatform] {capabilities.UnsupportedReason ?? "当前平台不支持原生视频输出。"}"
                : string.Join(Environment.NewLine, result.Issues.Select(x => $"[{x.Code}] {x.Summary}"));
            DeploymentCheckedPath = string.Join(Environment.NewLine,
                result.Issues.Select(x => x.CheckedPath).Distinct(StringComparer.OrdinalIgnoreCase));
            DeploymentSuggestedAction = string.Join(Environment.NewLine,
                result.Issues.Select(x => x.SuggestedAction).Distinct(StringComparer.Ordinal));
            StatusMessage = result.Issues.FirstOrDefault()?.Summary ??
                            capabilities.UnsupportedReason ?? "当前平台不支持原生视频输出。";
        }
        Checked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加载阶段再次发现部署故障时，更新同一份部署状态，不在协调器缓存副本。</summary>
    internal void ReportFailure(PlaybackFailure failure)
    {
        if (!_disposed) ApplyFailure(failure, _platform.Check());
    }

    private void ApplyFailure(PlaybackFailure failure, DeploymentCheckResult result)
    {
        IsPlaybackAvailable = false;
        DeploymentIssueText = $"[{failure.DiagnosticCode ?? "DEPLOYMENT_UNAVAILABLE"}] {failure.Message}";
        DeploymentCheckedPath = result.RuntimeDirectory;
        DeploymentSuggestedAction = failure.SuggestedAction ?? "请重新部署插件并重启宿主。";
        StatusMessage = failure.Message;
    }

    public void Dispose()
    {
        _disposed = true;
        Checked = null;
    }
}
