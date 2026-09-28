using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>一个设置分组的保存反馈与重试，不拥有配置数据，也不决定持久化方式。</summary>
/// <remarks>每个分组独立合并连续编辑；旧任务的完成通知必须匹配修订，不能覆盖新草稿的状态。</remarks>
public sealed partial class SettingsSaveFeedback : ObservableObject, IDisposable
{
    private readonly CapturedUiScheduler _ui = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Func<CancellationToken, Task<SettingsSaveResult>>? _retry;
    private CancellationTokenSource? _pending;
    private long _revision;
    private bool _disposed;
    [ObservableProperty] private string _message = "已保存";
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _hasFailure;
    public Task PendingTask { get; private set; } = Task.CompletedTask;

    public Task RunAsync(Func<CancellationToken, Task<SettingsSaveResult>> operation, bool debounce = true)
    {
        if (_disposed) return Task.CompletedTask;
        _retry = operation;
        var revision = Interlocked.Increment(ref _revision);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_sync)
        {
            _pending?.Cancel();
            _pending = cancellation;
        }
        IsSaving = true; HasFailure = false; Message = "保存中…";
        return PendingTask = ExecuteAsync(operation, revision, cancellation, debounce);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task<SettingsSaveResult>> operation, long revision,
        CancellationTokenSource cancellation, bool debounce)
    {
        try
        {
            if (debounce) await Task.Delay(150, cancellation.Token).ConfigureAwait(false);
            var result = await operation(cancellation.Token).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (_disposed || revision != Interlocked.Read(ref _revision)) return;
                IsSaving = false; HasFailure = !result.Success; Message = result.Message;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            _ui.Post(() =>
            {
                if (_disposed || revision != Interlocked.Read(ref _revision)) return;
                IsSaving = false; HasFailure = true; Message = "保存失败，请重试";
            });
        }
        finally
        {
            // 测试及无同步上下文的宿主也可能在工作线程完成；锁住取消源的整个所有权边界。
            lock (_sync)
            {
                if (ReferenceEquals(_pending, cancellation)) _pending = null;
                cancellation.Dispose();
            }
        }
    }

    [RelayCommand]
    private Task RetryAsync() => _retry is null ? Task.CompletedTask : RunAsync(_retry, debounce: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Hide/换 View 不释放此单例。只有宿主退出才走这里，此时等最后一次已接受的编辑
        // 越过短暂合并窗口并提交，避免用户改值后立即退出而丢失配置。ExecuteAsync 及端口
        // 不等待 UI 上下文，反馈通过 Post 发布，因此退出时不会与 UI 线程互相等待。
        PendingTask.GetAwaiter().GetResult();
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
