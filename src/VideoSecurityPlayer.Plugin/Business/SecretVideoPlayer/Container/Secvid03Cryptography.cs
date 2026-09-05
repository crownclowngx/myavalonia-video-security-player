using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

/// <summary>
/// SECVID03 认证成功后持有的短生命周期密码学上下文。
/// </summary>
internal sealed class Secvid03AuthenticationContext : IDisposable
{
    private bool _disposed;

    public Secvid03AuthenticationContext(Secvid03Header header, byte[] key, byte[] immutableDigest)
    {
        Header = header;
        Key = key;
        ImmutableDigest = immutableDigest;
    }

    public Secvid03Header Header { get; }
    public byte[] Key { get; }
    public byte[] ImmutableDigest { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(Key);
        CryptographicOperations.ZeroMemory(ImmutableDigest);
        _disposed = true;
    }
}

internal sealed class Secvid03AuthenticationException(string message, Exception innerException)
    : CryptographicException(message, innerException);

internal sealed class Secvid03ContentAuthenticationException(string message, Exception innerException)
    : CryptographicException(message, innerException);

/// <summary>
/// 播放流和明文导出共享的 SECVID03 认证规则，集中维护 key、nonce、AAD 和标签语义。
/// </summary>
internal static class Secvid03Cryptography
{
    internal static byte[] DeriveKey(string password, Secvid03Header header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            header.Salt,
            header.KdfIterations,
            HashAlgorithmName.SHA256,
            32);
    }

    public static Secvid03AuthenticationContext Authenticate(
        string password,
        Secvid03Header header,
        ReadOnlySpan<byte> originalHeader)
    {
        var key = DeriveKey(password, header);
        try
        {
            var immutableDigest = VerifyHeaderAndCreateDigest(header, originalHeader, key);
            return new Secvid03AuthenticationContext(header, key, immutableDigest);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    /// <summary>
    /// 使用已经派生的密钥验证固定头，并返回视频块 AAD 所需的不可变摘要。
    /// 调用方继续拥有传入密钥；此方法只清理自己创建且未能返回的摘要。
    /// </summary>
    internal static byte[] VerifyHeaderAndCreateDigest(
        Secvid03Header header,
        ReadOnlySpan<byte> originalHeader,
        ReadOnlySpan<byte> key)
    {
        var immutableAad = CreateImmutableHeaderAad(header, originalHeader);
        var immutableDigest = SHA256.HashData(immutableAad);
        try
        {
            using var aes = new AesGcm(key, Secvid03Format.TagSize);
            aes.Decrypt(
                CreateNonce(header, 0),
                ReadOnlySpan<byte>.Empty,
                header.HeaderTag,
                Span<byte>.Empty,
                immutableAad);
            return immutableDigest;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(immutableDigest);
            throw new Secvid03AuthenticationException("密码错误或固定头已损坏。", ex);
        }
    }

    public static void DecryptChunk(
        Secvid03AuthenticationContext context,
        long chunkIndex,
        ReadOnlySpan<byte> cipher,
        ReadOnlySpan<byte> tag,
        Span<byte> plain)
    {
        try
        {
            Span<byte> nonce = stackalloc byte[12];
            WriteNonce(context.Header, checked((uint)chunkIndex + 1), nonce);
            Span<byte> aad = stackalloc byte[40];
            WriteChunkAad(context.ImmutableDigest, chunkIndex, aad);
            using var aes = new AesGcm(context.Key, Secvid03Format.TagSize);
            aes.Decrypt(
                nonce,
                cipher,
                tag,
                plain,
                aad);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new Secvid03ContentAuthenticationException("视频内容认证失败。", ex);
        }
    }

    internal static byte[] CreateNonce(Secvid03Header header, uint counter)
    {
        var nonce = new byte[12];
        WriteNonce(header, counter, nonce);
        return nonce;
    }

    private static void WriteNonce(Secvid03Header header, uint counter, Span<byte> destination)
    {
        if (destination.Length < 12)
            throw new ArgumentException("Nonce 缓冲区必须至少为 12 字节。", nameof(destination));

        header.NoncePrefix.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), counter);
    }

    internal static byte[] CreateImmutableHeaderAad(
        Secvid03Header header,
        ReadOnlySpan<byte> originalHeader)
    {
        var headerBytes = header.Bytes.ToArray();
        headerBytes.AsSpan(Secvid03Format.HeaderTagOffset, Secvid03Format.TagSize).Clear();
        var aad = new byte[checked(headerBytes.Length + originalHeader.Length)];
        headerBytes.CopyTo(aad, 0);
        originalHeader.CopyTo(aad.AsSpan(headerBytes.Length));
        return aad;
    }

    internal static byte[] CreateChunkAad(
        ReadOnlySpan<byte> immutableHeaderDigest,
        long chunkIndex)
    {
        var aad = new byte[40];
        WriteChunkAad(immutableHeaderDigest, chunkIndex, aad);
        return aad;
    }

    private static void WriteChunkAad(
        ReadOnlySpan<byte> immutableHeaderDigest,
        long chunkIndex,
        Span<byte> destination)
    {
        if (immutableHeaderDigest.Length != 32)
            throw new ArgumentException("不可变头摘要必须为 32 字节。", nameof(immutableHeaderDigest));
        if (destination.Length < 40)
            throw new ArgumentException("块 AAD 缓冲区必须至少为 40 字节。", nameof(destination));

        immutableHeaderDigest.CopyTo(destination);
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(32, 8), chunkIndex);
    }
}
