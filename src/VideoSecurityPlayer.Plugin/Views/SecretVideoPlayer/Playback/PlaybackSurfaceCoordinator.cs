using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>
/// 协调一个原生表面与当前 Document 的语义化表面会话。
/// </summary>
/// <remarks>
/// 本类型只拥有事件订阅和恢复取消源，不保存媒体、播放位置或全屏状态。它把严格的
/// “旧会话记录分离、清空输出、绑定新输出、等待旧 Stop 后恢复当前表面”顺序从 View 代码中集中出来。
/// </remarks>
internal sealed class PlaybackSurfaceCoordinator : IDisposable
{
    private readonly IPlaybackVideoSurface _surface;
    private IPlaybackSurfaceSession? _session;
    private CancellationTokenSource? _attachmentCancellation;
    private int _disposeState;

    public PlaybackSurfaceCoordinator(IPlaybackVideoSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _surface.SurfaceReady += OnSurfaceReady;
        _surface.SurfaceLosing += OnSurfaceLosing;
    }

    public event EventHandler<VideoSurfaceAttachmentCompletedEventArgs>?
        AttachmentCompleted;

    public VideoSurfaceIdentity? CurrentSurface => _surface.CurrentSurface;

    public void Bind(IPlaybackSurfaceSession? session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        CancelAttachment();
        var current = _surface.CurrentSurface;
        if (_session is not null && current is not null)
        {
            _session.DetachSurface(current.Value);
        }

        // 清除旧输出必须发生在旧会话保存恢复快照、请求输入停止并排入 Stop 之后。
        // VideoView 会在这里把 HWND 清零；可能阻塞的 Stop 留在后台串行队列中，
        // 因而既不会占住 UI，也不会在窗口销毁后继续引用旧 HWND。
        if (_surface.Output is not null)
        {
            _surface.Output = null;
        }
        _session = session;
        _surface.Output = session?.VideoOutput;
        if (session is not null && current is not null)
        {
            StartAttachment(session, current.Value);
        }
    }

    private void OnSurfaceLosing(object? sender, VideoSurfaceChangedEventArgs e)
    {
        CancelAttachment();
        _session?.DetachSurface(e.Surface);
    }

    private void OnSurfaceReady(object? sender, VideoSurfaceChangedEventArgs e)
    {
        if (_session is not null)
        {
            StartAttachment(_session, e.Surface);
        }
    }

    private void StartAttachment(
        IPlaybackSurfaceSession session,
        VideoSurfaceIdentity surface)
    {
        CancelAttachment();
        var cancellation = new CancellationTokenSource();
        _attachmentCancellation = cancellation;
        _ = AttachAsync(session, surface, cancellation);
    }

    private async Task AttachAsync(
        IPlaybackSurfaceSession session,
        VideoSurfaceIdentity surface,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await session
                .AttachAndRestoreSurfaceAsync(surface, cancellation.Token)
                .ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested &&
                ReferenceEquals(_session, session) &&
                _surface.CurrentSurface == surface)
            {
                AttachmentCompleted?.Invoke(
                    this,
                    new VideoSurfaceAttachmentCompletedEventArgs(surface, result));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_attachmentCancellation, cancellation))
            {
                _attachmentCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelAttachment()
    {
        var cancellation = _attachmentCancellation;
        _attachmentCancellation = null;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelAttachment();
        var current = _surface.CurrentSurface;
        if (_session is not null && current is not null)
        {
            _session.DetachSurface(current.Value);
        }

        if (_surface.Output is not null)
        {
            _surface.Output = null;
        }
        _surface.SurfaceReady -= OnSurfaceReady;
        _surface.SurfaceLosing -= OnSurfaceLosing;
        _session = null;
        AttachmentCompleted = null;
    }
}
