using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>
/// 在普通占位区和宿主全屏覆盖层之间迁移唯一播放器视觉树。
/// </summary>
/// <remarks>
/// 本类型只处理 Avalonia/TopLevel 呈现，不拥有播放状态。迁移被串行化，并等待
/// NativeControlHost 完成旧 HWND 销毁后再连接新表面，避免一个 MediaPlayer 同时绑定两个句柄。
/// </remarks>
internal sealed class FullscreenPlaybackPresenter
{
    private readonly VideoPlayerControl _owner;
    private readonly ContentControl _normalPlaceholder;
    private readonly Control _playerShell;
    private readonly PlaybackSurfaceCoordinator _surfaceCoordinator;
    private readonly Func<VideoPlayerControlViewModel?> _viewModel;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private IDisposable? _fullscreenLease;
    private TopLevel? _fullscreenTopLevel;
    private bool _forcingVisualReset;

    public FullscreenPlaybackPresenter(
        VideoPlayerControl owner,
        ContentControl normalPlaceholder,
        Control playerShell,
        PlaybackSurfaceCoordinator surfaceCoordinator,
        Func<VideoPlayerControlViewModel?> viewModel)
    {
        _owner = owner;
        _normalPlaceholder = normalPlaceholder;
        _playerShell = playerShell;
        _surfaceCoordinator = surfaceCoordinator;
        _viewModel = viewModel;
    }

    public async Task<PlaybackFailure?> ApplyAsync(bool enterFullscreen)
    {
        await _transitionGate.WaitAsync();
        try
        {
            return enterFullscreen
                ? await EnterAsync()
                : await ExitAsync();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<PlaybackFailure?> EnterAsync()
    {
        if (_fullscreenLease is not null)
            return null;

        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is not IWindowContentFullscreenHost fullscreenHost)
        {
            return new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "当前宿主窗口不支持内容区全屏。");
        }

        var previousGeneration = _surfaceCoordinator.CurrentSurface?.Generation ?? 0;
        var attachment = WaitForNewSurfaceAttachmentAsync(previousGeneration);
        _normalPlaceholder.Content = null;
        await WaitForNativeSurfaceReleaseAsync();

        IDisposable? lease;
        try
        {
            lease = fullscreenHost.TryPresent(_playerShell);
        }
        catch
        {
            // Host 挂载内容失败时不能把唯一 PlayerShell 留在无父级状态。先恢复普通占位区并等待
            // 新 HWND 表面重新连接，再向 ViewModel 返回不包含异常正文的稳定失败。
            _normalPlaceholder.Content = _playerShell;
            var restoreFailure = await attachment;
            return restoreFailure ?? new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "宿主无法挂载全屏播放器。");
        }

        if (lease is null)
        {
            _normalPlaceholder.Content = _playerShell;
            var restoreFailure = await attachment;
            return restoreFailure ?? new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "当前窗口已有播放器处于全屏状态。");
        }

        _fullscreenLease = lease;
        _fullscreenTopLevel = topLevel;
        _fullscreenTopLevel.AddHandler(
            InputElement.KeyDownEvent,
            OnFullscreenTopLevelKeyDown,
            RoutingStrategies.Tunnel);

        var failure = await attachment;
        if (failure is not null)
        {
            await RollBackFailedEntryAsync();
            return failure;
        }

        _owner.Focus();
        return null;
    }

    private async Task<PlaybackFailure?> ExitAsync()
    {
        if (_fullscreenLease is null)
            return null;

        var previousGeneration = _surfaceCoordinator.CurrentSurface?.Generation ?? 0;
        var attachment = WaitForNewSurfaceAttachmentAsync(previousGeneration);
        var lease = _fullscreenLease;
        _fullscreenLease = null;
        lease.Dispose();
        await WaitForNativeSurfaceReleaseAsync();
        _normalPlaceholder.Content = _playerShell;
        RemoveTopLevelHandler();
        var failure = await attachment;
        _owner.Focus();
        return failure;
    }

    private async Task RollBackFailedEntryAsync()
    {
        var previousGeneration = _surfaceCoordinator.CurrentSurface?.Generation ?? 0;
        var attachment = WaitForNewSurfaceAttachmentAsync(previousGeneration);
        var lease = _fullscreenLease;
        _fullscreenLease = null;
        lease?.Dispose();
        RemoveTopLevelHandler();
        await WaitForNativeSurfaceReleaseAsync();
        _normalPlaceholder.Content = _playerShell;
        _ = await attachment;
    }

    private async Task WaitForNativeSurfaceReleaseAsync()
    {
        await _owner.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Background);
    }

    private async Task<PlaybackFailure?> WaitForNewSurfaceAttachmentAsync(long previousGeneration)
    {
        var viewModel = _viewModel();
        if (viewModel is null)
        {
            return new PlaybackFailure(
                PlaybackFailureCode.ControlUnavailable,
                "播放器视图已不可用。");
        }

        var completion = new TaskCompletionSource<PlaybackFailure?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VideoSurfaceAttachmentCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.Surface.Generation <= previousGeneration)
                return;
            completion.TrySetResult(args.Result.Success
                ? null
                : args.Result.Failure ?? new PlaybackFailure(
                    PlaybackFailureCode.SurfaceRestoreFailed,
                    "视频表面恢复失败。"));
        };

        _surfaceCoordinator.AttachmentCompleted += handler;
        try
        {
            var completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(TimeSpan.FromSeconds(5)));
            return completed == completion.Task
                ? await completion.Task
                : new PlaybackFailure(
                    PlaybackFailureCode.SurfaceRestoreFailed,
                    "视频表面未能在允许时间内完成恢复。");
        }
        finally
        {
            _surfaceCoordinator.AttachmentCompleted -= handler;
        }
    }

    public void ForceReset()
    {
        if (_forcingVisualReset)
            return;

        _forcingVisualReset = true;
        try
        {
            RemoveTopLevelHandler();
            var lease = _fullscreenLease;
            _fullscreenLease = null;
            lease?.Dispose();

            if (_normalPlaceholder.Content is null && _playerShell.Parent is null)
                _normalPlaceholder.Content = _playerShell;
        }
        finally
        {
            _forcingVisualReset = false;
        }
    }

    private void RemoveTopLevelHandler()
    {
        _fullscreenTopLevel?.RemoveHandler(
            InputElement.KeyDownEvent,
            OnFullscreenTopLevelKeyDown);
        _fullscreenTopLevel = null;
    }

    private void OnFullscreenTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = _viewModel();
        if (e.Key != Key.Escape || viewModel?.IsFullscreen != true)
            return;
        if (viewModel.ToggleFullscreenCommand.CanExecute(null))
            viewModel.ToggleFullscreenCommand.Execute(null);
        e.Handled = true;
    }
}
