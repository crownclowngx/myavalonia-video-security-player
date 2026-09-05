using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

internal readonly record struct EncryptedStreamResourceSnapshot(
    int LiveStreams,
    int CachedPlaintextChunks);

internal static class EncryptedStreamResourceDiagnostics
{
    private const int MaximumTraceLength = 256;
    private static readonly ConcurrentQueue<long> RecentChunkReads = new();
    private static int _liveStreams;
    private static int _cachedPlaintextChunks;
    private static long _cacheRequests;
    private static long _cacheHits;
    private static long _cacheMisses;
    private static long _cacheEvictions;
    private static readonly object RetainedSync = new();
    private static readonly List<WeakReference<SeekableEncryptedVideoStream>>
        RetainedStreams = [];

    public static EncryptedStreamResourceSnapshot Capture() => new(
        Volatile.Read(ref _liveStreams),
        Volatile.Read(ref _cachedPlaintextChunks));

    public static PlaybackCacheStatistics CaptureCacheStatistics() => new(
        Interlocked.Read(ref _cacheRequests),
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _cacheEvictions));

    public static void ResetCacheStatistics()
    {
        Interlocked.Exchange(ref _cacheRequests, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _cacheEvictions, 0);
    }

    public static IReadOnlyList<long> CaptureRecentChunkReads() =>
        RecentChunkReads.ToArray();

    public static void ClearRecentChunkReads()
    {
        while (RecentChunkReads.TryDequeue(out _))
        {
        }
    }

    public static void StreamCreated(SeekableEncryptedVideoStream stream)
    {
        Interlocked.Increment(ref _liveStreams);
        lock (RetainedSync)
        {
            RetainedStreams.RemoveAll(reference => !reference.TryGetTarget(out _));
            RetainedStreams.Add(new WeakReference<SeekableEncryptedVideoStream>(stream));
        }
    }

    internal static int CaptureRetainedStreamCount()
    {
        lock (RetainedSync)
        {
            RetainedStreams.RemoveAll(reference => !reference.TryGetTarget(out _));
            return RetainedStreams.Count;
        }
    }
    public static void StreamDisposed() => Interlocked.Decrement(ref _liveStreams);
    public static void ChunkCached() => Interlocked.Increment(ref _cachedPlaintextChunks);
    public static void ChunkReleased() => Interlocked.Decrement(ref _cachedPlaintextChunks);
    public static void CacheRequested() => Interlocked.Increment(ref _cacheRequests);
    public static void CacheHit() => Interlocked.Increment(ref _cacheHits);
    public static void CacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public static void CacheEvicted() => Interlocked.Increment(ref _cacheEvictions);

    public static void ChunkRead(long chunkIndex)
    {
        RecentChunkReads.Enqueue(chunkIndex);
        while (RecentChunkReads.Count > MaximumTraceLength)
        {
            RecentChunkReads.TryDequeue(out _);
        }
    }
}

/// <summary>
/// 把 SECVID03 暴露为与原视频内容完全一致的只读、可随机定位流。
/// </summary>
/// <remarks>
/// 虚拟流布局为“明文原视频前缀 + 按需解密的视频主体”。打开时仅执行一次 PBKDF2 并验证固定头，
/// 不解密视频块；真正读取时只认证涉及的块。四块 LRU 缓存把常见的解码器回读和小范围拖动控制在约 4 MiB 明文内存。
/// Stream 本身不承诺多线程并发安全，LibVLC 适配器会在回调入口进行串行化。
/// </remarks>
public sealed class SeekableEncryptedVideoStream : Stream
{
    private const int MaxCachedChunks = 4;
    private readonly FileStream _file;
    private readonly Secvid03AuthenticationContext _authentication;
    private readonly Secvid03Header _header;
    private readonly Dictionary<long, CacheEntry> _cache = [];
    private readonly LinkedList<long> _lru = [];
    private readonly Stack<byte[]> _availablePlaintextBuffers = new(MaxCachedChunks);
    private readonly byte[][] _plaintextBuffers = new byte[MaxCachedChunks][];
    private byte[] _cipherBuffer;
    private long _position;
    private bool _disposed;

    private sealed record CacheEntry(byte[] Data, int Length, LinkedListNode<long> Node);

    private SeekableEncryptedVideoStream(FileStream file, Secvid03AuthenticationContext authentication)
    {
        _file = file;
        _authentication = authentication;
        _header = authentication.Header;
        _cipherBuffer = new byte[_header.ChunkSize];
        for (var index = 0; index < _plaintextBuffers.Length; index++)
        {
            var buffer = new byte[_header.ChunkSize];
            _plaintextBuffers[index] = buffer;
            _availablePlaintextBuffers.Push(buffer);
        }
        EncryptedStreamResourceDiagnostics.StreamCreated(this);
    }

    internal IReadOnlyList<byte[]> PlaintextBuffers => _plaintextBuffers;
    internal byte[] CipherBuffer => _cipherBuffer;

    /// <summary>
    /// 打开容器、派生密钥并验证不可变固定头，不预读或解密视频主体。
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">密码错误，或固定头/原视频前缀已被修改。</exception>
    public static SeekableEncryptedVideoStream Open(string path, string password)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Secvid03Format.ChunkSize, false);
        Secvid03AuthenticationContext? authentication = null;
        try
        {
            var headerBytes = new byte[Secvid03Format.FixedHeaderSize];
            file.ReadExactly(headerBytes);
            var header = Secvid03Format.ParseHeader(headerBytes, file.Length);
            var originalHeader = new byte[header.OriginalHeaderLength];
            file.Position = Secvid03Format.OriginalHeaderOffset;
            file.ReadExactly(originalHeader);
            try
            {
                authentication = Secvid03Cryptography.Authenticate(password, header, originalHeader);
            }
            catch (Secvid03AuthenticationException ex)
            {
                throw new UnauthorizedAccessException("密码错误或文件已损坏。", ex);
            }
            return new SeekableEncryptedVideoStream(file, authentication);
        }
        catch
        {
            authentication?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return _header.OriginalFileLength; } }
    internal string FileId => Convert.ToHexString(_header.FileId);
    internal long OriginalFileLength => _header.OriginalFileLength;
    internal Secvid03DiagnosticSummary DiagnosticSummary => new(
        "SECVID03",
        Secvid03Format.Version,
        _header.OriginalHeaderLength,
        _header.OriginalFileLength,
        _header.ChunkSize,
        _header.ChunkCount,
        Secvid03Format.TagSize,
        "PBKDF2-SHA256",
        _header.KdfIterations);
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set
        {
            ThrowIfDisposed();
            if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        ThrowIfDisposed();
        if (destination.Length == 0 || _position >= Length) return 0;
        var totalRead = 0;
        var remaining = (int)Math.Min(destination.Length, Length - _position);

        while (remaining > 0)
        {
            if (_position < _header.OriginalHeaderLength)
            {
                // 原视频前缀物理上位于公开区之后，虚拟流中则必须从偏移 0 开始呈现给解码器。
                var count = (int)Math.Min(remaining, _header.OriginalHeaderLength - _position);
                _file.Position = Secvid03Format.OriginalHeaderOffset + _position;
                ReadExactly(_file, destination.Slice(totalRead, count));
                Advance(count, ref totalRead, ref remaining);
                continue;
            }

            var bodyPosition = _position - _header.OriginalHeaderLength;
            var chunkIndex = bodyPosition / _header.ChunkSize;
            var offsetInChunk = (int)(bodyPosition % _header.ChunkSize);
            var chunk = GetDecryptedChunk(chunkIndex);
            var countFromChunk = Math.Min(remaining, chunk.Length - offsetInChunk);
            chunk.Data.AsSpan(offsetInChunk, countFromChunk).CopyTo(destination.Slice(totalRead, countFromChunk));
            Advance(countFromChunk, ref totalRead, ref remaining);
        }
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (newPosition < 0 || newPosition > Length) throw new IOException("Seek 位置超出视频范围。");
        _position = newPosition;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                // 先关闭文件句柄，再清零缓存明文和派生密钥，确保切换视频后可立即删除或覆盖原容器。
                try
                {
                    _file.Dispose();
                }
                finally
                {
                    for (var index = 0; index < _plaintextBuffers.Length; index++)
                    {
                        CryptographicOperations.ZeroMemory(_plaintextBuffers[index]);
                        _plaintextBuffers[index] = Array.Empty<byte>();
                    }
                    for (var index = 0; index < _cache.Count; index++)
                        EncryptedStreamResourceDiagnostics.ChunkReleased();
                    CryptographicOperations.ZeroMemory(_cipherBuffer);
                    // LibVLC 的托管回调包装可能晚于 Dispose 才被 GC 回收。主动断开大数组
                    // 引用，避免已清零的 1 MiB LOH 缓冲随包装对象继续保留多个 Document 周期。
                    _cipherBuffer = Array.Empty<byte>();
                    _cache.Clear();
                    _lru.Clear();
                    _availablePlaintextBuffers.Clear();
                    _authentication.Dispose();
                    EncryptedStreamResourceDiagnostics.StreamDisposed();
                }
            }
        }
        base.Dispose(disposing);
    }

    private (byte[] Data, int Length) GetDecryptedChunk(long chunkIndex)
    {
        EncryptedStreamResourceDiagnostics.CacheRequested();
        EncryptedStreamResourceDiagnostics.ChunkRead(chunkIndex);
        if (_cache.TryGetValue(chunkIndex, out var cached))
        {
            EncryptedStreamResourceDiagnostics.CacheHit();
            // 命中缓存时把节点提升到链表头，尾节点始终代表最久未使用的块。
            _lru.Remove(cached.Node);
            _lru.AddFirst(cached.Node);
            return (cached.Data, cached.Length);
        }

        EncryptedStreamResourceDiagnostics.CacheMiss();
        // 物理偏移可以 O(1) 计算：每个完整块固定由 1 MiB 密文和 16 字节标签组成。
        // 最后一块可以较短，但它之前的所有块必然是完整块，因此公式对尾块同样成立。
        var chunkPlainOffset = checked(chunkIndex * _header.ChunkSize);
        var plainLength = (int)Math.Min(_header.ChunkSize, _header.PlainBodyLength - chunkPlainOffset);
        if (plainLength <= 0) throw new EndOfStreamException("请求的加密块超出视频范围。");
        var physicalOffset = checked(_header.EncryptedDataOffset + chunkIndex * (_header.ChunkSize + Secvid03Format.TagSize));
        _file.Position = physicalOffset;
        var cipher = _cipherBuffer.AsSpan(0, plainLength);
        _file.ReadExactly(cipher);
        Span<byte> tag = stackalloc byte[Secvid03Format.TagSize];
        _file.ReadExactly(tag);

        var plain = AcquirePlaintextBuffer();
        try
        {
            Secvid03Cryptography.DecryptChunk(
                _authentication,
                chunkIndex,
                cipher,
                tag,
                plain.AsSpan(0, plainLength));
        }
        catch (Secvid03ContentAuthenticationException ex)
        {
            CryptographicOperations.ZeroMemory(plain);
            _availablePlaintextBuffers.Push(plain);
            throw new InvalidDataException("密码错误或文件已损坏。", ex);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            _availablePlaintextBuffers.Push(plain);
            throw;
        }

        var node = _lru.AddFirst(chunkIndex);
        _cache[chunkIndex] = new CacheEntry(plain, plainLength, node);
        EncryptedStreamResourceDiagnostics.ChunkCached();
        return (plain, plainLength);
    }

    private byte[] AcquirePlaintextBuffer()
    {
        if (_availablePlaintextBuffers.TryPop(out var available))
            return available;

        if (_lru.Last is not { } oldest)
            throw new InvalidOperationException("明文缓存槽状态不一致。");

        var oldEntry = _cache[oldest.Value];
        _cache.Remove(oldest.Value);
        _lru.RemoveLast();
        EncryptedStreamResourceDiagnostics.CacheEvicted();
        EncryptedStreamResourceDiagnostics.ChunkReleased();
        CryptographicOperations.ZeroMemory(oldEntry.Data);
        return oldEntry.Data;
    }

    private void Advance(int count, ref int totalRead, ref int remaining)
    {
        _position += count;
        totalRead += count;
        remaining -= count;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        // 随机读取也可能发生短读；若到达 EOF 仍未填满，说明容器在打开后被截断或底层存储损坏。
        var read = 0;
        while (read < destination.Length)
        {
            var current = stream.Read(destination[read..]);
            if (current == 0) throw new EndOfStreamException("加密视频被截断。");
            read += current;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
