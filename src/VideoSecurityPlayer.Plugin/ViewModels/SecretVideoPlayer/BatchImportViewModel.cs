using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 两类批次复用的导入协调器。发现文件与接受队列分别由窄接口及回调承担，
/// 关闭、取消或页面变忙后不再交付结果；发现过程中不修改已有队列和输出配置。
/// </summary>
public partial class BatchImportViewModel : ObservableObject, IDisposable
{
    private readonly IVideoInputDiscovery _discovery;
    private readonly IDocumentLifetime _lifetime;
    private readonly Func<bool> _canAccept;
    private readonly Func<IReadOnlyList<VideoInputFile>, Task> _accept;
    private readonly VideoInputKind _kind;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    [ObservableProperty] private bool _isCollecting;
    [ObservableProperty] private bool _includeSubdirectories;
    [ObservableProperty] private string _summary = "支持拖入文件或目录";

    public BatchImportViewModel(IVideoInputDiscovery discovery, IDocumentLifetime lifetime, VideoInputKind kind,
        Func<bool> canAccept, Func<IReadOnlyList<VideoInputFile>, Task> accept)
    { _discovery = discovery; _lifetime = lifetime; _kind = kind; _canAccept = canAccept; _accept = accept; }

    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (_disposed || _lifetime.IsClosing || IsCollecting || !_canAccept() || paths.Count == 0) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        _cancellation = cancellation;
        IsCollecting = true;
        Summary = "正在查找文件，可取消导入";
        try
        {
            var result = await _discovery.DiscoverAsync(paths, _kind, IncludeSubdirectories, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || _lifetime.IsClosing) return;
            // UI 上下文内同步解除发现状态，再让队列接手；解密队列会立即进入自己的检查状态。
            IsCollecting = false;
            if (!_canAccept()) return;
            await _accept(result.Files);
            if (!_disposed && !_lifetime.IsClosing)
                Summary = $"发现 {result.Files.Count} 个文件，跳过不支持或重复 {result.SkippedCount} 个" +
                    (result.Issues.Count == 0 ? "" : $"；{result.Issues.Count} 项问题：" + string.Join("；", result.Issues.Take(3)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed && !_lifetime.IsClosing) Summary = "导入已取消，原队列保持不变"; }
        catch
        { if (!_disposed && !_lifetime.IsClosing) Summary = "导入失败，请检查输入文件或目录后重试"; }
        finally
        {
            _cancellation = null;
            if (!_disposed && !_lifetime.IsClosing) IsCollecting = false;
        }
    }

    [RelayCommand] private void CancelImport() => _cancellation?.Cancel();
    public void Dispose() { _disposed = true; _cancellation?.Cancel(); }
}
