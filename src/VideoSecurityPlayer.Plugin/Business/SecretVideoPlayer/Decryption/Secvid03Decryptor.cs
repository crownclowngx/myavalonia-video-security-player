using System.Security.Cryptography;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;

/// <summary>
/// 以常量内存顺序认证并导出一个 SECVID03 容器。
/// </summary>
public sealed class Secvid03Decryptor : ISecvid03Decryptor
{
    private readonly IOutputFileTransactionFactory _transactionFactory;

    public Secvid03Decryptor()
        : this(new OutputFileTransactionFactory())
    {
    }

    public Secvid03Decryptor(IOutputFileTransactionFactory transactionFactory)
    {
        _transactionFactory = transactionFactory ?? throw new ArgumentNullException(nameof(transactionFactory));
    }

    public async Task DecryptAsync(
        string inputPath,
        string outputPath,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var fullInputPath = Path.GetFullPath(inputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (fullInputPath.Equals(fullOutputPath, StringComparison.OrdinalIgnoreCase))
            throw new VideoTaskException(
                VideoTaskFailureCode.InputOutputConflict,
                "输入文件和输出文件不能相同。");
        if (File.Exists(fullOutputPath))
            throw new VideoTaskException(
                VideoTaskFailureCode.OutputConflict,
                "输出文件已存在，不会覆盖现有文件。");

        IOutputFileTransaction? transaction = null;
        Exception? primaryError = null;
        try
        {
            await using var input = OpenInput(fullInputPath);
            var headerBytes = new byte[Secvid03Format.FixedHeaderSize];
            try
            {
                await ReadExactlyAsync(input, headerBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new VideoTaskException(
                    VideoTaskFailureCode.InvalidFormat,
                    "文件不是完整的 SECVID03。",
                    ex);
            }

            Secvid03Header header;
            try
            {
                header = Secvid03Format.ParseHeader(headerBytes, input.Length);
            }
            catch (InvalidDataException ex)
            {
                throw new VideoTaskException(
                    VideoTaskFailureCode.InvalidFormat,
                    "文件不是有效的 SECVID03。",
                    ex);
            }

            var originalHeader = new byte[header.OriginalHeaderLength];
            input.Position = Secvid03Format.OriginalHeaderOffset;
            try
            {
                await ReadExactlyAsync(input, originalHeader, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new VideoTaskException(
                    VideoTaskFailureCode.CorruptedContent,
                    "加密视频已被截断。",
                    ex);
            }

            Secvid03AuthenticationContext authentication;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                authentication = await Task.Run(
                        () => Secvid03Cryptography.Authenticate(password, header, originalHeader),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Secvid03AuthenticationException ex)
            {
                throw new VideoTaskException(
                    VideoTaskFailureCode.AuthenticationFailed,
                    "密码错误或固定头已损坏。",
                    ex);
            }

            using (authentication)
            {
                // 只有固定头认证成功后才创建明文 partial。
                cancellationToken.ThrowIfCancellationRequested();
                transaction = _transactionFactory.Create(fullOutputPath);
                var output = transaction.Stream;
                await output.WriteAsync(originalHeader, cancellationToken).ConfigureAwait(false);
                progress?.Report(new VideoTaskProgress(
                    VideoTaskState.Running,
                    header.OriginalHeaderLength,
                    header.OriginalFileLength,
                    CalculatePercentage(header.OriginalHeaderLength, header.OriginalFileLength),
                    "正在解密..."));

                input.Position = header.EncryptedDataOffset;
                var cipher = new byte[Secvid03Format.ChunkSize];
                var plain = new byte[Secvid03Format.ChunkSize];
                var tag = new byte[Secvid03Format.TagSize];
                try
                {
                    long processedBody = 0;
                    long chunkIndex = 0;
                    while (processedBody < header.PlainBodyLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var length = (int)Math.Min(header.ChunkSize, header.PlainBodyLength - processedBody);
                        try
                        {
                            await ReadExactlyAsync(input, cipher.AsMemory(0, length), cancellationToken)
                                .ConfigureAwait(false);
                            await ReadExactlyAsync(input, tag, cancellationToken).ConfigureAwait(false);
                        }
                        catch (EndOfStreamException ex)
                        {
                            throw new VideoTaskException(
                                VideoTaskFailureCode.CorruptedContent,
                                "加密视频已被截断。",
                                ex);
                        }

                        try
                        {
                            Secvid03Cryptography.DecryptChunk(
                                authentication,
                                chunkIndex,
                                cipher.AsSpan(0, length),
                                tag,
                                plain.AsSpan(0, length));
                        }
                        catch (Secvid03ContentAuthenticationException ex)
                        {
                            throw new VideoTaskException(
                                VideoTaskFailureCode.CorruptedContent,
                                "视频内容已损坏，无法完成认证。",
                                ex);
                        }

                        await output.WriteAsync(plain.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(plain.AsSpan(0, length));
                        processedBody += length;
                        chunkIndex++;
                        var processed = header.OriginalHeaderLength + processedBody;
                        var percentage = CalculatePercentage(processed, header.OriginalFileLength);
                        progress?.Report(new VideoTaskProgress(
                            VideoTaskState.Running,
                            processed,
                            header.OriginalFileLength,
                            percentage,
                            $"正在解密... {percentage:F1}%"));
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cipher);
                    CryptographicOperations.ZeroMemory(plain);
                    CryptographicOperations.ZeroMemory(tag);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new VideoTaskProgress(
                VideoTaskState.Succeeded,
                header.OriginalFileLength,
                header.OriginalFileLength,
                100,
                "解密完成"));
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
            primaryError = ex;
            throw VideoTaskFailureClassifier.Map(ex, readingInput: transaction is null);
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

    private static FileStream OpenInput(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                Secvid03Format.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            throw VideoTaskFailureClassifier.Map(ex, readingInput: true);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0)
                throw new EndOfStreamException("加密视频已被截断。");
            read += current;
        }
    }

    private static double CalculatePercentage(long processed, long total) =>
        total == 0 ? 100 : Math.Clamp(processed * 100d / total, 0, 100);
}
