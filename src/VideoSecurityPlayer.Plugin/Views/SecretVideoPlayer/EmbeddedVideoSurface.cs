using Avalonia.Platform;
using LibVLCSharp.Avalonia;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

/// <summary>
/// 播放器 View 使用的原生视频表面端口。
/// </summary>
/// <remarks>
/// 通用 View 只观察表面代次和生命周期事件，不读取 HWND，也不依赖 LibVLC 类型。
/// </remarks>
public interface IPlaybackVideoSurface
{
    event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceReady;
    event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceLosing;

    VideoSurfaceIdentity? CurrentSurface { get; }

    IPlaybackVideoOutput? Output { get; set; }
}

/// <summary>原生视频表面生命周期事件。</summary>
public sealed class VideoSurfaceChangedEventArgs(VideoSurfaceIdentity surface) : EventArgs
{
    public VideoSurfaceIdentity Surface { get; } = surface;
}

/// <summary>
/// Windows HWND 与 LibVLC MediaPlayer 之间的唯一生产适配器。
/// </summary>
/// <remarks>
/// HWND 只允许存在于本类型内部。业务会话只接收单调递增的表面代次，因此未来增加其他
/// 平台表面时，不需要修改媒体切换、恢复快照或 ViewModel 命令逻辑。
/// </remarks>
public sealed class EmbeddedVideoSurface : VideoView, IPlaybackVideoSurface
{
    private static long _createdSurfaceCount;
    private static long _destroyedSurfaceCount;
    private static long _activeSurfaceCount;
    private static string _lastHandleDescriptor = "unavailable";
    private static int _lastHandleWasNonZero;

    private long _surfaceGeneration;
    private nint _nativeHandle;
    private IPlaybackVideoOutput? _output;

    public event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceReady;
    public event EventHandler<VideoSurfaceChangedEventArgs>? SurfaceLosing;

    public VideoSurfaceIdentity? CurrentSurface { get; private set; }

    public IPlaybackVideoOutput? Output
    {
        get => _output;
        set
        {
            if (ReferenceEquals(_output, value))
            {
                return;
            }

            if (_output is not null)
            {
                _output.OutputChanged -= OnOutputChanged;
            }

            _output = value;
            if (_output is not null)
            {
                _output.OutputChanged += OnOutputChanged;
            }

            ApplyOutput();
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var control = base.CreateNativeControlCore(parent);

        // 只有 HWND 才属于当前 Windows x64 生产实现。不能仅凭“句柄非零”把其他平台
        // 的 XID/NSView 当成 HWND 写给 LibVLC。
        if (!string.Equals(
                control.HandleDescriptor,
                "HWND",
                StringComparison.OrdinalIgnoreCase) ||
            control.Handle == nint.Zero)
        {
            return control;
        }

        _nativeHandle = control.Handle;
        Volatile.Write(
            ref _lastHandleDescriptor,
            control.HandleDescriptor ?? "unknown");
        Volatile.Write(ref _lastHandleWasNonZero, 1);
        Interlocked.Increment(ref _createdSurfaceCount);
        Interlocked.Increment(ref _activeSurfaceCount);
        ApplyOutput();
        var identity = new VideoSurfaceIdentity(
            Interlocked.Increment(ref _surfaceGeneration));
        CurrentSurface = identity;
        SurfaceReady?.Invoke(this, new VideoSurfaceChangedEventArgs(identity));
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var identity = CurrentSurface;
        if (identity is not null)
        {
            // 必须同步通知订阅方保存恢复快照并请求输入停止；通知返回后基类 VideoView 会先把
            // MediaPlayer.Hwnd 清零，再销毁真实窗口。可能阻塞的 Stop 已由会话排入后台串行队列，
            // 新表面恢复会等待它完成，因此这里既不阻塞 UI，也不留下失效 HWND。
            SurfaceLosing?.Invoke(this, new VideoSurfaceChangedEventArgs(identity.Value));
            Interlocked.Increment(ref _destroyedSurfaceCount);
            Interlocked.Decrement(ref _activeSurfaceCount);
        }

        base.DestroyNativeControlCore(control);
        _nativeHandle = nint.Zero;
        CurrentSurface = null;
    }

    /// <summary>
    /// 只公开验收需要的描述符与计数，不把真实 HWND 句柄扩散到播放器业务层。
    /// </summary>
    public static EmbeddedVideoSurfaceDiagnostics CaptureDiagnostics() =>
        new(
            Volatile.Read(ref _lastHandleDescriptor),
            Volatile.Read(ref _lastHandleWasNonZero) != 0,
            Interlocked.Read(ref _createdSurfaceCount),
            Interlocked.Read(ref _destroyedSurfaceCount),
            Interlocked.Read(ref _activeSurfaceCount));

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _output))
        {
            return;
        }

        // VideoView.Detach 会访问旧 MediaPlayer，所以输出切换必须在 UI 线程同步完成，
        // 不能 Post 后立即允许后台线程释放原生播放器。
        if (Dispatcher.CheckAccess())
        {
            ApplyOutput();
            return;
        }

        Dispatcher.InvokeAsync(ApplyOutput).GetAwaiter().GetResult();
    }

    private void ApplyOutput()
    {
        var player = (_output as ILibVlcVideoOutputAccessor)?.NativePlayer;
        MediaPlayer = player;

        // LibVLCSharp.Avalonia 可能在属性设置时尚未拿到 NativeControlHost 的 HWND。
        // 句柄创建后显式再写一次，防止 LibVLC 回退创建独立视频窗口。
        if (player is not null && _nativeHandle != nint.Zero)
        {
            player.Hwnd = _nativeHandle;
        }
    }
}

public readonly record struct EmbeddedVideoSurfaceDiagnostics(
    string HandleDescriptor,
    bool LastHandleWasNonZero,
    long CreatedCount,
    long DestroyedCount,
    long ActiveCount);
