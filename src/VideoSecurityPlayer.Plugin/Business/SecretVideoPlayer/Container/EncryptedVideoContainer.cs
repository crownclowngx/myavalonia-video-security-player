using System.Buffers.Binary;
using System.Text;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

/// <summary>
/// 无需密码即可读取的 SECVID03 公开信息。
/// </summary>
/// <remarks>
/// 标题、描述和原始文件名是明文元数据，不提供真实性保证；任何拥有文件写权限的人都可以修改它们。
/// 原视频长度和扩展名来自不可变固定头，播放时还会通过 AES-GCM 固定头标签验证。
/// </remarks>
public sealed record EncryptedVideoPublicInfo(
    int Version,
    string OriginalFileName,
    string OriginalExtension,
    string Title,
    string Description,
    long OriginalFileLength,
    string FileId);

/// <summary>
/// 提供 SECVID03 公开信息的读取、校验和原地更新能力。
/// </summary>
/// <remarks>
/// 该类型只处理无需密码即可访问的公开层，不负责视频解密。把公开信息与加密主体分离，
/// 是为了让文件管理界面能在输入密码前展示标题和描述，并允许修改它们而不重写大型视频文件。
/// </remarks>
public static class EncryptedVideoContainer
{
    // 公开区内部布局固定为 32 字节记录头 + UTF-8 负载 + 零填充：
    // 记录头依次保存 PUBMETA1、版本、总长度、文件名/标题/描述长度和负载 CRC32。
    // CRC 不覆盖零填充，也不承担安全认证职责；它只需要准确判断当前记录是否完整可读。
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string PublicMagic = "PUBMETA1";
    private const int PublicVersion = 1;
    private const int PublicHeaderSize = 32;
    public const int MaxTitleRunes = 200;
    public const int MaxDescriptionRunes = 10_000;
    public const int MaxFileNameRunes = 255;
    private const int MaxTitleBytes = 800;
    private const int MaxDescriptionBytes = 40_000;
    private const int MaxFileNameBytes = 1_020;

    /// <summary>
    /// 读取公开信息区，不派生密钥，也不要求用户输入密码。
    /// </summary>
    /// <remarks>
    /// 此方法仍会严格检查固定头边界和公开区 CRC。CRC 只用于发现写入中断或意外损坏，
    /// 不能阻止主动篡改；真正的视频完整性由随机读取流在解密每个块时验证。
    /// </remarks>
    public static EncryptedVideoPublicInfo ReadPublicInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var headerBytes = new byte[Secvid03Format.FixedHeaderSize];
        stream.ReadExactly(headerBytes);
        var header = Secvid03Format.ParseHeader(headerBytes, stream.Length);
        var publicBytes = new byte[Secvid03Format.PublicInfoCapacity];
        stream.Position = Secvid03Format.FixedHeaderSize;
        stream.ReadExactly(publicBytes);
        var (fileName, title, description) = ParsePublicRegion(publicBytes);
        return new EncryptedVideoPublicInfo(
            Secvid03Format.Version,
            fileName,
            header.OriginalExtension,
            title,
            description,
            header.OriginalFileLength,
            Convert.ToHexString(header.FileId));
    }

    /// <summary>
    /// 在固定的 64 KiB 区域内原地更新标题和描述，不移动或重新加密视频主体。
    /// </summary>
    /// <remarks>
    /// 写入顺序刻意采用“先负载、Flush，再写 32 字节记录头和 CRC、Flush”。如果进程在中途退出，
    /// 读取端会得到 CRC 错误，而不会把部分新数据当作有效记录；固定区域之后的视频偏移始终不变。
    /// 原始文件名沿用当前记录，公开 API 不允许借此操作改写它。
    /// </remarks>
    public static void UpdatePublicInfo(string path, string title, string description)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var headerBytes = new byte[Secvid03Format.FixedHeaderSize];
        stream.ReadExactly(headerBytes);
        var header = Secvid03Format.ParseHeader(headerBytes, stream.Length);

        var currentRegion = new byte[Secvid03Format.PublicInfoCapacity];
        stream.Position = Secvid03Format.FixedHeaderSize;
        stream.ReadExactly(currentRegion);
        var (fileName, _, _) = ParsePublicRegion(currentRegion);
        var newRegion = BuildPublicRegion(fileName, title, description);

        stream.Position = Secvid03Format.FixedHeaderSize + PublicHeaderSize;
        stream.Write(newRegion, PublicHeaderSize, newRegion.Length - PublicHeaderSize);
        stream.Flush(flushToDisk: true);
        stream.Position = Secvid03Format.FixedHeaderSize;
        stream.Write(newRegion, 0, PublicHeaderSize);
        stream.Flush(flushToDisk: true);
    }

    internal static byte[] BuildPublicRegion(string originalFileName, string title, string description)
    {
        // 先构造完整的 64 KiB 镜像，再交给调用者一次性按既定顺序落盘。
        // 未使用空间保持为零，避免旧描述残留在缩短后的记录尾部。
        ValidateText(originalFileName, nameof(originalFileName), MaxFileNameRunes, MaxFileNameBytes);
        ValidateText(title, nameof(title), MaxTitleRunes, MaxTitleBytes);
        ValidateText(description, nameof(description), MaxDescriptionRunes, MaxDescriptionBytes);

        var fileNameBytes = StrictUtf8.GetBytes(originalFileName);
        var titleBytes = StrictUtf8.GetBytes(title);
        var descriptionBytes = StrictUtf8.GetBytes(description);
        var totalLength = checked(PublicHeaderSize + fileNameBytes.Length + titleBytes.Length + descriptionBytes.Length);
        if (totalLength > Secvid03Format.PublicInfoCapacity)
            throw new ArgumentException("公开信息超过 SECVID03 的 64 KiB 容量。");

        var region = new byte[Secvid03Format.PublicInfoCapacity];
        Encoding.ASCII.GetBytes(PublicMagic).CopyTo(region, 0);
        BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(8, 4), PublicVersion);
        BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(12, 4), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(16, 4), fileNameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(20, 4), titleBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(24, 4), descriptionBytes.Length);

        var offset = PublicHeaderSize;
        fileNameBytes.CopyTo(region, offset);
        offset += fileNameBytes.Length;
        titleBytes.CopyTo(region, offset);
        offset += titleBytes.Length;
        descriptionBytes.CopyTo(region, offset);
        var crc = Crc32.Compute(region.AsSpan(PublicHeaderSize, totalLength - PublicHeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(28, 4), crc);
        return region;
    }

    internal static (string FileName, string Title, string Description) ParsePublicRegion(ReadOnlySpan<byte> region)
    {
        // 任何长度都来自外部文件，必须先验证所有分量及其和，再执行 Slice，避免越界和溢出。
        if (region.Length != Secvid03Format.PublicInfoCapacity ||
            !region[..8].SequenceEqual(Encoding.ASCII.GetBytes(PublicMagic)))
            throw new InvalidDataException("SECVID03 公开信息不可读取。");

        var version = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(8, 4));
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(12, 4));
        var fileNameLength = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(16, 4));
        var titleLength = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(20, 4));
        var descriptionLength = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(24, 4));
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(region.Slice(28, 4));

        if (version != PublicVersion || totalLength < PublicHeaderSize || totalLength > region.Length ||
            fileNameLength < 0 || fileNameLength > MaxFileNameBytes || titleLength < 0 || titleLength > MaxTitleBytes ||
            descriptionLength < 0 || descriptionLength > MaxDescriptionBytes ||
            PublicHeaderSize + fileNameLength + titleLength + descriptionLength != totalLength)
            throw new InvalidDataException("SECVID03 公开信息长度无效。");

        var actualCrc = Crc32.Compute(region.Slice(PublicHeaderSize, totalLength - PublicHeaderSize));
        if (actualCrc != expectedCrc)
            throw new InvalidDataException("SECVID03 公开信息校验失败。");
        if (!IsAllZero(region[totalLength..]))
            throw new InvalidDataException("SECVID03 公开信息填充区域必须为零。");

        var offset = PublicHeaderSize;
        var fileName = DecodeUtf8(region.Slice(offset, fileNameLength));
        offset += fileNameLength;
        var title = DecodeUtf8(region.Slice(offset, titleLength));
        offset += titleLength;
        var description = DecodeUtf8(region.Slice(offset, descriptionLength));
        ValidateText(fileName, nameof(fileName), MaxFileNameRunes, MaxFileNameBytes);
        ValidateText(title, nameof(title), MaxTitleRunes, MaxTitleBytes);
        ValidateText(description, nameof(description), MaxDescriptionRunes, MaxDescriptionBytes);
        return (fileName, title, description);
    }

    /// <summary>
    /// 按 Unicode 标量值统计用户可见字符上限，而不是按 UTF-16 代码单元统计。
    /// </summary>
    /// <remarks>例如一个常见 emoji 在 .NET string 中占两个 char，但在这里计为一个 Rune。</remarks>
    public static int CountRunes(string value) => value.EnumerateRunes().Count();

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("SECVID03 公开信息不是有效的 UTF-8。", ex);
        }
    }

    private static void ValidateText(string value, string parameterName, int maxRunes, int maxBytes)
    {
        // 同时限制 Rune 数和 UTF-8 字节数：前者符合 UI 字数语义，后者保证二进制记录容量绝对可控。
        // 严格 UTF-8 编码器会拒绝未配对代理项，避免写入后无法无损往返的字符串。
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (CountRunes(value) > maxRunes)
            throw new ArgumentException($"{parameterName} 最多允许 {maxRunes} 个字符。", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes)
                throw new ArgumentException($"{parameterName} 的 UTF-8 数据最多允许 {maxBytes} 字节。", parameterName);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException($"{parameterName} 包含无效的 Unicode 字符。", parameterName, ex);
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == 0 || (Rune.IsControl(rune) && rune.Value is not '\r' and not '\n' and not '\t'))
                throw new ArgumentException($"{parameterName} 包含不允许的控制字符。", parameterName);
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static class Crc32
    {
        // 使用标准 CRC-32/ISO-HDLC 多项式。这里的数据量固定不超过 64 KiB，
        // 简单逐位实现足够快，并避免为了非安全校验额外引入第三方依赖。
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
