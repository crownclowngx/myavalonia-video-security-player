using System.Buffers.Binary;
using System.Text;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

/// <summary>
/// SECVID03 固定头解析后的内存模型。
/// </summary>
/// <remarks>
/// 该对象只描述会受到 AES-GCM 认证保护的不可变字段，不包含可原地修改的公开标题和描述。
/// <see cref="Bytes"/> 始终保存完整的 256 字节固定头，生成 AAD 时会把认证标签所在区域清零，
/// 从而保证写入标签前和读取验证时能够得到完全相同的认证数据。
/// </remarks>
internal sealed class Secvid03Header
{
    public required byte[] Bytes { get; init; }
    public required byte[] Salt { get; init; }
    public required byte[] FileId { get; init; }
    public required byte[] NoncePrefix { get; init; }
    public required byte[] HeaderTag { get; init; }
    public required string OriginalExtension { get; init; }
    public required Secvid03Layout Layout { get; init; }
    public required int ChunkSize { get; init; }
    public required int KdfIterations { get; init; }

    public long OriginalFileLength => Layout.OriginalFileLength;
    public long PlainBodyLength => Layout.PlainBodyLength;
    public long EncryptedDataOffset => Layout.EncryptedDataOffset;
    public int OriginalHeaderLength => Layout.OriginalHeaderLength;
    public long ChunkCount => Layout.ChunkCount;
}

/// <summary>
/// 从受信任输入或严格解析后的固定头计算得到的 SECVID03 物理布局。
/// </summary>
internal readonly record struct Secvid03Layout(
    long OriginalFileLength,
    int OriginalHeaderLength,
    long PlainBodyLength,
    long EncryptedDataOffset,
    long ChunkCount,
    long PhysicalFileLength);

internal static class Secvid03Format
{
    // 以下常量共同定义 SECVID03 的磁盘布局。格式版本一旦发布，不允许随意修改这些值，
    // 否则旧文件的偏移计算、nonce 计数器和认证数据都会失去兼容性。
    // 固定头关键偏移：
    //   0..7    魔数；8..67 版本、长度、偏移、分块和 KDF 参数；
    //   68..71  保留；72..87 salt；88..103 文件 ID；104..111 nonce 前缀；
    //   112..147 扩展名长度及固定槽位；148..163 固定头认证标签；164..255 保留。
    public const string Magic = "SECVID03";
    public const int Version = 3;
    public const int FixedHeaderSize = 256;
    public const int PublicInfoCapacity = 64 * 1024;
    public const int ChunkSize = 1024 * 1024;
    public const int TagSize = 16;
    public const int KdfIterations = 600_000;
    public const int HeaderTagOffset = 148;
    public const int OriginalHeaderOffset = FixedHeaderSize + PublicInfoCapacity;
    public const int MaxOriginalHeaderLength = 40;

    private const int ExtensionOffset = 116;
    private const int ExtensionCapacity = 32;

    /// <summary>
    /// 根据原视频信息创建尚未写入认证标签的 256 字节不可变固定头。
    /// </summary>
    /// <remarks>
    /// 此方法集中写入全部固定偏移，避免加密器、播放器分别维护磁盘布局造成偏移漂移。
    /// 所有未使用字节依赖新数组初始化为零；解析端会再次验证保留区为零，为后续格式扩展留下明确边界。
    /// </remarks>
    public static Secvid03Header CreateHeader(
        long originalFileLength,
        int originalHeaderLength,
        string originalExtension,
        byte[] salt,
        byte[] fileId,
        byte[] noncePrefix)
    {
        var layout = CalculateLayout(originalFileLength, originalHeaderLength);

        originalExtension ??= string.Empty;
        var extensionBytes = Encoding.UTF8.GetBytes(originalExtension);
        if (extensionBytes.Length > ExtensionCapacity)
            throw new ArgumentException($"原始扩展名的 UTF-8 长度不能超过 {ExtensionCapacity} 字节。", nameof(originalExtension));
        if (salt.Length != 16 || fileId.Length != 16 || noncePrefix.Length != 8)
            throw new ArgumentException("SECVID03 随机参数长度不正确。");

        var bytes = new byte[FixedHeaderSize];
        Encoding.ASCII.GetBytes(Magic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), FixedHeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16, 8), FixedHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), PublicInfoCapacity);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), originalHeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32, 8), originalFileLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40, 8), layout.PlainBodyLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48, 8), layout.EncryptedDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56, 4), ChunkSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60, 4), TagSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64, 4), KdfIterations);
        salt.CopyTo(bytes, 72);
        fileId.CopyTo(bytes, 88);
        noncePrefix.CopyTo(bytes, 104);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(112, 4), extensionBytes.Length);
        extensionBytes.CopyTo(bytes, ExtensionOffset);

        return new Secvid03Header
        {
            Bytes = bytes,
            Salt = salt.ToArray(),
            FileId = fileId.ToArray(),
            NoncePrefix = noncePrefix.ToArray(),
            HeaderTag = new byte[TagSize],
            OriginalExtension = originalExtension,
            Layout = layout,
            ChunkSize = ChunkSize,
            KdfIterations = KdfIterations
        };
    }

    public static Secvid03Header ParseHeader(ReadOnlySpan<byte> bytes, long physicalFileLength)
    {
        // 在执行 PBKDF2 这种高成本操作之前先完成结构检查，可以尽早拒绝截断文件、伪造长度和整数溢出。
        if (bytes.Length != FixedHeaderSize)
            throw new InvalidDataException("SECVID03 固定头长度不正确。");
        if (!bytes[..8].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            throw new InvalidDataException("不是 SECVID03 加密视频。旧格式需要重新加密。");

        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
        var publicOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(16, 8));
        var publicCapacity = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        var originalHeaderLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4));
        var originalFileLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(32, 8));
        var bodyLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(40, 8));
        var encryptedOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(48, 8));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(56, 4));
        var tagSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(60, 4));
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(64, 4));
        var extensionLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(112, 4));

        if (version != Version || headerSize != FixedHeaderSize || publicOffset != FixedHeaderSize ||
            publicCapacity != PublicInfoCapacity || chunkSize != ChunkSize || tagSize != TagSize ||
            iterations != KdfIterations || originalHeaderLength < 0 ||
            originalHeaderLength > MaxOriginalHeaderLength || originalFileLength < 0 || bodyLength < 0 ||
            extensionLength < 0 || extensionLength > ExtensionCapacity)
            throw new InvalidDataException("SECVID03 固定头字段无效。");

        if (!IsAllZero(bytes.Slice(68, 4)) ||
            !IsAllZero(bytes.Slice(ExtensionOffset + extensionLength, ExtensionCapacity - extensionLength)) ||
            !IsAllZero(bytes.Slice(HeaderTagOffset + TagSize)))
            throw new InvalidDataException("SECVID03 固定头保留字段必须为零。");

        string extension;
        try
        {
            extension = new UTF8Encoding(false, true).GetString(bytes.Slice(ExtensionOffset, extensionLength));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("SECVID03 原始扩展名不是有效的 UTF-8。", ex);
        }

        Secvid03Layout layout;
        try
        {
            layout = CalculateLayout(originalFileLength, originalHeaderLength);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("SECVID03 长度字段无效或溢出。", ex);
        }

        if (bodyLength != layout.PlainBodyLength ||
            encryptedOffset != layout.EncryptedDataOffset ||
            physicalFileLength != layout.PhysicalFileLength)
            throw new InvalidDataException("SECVID03 文件长度或偏移不一致。");

        return new Secvid03Header
        {
            Bytes = bytes.ToArray(),
            Salt = bytes.Slice(72, 16).ToArray(),
            FileId = bytes.Slice(88, 16).ToArray(),
            NoncePrefix = bytes.Slice(104, 8).ToArray(),
            HeaderTag = bytes.Slice(HeaderTagOffset, TagSize).ToArray(),
            OriginalExtension = extension,
            Layout = layout,
            ChunkSize = chunkSize,
            KdfIterations = iterations
        };
    }

    internal static Secvid03Layout CalculateLayout(long originalFileLength, int originalHeaderLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalFileLength);
        ArgumentOutOfRangeException.ThrowIfNegative(originalHeaderLength);
        if (originalHeaderLength > MaxOriginalHeaderLength)
            throw new ArgumentOutOfRangeException(
                nameof(originalHeaderLength),
                $"SECVID03 原视频前缀不能超过 {MaxOriginalHeaderLength} 字节。");
        if (originalHeaderLength > originalFileLength)
            throw new ArgumentException("视频头长度不能超过原始文件长度。", nameof(originalHeaderLength));

        var bodyLength = checked(originalFileLength - originalHeaderLength);
        var encryptedDataOffset = checked((long)OriginalHeaderOffset + originalHeaderLength);
        var chunkCount = bodyLength == 0 ? 0 : checked(1 + (bodyLength - 1) / ChunkSize);
        if (chunkCount > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(originalFileLength), "SECVID03 视频块数量超过 nonce 计数器范围。");

        var physicalFileLength = checked(encryptedDataOffset + bodyLength + chunkCount * TagSize);
        return new Secvid03Layout(
            originalFileLength,
            originalHeaderLength,
            bodyLength,
            encryptedDataOffset,
            chunkCount,
            physicalFileLength);
    }

    public static int DetectOriginalHeaderLength(Stream stream)
    {
        // 只保留解码器识别容器所需的最小前缀。播放器读取时会把该前缀和解密主体重新拼成原视频视图。
        // MP4 的 ftyp 位于偏移 4，而不是文件开头；这里特意按真实格式位置检测。
        var originalPosition = stream.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[40];
            stream.Position = 0;
            var read = stream.Read(buffer);
            if (read < 9)
                return read;

            if (read >= 8 && buffer.Slice(4, 4).SequenceEqual("ftyp"u8)) return Math.Min(32, read);
            if (read >= 12 && buffer.Slice(8, 4).SequenceEqual("AVI "u8)) return Math.Min(12, read);
            if (read >= 4 && buffer[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return Math.Min(40, read);
            if (buffer[..3].SequenceEqual("FLV"u8)) return Math.Min(9, read);
            return Math.Min(32, read);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0) return false;
        }
        return true;
    }
}
