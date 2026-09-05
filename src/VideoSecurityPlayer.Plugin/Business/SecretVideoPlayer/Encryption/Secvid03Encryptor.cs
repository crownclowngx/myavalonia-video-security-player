using System.Security.Cryptography;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

/// <summary>
/// 以流式方式创建 SECVID03 文件的加密器。
/// </summary>
/// <remarks>
/// 原视频主体按 1 MiB 独立使用 AES-256-GCM 加密，内存占用与视频总大小无关。
/// 每个块都有独立认证标签，因此播放器可以直接验证并解密目标块，不需要从文件开头顺序处理。
/// </remarks>
public sealed class Secvid03Encryptor : ISecvid03Encryptor
{
    private readonly ISecvid03EntropySource _entropySource;
    private readonly IOutputFileTransactionFactory _transactionFactory;

    public Secvid03Encryptor()
        : this(RandomSecvid03EntropySource.Instance, new OutputFileTransactionFactory())
    {
    }

    public Secvid03Encryptor(IOutputFileTransactionFactory transactionFactory)
        : this(RandomSecvid03EntropySource.Instance, transactionFactory)
    {
    }

    internal Secvid03Encryptor(ISecvid03EntropySource entropySource)
        : this(entropySource, new OutputFileTransactionFactory())
    {
    }

    internal Secvid03Encryptor(
        ISecvid03EntropySource entropySource,
        IOutputFileTransactionFactory transactionFactory)
    {
        _entropySource = entropySource ?? throw new ArgumentNullException(nameof(entropySource));
        _transactionFactory = transactionFactory ?? throw new ArgumentNullException(nameof(transactionFactory));
    }

    /// <summary>
    /// 将一个普通视频加密为 SECVID03 容器。
    /// </summary>
    /// <remarks>
    /// 加密期间先写入同目录的唯一临时文件，全部成功并 Flush 后才覆盖目标路径。
    /// 这样可避免取消、磁盘写满或认证计算异常留下一个看似完整的正式文件。
    /// </remarks>
    public Task EncryptAsync(
        string inputPath,
        string outputPath,
        string password,
        string title,
        string description,
        CancellationToken cancellationToken = default) =>
        EncryptAsync(
            new VideoEncryptionRequest(inputPath, outputPath, title, description),
            password,
            progress: null,
            cancellationToken: cancellationToken);

    public async Task EncryptAsync(
        VideoEncryptionRequest request,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var inputPath = Path.GetFullPath(request.InputPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (inputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            throw new VideoTaskException(VideoTaskFailureCode.InputOutputConflict, "输入文件和输出文件不能相同。");
        if (File.Exists(outputPath))
            throw new VideoTaskException(VideoTaskFailureCode.OutputConflict, "输出文件已经存在，不会覆盖现有文件。");

        IOutputFileTransaction? transaction = null;
        Exception? primaryError = null;
        try
        {
            // 原视频只以顺序流打开；即使输入达到数百 GiB，也只保留一个块的明文和密文缓冲区。
            await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, Secvid03Format.ChunkSize, true);
            var originalHeaderLength = Secvid03Format.DetectOriginalHeaderLength(input);
            var originalHeader = new byte[originalHeaderLength];
            input.Position = 0;
            await input.ReadExactlyAsync(originalHeader, cancellationToken);

            var entropy = _entropySource.Create();
            var extension = Path.GetExtension(inputPath).ToLowerInvariant();
            var header = Secvid03Format.CreateHeader(
                input.Length,
                originalHeaderLength,
                extension,
                entropy.Salt,
                entropy.FileId,
                entropy.NoncePrefix);
            var publicRegion = EncryptedVideoContainer.BuildPublicRegion(
                Path.GetFileName(inputPath),
                request.PublicTitle,
                request.PublicDescription);
            var key = Secvid03Cryptography.DeriveKey(password, header);
            byte[]? immutableDigest = null;

            try
            {
                // 固定头和明文视频前缀共同构成不可变 AAD。公开标题/描述刻意不在其中，
                // 因而它们可原地编辑，同时固定头、视频前缀和密文主体仍受完整性保护。
                var immutableAad = Secvid03Cryptography.CreateImmutableHeaderAad(header, originalHeader);
                immutableDigest = SHA256.HashData(immutableAad);
                using var aes = new AesGcm(key, Secvid03Format.TagSize);
                aes.Encrypt(Secvid03Cryptography.CreateNonce(header, 0), ReadOnlySpan<byte>.Empty, Span<byte>.Empty,
                    header.HeaderTag, immutableAad);
                header.HeaderTag.CopyTo(header.Bytes, Secvid03Format.HeaderTagOffset);

                transaction = _transactionFactory.Create(outputPath);
                var output = transaction.Stream;
                await output.WriteAsync(header.Bytes, cancellationToken);
                await output.WriteAsync(publicRegion, cancellationToken);
                await output.WriteAsync(originalHeader, cancellationToken);

                input.Position = originalHeaderLength;
                var plain = new byte[Secvid03Format.ChunkSize];
                var cipher = new byte[Secvid03Format.ChunkSize];
                try
                {
                    long processedBody = 0;
                    long chunkIndex = 0;
                    while (processedBody < header.PlainBodyLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var required = (int)Math.Min(Secvid03Format.ChunkSize, header.PlainBodyLength - processedBody);
                        await ReadExactlyAsync(input, plain.AsMemory(0, required), cancellationToken);
                        var tag = new byte[Secvid03Format.TagSize];
                        var aad = Secvid03Cryptography.CreateChunkAad(immutableDigest, chunkIndex);
                        aes.Encrypt(Secvid03Cryptography.CreateNonce(header, checked((uint)chunkIndex + 1)),
                            plain.AsSpan(0, required), cipher.AsSpan(0, required), tag, aad);
                        await output.WriteAsync(cipher.AsMemory(0, required), cancellationToken);
                        await output.WriteAsync(tag, cancellationToken);

                        processedBody += required;
                        chunkIndex++;
                        var processed = originalHeaderLength + processedBody;
                        var percentage = header.OriginalFileLength == 0 ? 100 : processed * 100d / header.OriginalFileLength;
                        progress?.Report(new VideoTaskProgress(
                            VideoTaskState.Running,
                            processed,
                            header.OriginalFileLength,
                            percentage,
                            $"正在加密... {percentage:F1}%"));
                    }
                }
                finally
                {
                    // 明文块属于受保护内容，即使正常完成或异常退出，也尽快从托管数组中清零。
                    CryptographicOperations.ZeroMemory(plain);
                    CryptographicOperations.ZeroMemory(cipher);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 派生材料只在本次加密调用中存活，不缓存到对象字段，也不会写入文件。
                if (immutableDigest is not null)
                    CryptographicOperations.ZeroMemory(immutableDigest);
                CryptographicOperations.ZeroMemory(key);
            }

            progress?.Report(new VideoTaskProgress(
                VideoTaskState.Succeeded,
                new FileInfo(inputPath).Length,
                new FileInfo(inputPath).Length,
                100,
                "加密完成"));
        }
        catch (OperationCanceledException ex)
        {
            primaryError = ex;
            throw;
        }
        catch (VideoTaskException ex)
        {
            primaryError = ex;
            throw;
        }
        catch (Exception ex)
        {
            var mapped = VideoTaskFailureClassifier.Map(ex, readingInput: transaction is null);
            primaryError = mapped;
            throw mapped;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                if (primaryError is null && transaction.CleanupError is not null)
                    throw transaction.CleanupError;
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // FileStream.ReadAsync 允许短读；循环到填满目标块，才能保证 GCM 标签对应准确的块边界。
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0) throw new EndOfStreamException("原视频在加密过程中被截断。");
            read += current;
        }
    }
}
