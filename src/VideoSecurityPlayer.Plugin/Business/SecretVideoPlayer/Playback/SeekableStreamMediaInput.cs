using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 将 .NET 可定位流桥接到 LibVLC 的 MediaInput 回调接口。
/// </summary>
/// <remarks>
/// 适配器不会复制完整视频，只维护一个最大 1 MiB 的复用缓冲区。所有 Open、Read、Seek、Close 回调通过同一把锁串行化，
/// 因为 LibVLC 可能从不同原生线程发起回调，而底层 <see cref="SeekableEncryptedVideoStream"/> 的 Position 是共享状态。
/// 适配器拥有传入流的生命周期，释放 MediaInput 时会同时释放容器文件句柄和解密缓存。
/// </remarks>
public sealed class SeekableStreamMediaInput : MediaInput
{
    private const int MaximumReadSize = 1024 * 1024;
    private readonly Stream _stream;
    private readonly object _syncRoot = new();
    private byte[] _buffer = Array.Empty<byte>();
    private PlaybackFailure? _lastFailure;
    private int _stopRequested;
    private bool _disposed;

    internal event Action<PlaybackFailure>? Failed;

    public SeekableStreamMediaInput(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("媒体流必须可读且可随机定位。", nameof(stream));
        }

        _stream = stream;
        if (stream is SeekableEncryptedVideoStream encrypted)
        {
            MediaIdentity = new PlaybackMediaIdentity(
                encrypted.FileId,
                encrypted.OriginalFileLength);
            DiagnosticSummary = encrypted.DiagnosticSummary;
        }
        CanSeek = true;
    }

    internal PlaybackMediaIdentity? MediaIdentity { get; }
    internal Secvid03DiagnosticSummary? DiagnosticSummary { get; }

    /// <summary>
    /// 一次性取得原生回调边界内的首个类型化失败。
    /// 首次失败优先可以避免后续 Close/Dispose 异常掩盖真正的认证或读取根因。
    /// </summary>
    public bool TryTakeLastFailure(out PlaybackFailure? failure)
    {
        failure = Interlocked.Exchange(ref _lastFailure, null);
        return failure is not null;
    }

    /// <summary>
    /// 请求当前原生读取尽快结束。该方法不会等待读取锁，因此可以在 LibVLC Stop 前安全调用。
    /// </summary>
    public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);

    /// <summary>
    /// 为重新播放同一个 MediaInput 清除停止标志。
    /// </summary>
    public void PrepareForPlayback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _stopRequested, 0);
        Interlocked.Exchange(ref _lastFailure, null);
    }

    private bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

    /// <summary>
    /// 向 LibVLC 报告虚拟原视频长度并把流位置复位到开头。
    /// </summary>
    public override bool Open(out ulong size)
    {
        lock (_syncRoot)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsStopRequested)
                {
                    size = 0;
                    return false;
                }

                size = checked((ulong)_stream.Length);
                _stream.Position = 0;
                Interlocked.Exchange(ref _lastFailure, null);
                return true;
            }
            catch (Exception ex)
            {
                size = 0;
                RecordFailure(ex);
                return false;
            }
        }
    }

    /// <summary>
    /// 从已经认证的虚拟视频流读取数据并复制到 LibVLC 提供的原生缓冲区。
    /// </summary>
    public override unsafe int Read(IntPtr buffer, uint length)
    {
        if (IsStopRequested)
        {
            return -1;
        }

        lock (_syncRoot)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsStopRequested)
                {
                    return -1;
                }

                if (length == 0)
                {
                    return 0;
                }

                // LibVLC 允许请求任意 uint 长度；主动限制单次读取，防止异常请求导致超大托管数组分配。
                // MediaInput 协议允许返回少于请求量的字节，LibVLC 会按需继续读取。
                var requested = (int)Math.Min(length, MaximumReadSize);
                if (_buffer.Length < requested)
                {
                    _buffer = new byte[requested];
                }

                var bytesRead = _stream.Read(_buffer, 0, requested);
                if (IsStopRequested)
                {
                    return -1;
                }

                if (bytesRead == 0)
                {
                    return 0;
                }

                fixed (byte* source = _buffer)
                {
                    // 只把已经通过 AES-GCM 验证的字节复制到原生缓冲区。
                    Buffer.MemoryCopy(source, buffer.ToPointer(), length, bytesRead);
                }

                return bytesRead;
            }
            catch (Exception ex)
            {
                // LibVLC 回调边界不能抛出托管异常；保存原始异常，并用 -1 通知原生读取失败。
                RecordFailure(ex);
                return -1;
            }
        }
    }

    /// <summary>
    /// 把 LibVLC 的无符号绝对偏移转换为 .NET Stream 的有符号位置。
    /// </summary>
    public override bool Seek(ulong offset)
    {
        if (IsStopRequested)
        {
            return false;
        }

        lock (_syncRoot)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsStopRequested)
                {
                    return false;
                }

                if (offset > (ulong)_stream.Length || offset > long.MaxValue)
                {
                    return false;
                }

                _stream.Position = (long)offset;
                return true;
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                return false;
            }
        }
    }

    /// <summary>
    /// 响应 LibVLC 的媒体关闭回调。这里只复位位置，最终资源所有权仍由 Dispose 统一处理。
    /// </summary>
    public override void Close()
    {
        lock (_syncRoot)
        {
            if (!_disposed && !IsStopRequested)
            {
                _stream.Position = 0;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _stream.Dispose();
                // 复用缓冲区包含已解密视频内容，释放时主动清零。
                Array.Clear(_buffer);
                _buffer = Array.Empty<byte>();
            }
        }

        base.Dispose(disposing);
    }

    private void RecordFailure(Exception exception)
    {
        if (IsStopRequested)
        {
            return;
        }

        var failure = PlaybackFailureMapper.MapMediaInput(exception);
        if (Interlocked.CompareExchange(ref _lastFailure, failure, null) is null)
        {
            // Never call MediaPlayer.Stop from inside a native Read callback.
            // Queueing the typed failure lets Read return -1 and release its
            // serialization lock before the playback session stops the lease.
            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var (input, captured) =
                        ((SeekableStreamMediaInput, PlaybackFailure))state!;
                    input.Failed?.Invoke(captured);
                },
                (this, failure));
        }
    }
}
