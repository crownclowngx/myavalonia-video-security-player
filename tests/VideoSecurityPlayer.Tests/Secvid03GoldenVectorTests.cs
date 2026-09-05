using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[Collection(Secvid03Collection.Name)]
public sealed class Secvid03GoldenVectorTests
{
    private const string Password = "SECVID03-G1-Vector!";
    private const string OriginalFileName = "g1-vector.mp4";
    private const string Title = "G1 固定向量 😀";
    private const string Description = "full block + tail\n公开信息不认证";
    private const string SaltHex = "000102030405060708090a0b0c0d0e0f";
    private const string FileIdHex = "101112131415161718191a1b1c1d1e1f";
    private const string NoncePrefixHex = "2021222324252627";

    [Fact]
    public async Task GoldenVector_MatchesWriterReaderAndIndependentReference()
    {
        var explicitGenerationDirectory = Environment.GetEnvironmentVariable("SECVID03_REGENERATE_VECTOR_DIR");
        if (!string.IsNullOrWhiteSpace(explicitGenerationDirectory))
            await GenerateAssetsAsync(Path.GetFullPath(explicitGenerationDirectory));

        var assetDirectory = string.IsNullOrWhiteSpace(explicitGenerationDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "TestAssets", "Secvid03Vectors", "v1")
            : Path.GetFullPath(explicitGenerationDirectory);
        var plaintextPath = Path.Combine(assetDirectory, OriginalFileName);
        var containerPath = Path.Combine(assetDirectory, "g1-vector.secvid");
        var manifestPath = Path.Combine(assetDirectory, "manifest.json");

        Assert.True(File.Exists(plaintextPath), $"缺少固定向量明文: {plaintextPath}");
        Assert.True(File.Exists(containerPath), $"缺少固定向量容器: {containerPath}");
        Assert.True(File.Exists(manifestPath), $"缺少固定向量清单: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<VectorManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.Equal("SECVID03", manifest.Format);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(Password, manifest.Password);
        Assert.Equal(OriginalFileName, manifest.OriginalFileName);
        Assert.Equal(Title, manifest.Title);
        Assert.Equal(Description, manifest.Description);
        Assert.Equal(SaltHex, manifest.SaltHex);
        Assert.Equal(FileIdHex, manifest.FileIdHex);
        Assert.Equal(NoncePrefixHex, manifest.NoncePrefixHex);

        var plaintext = await File.ReadAllBytesAsync(plaintextPath);
        var container = await File.ReadAllBytesAsync(containerPath);
        Assert.Equal(32 + Secvid03Format.ChunkSize + 17, plaintext.Length);
        Assert.Equal(manifest.PlaintextLength, plaintext.LongLength);
        Assert.Equal(manifest.ContainerLength, container.LongLength);
        Assert.Equal(manifest.PlaintextSha256, Sha256Hex(plaintext));
        Assert.Equal(manifest.ContainerSha256, Sha256Hex(container));
        Assert.Equal(manifest.FixedHeaderHex, Hex(container.AsSpan(0, Secvid03Format.FixedHeaderSize)));

        var reference = VerifyWithIndependentReference(container, Password);
        Assert.Equal(plaintext, reference.Plaintext);
        Assert.Equal(manifest.OriginalHeaderLength, reference.OriginalHeaderLength);
        Assert.Equal(manifest.EncryptedDataOffset, reference.EncryptedDataOffset);
        Assert.Equal(manifest.Chunks, reference.Chunks);

        var publicInfo = EncryptedVideoContainer.ReadPublicInfo(containerPath);
        Assert.Equal(OriginalFileName, publicInfo.OriginalFileName);
        Assert.Equal(Title, publicInfo.Title);
        Assert.Equal(Description, publicInfo.Description);

        using (var stream = SeekableEncryptedVideoStream.Open(containerPath, Password))
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            Assert.Equal(plaintext, output.ToArray());
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-GoldenVector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var input = Path.Combine(temporaryDirectory, OriginalFileName);
            var output = Path.Combine(temporaryDirectory, "actual.secvid");
            await File.WriteAllBytesAsync(input, plaintext);
            await new Secvid03Encryptor(new FixedEntropySource()).EncryptAsync(
                input,
                output,
                Password,
                Title,
                Description);
            Assert.Equal(container, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task GenerateAssetsAsync(string assetDirectory)
    {
        Directory.CreateDirectory(assetDirectory);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-GenerateVector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var plaintext = CreatePlaintext();
            var input = Path.Combine(temporaryDirectory, OriginalFileName);
            var output = Path.Combine(temporaryDirectory, "g1-vector.secvid");
            await File.WriteAllBytesAsync(input, plaintext);
            await new Secvid03Encryptor(new FixedEntropySource()).EncryptAsync(
                input,
                output,
                Password,
                Title,
                Description);
            var container = await File.ReadAllBytesAsync(output);
            var reference = VerifyWithIndependentReference(container, Password);
            Assert.Equal(plaintext, reference.Plaintext);

            File.Copy(input, Path.Combine(assetDirectory, OriginalFileName), overwrite: true);
            File.Copy(output, Path.Combine(assetDirectory, "g1-vector.secvid"), overwrite: true);
            var manifest = new VectorManifest
            {
                SchemaVersion = 1,
                Format = "SECVID03",
                Password = Password,
                OriginalFileName = OriginalFileName,
                Title = Title,
                Description = Description,
                SaltHex = SaltHex,
                FileIdHex = FileIdHex,
                NoncePrefixHex = NoncePrefixHex,
                PlaintextLength = plaintext.LongLength,
                PlaintextSha256 = Sha256Hex(plaintext),
                ContainerLength = container.LongLength,
                ContainerSha256 = Sha256Hex(container),
                FixedHeaderHex = Hex(container.AsSpan(0, Secvid03Format.FixedHeaderSize)),
                OriginalHeaderLength = reference.OriginalHeaderLength,
                EncryptedDataOffset = reference.EncryptedDataOffset,
                Chunks = reference.Chunks
            };
            await File.WriteAllTextAsync(
                Path.Combine(assetDirectory, "manifest.json"),
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }) + Environment.NewLine);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static byte[] CreatePlaintext()
    {
        var bytes = new byte[32 + Secvid03Format.ChunkSize + 17];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(index % 251);

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 32);
        "ftyp"u8.CopyTo(bytes.AsSpan(4, 4));
        "isom"u8.CopyTo(bytes.AsSpan(8, 4));
        return bytes;
    }

    private static ReferenceResult VerifyWithIndependentReference(byte[] container, string password)
    {
        const int fixedHeaderSize = 256;
        const int publicCapacity = 65_536;
        const int tagSize = 16;
        const int headerTagOffset = 148;

        var header = container.AsSpan(0, fixedHeaderSize);
        Assert.True(header[..8].SequenceEqual("SECVID03"u8));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4)));
        var prefixLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28, 4));
        var originalLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(32, 8));
        var bodyLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(40, 8));
        var encryptedOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(48, 8));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(56, 4));
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(64, 4));
        var salt = header.Slice(72, 16).ToArray();
        var noncePrefix = header.Slice(104, 8).ToArray();
        var prefix = container.AsSpan(fixedHeaderSize + publicCapacity, prefixLength).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);

        try
        {
            var headerAadBytes = header.ToArray();
            headerAadBytes.AsSpan(headerTagOffset, tagSize).Clear();
            var immutableAad = new byte[headerAadBytes.Length + prefix.Length];
            headerAadBytes.CopyTo(immutableAad, 0);
            prefix.CopyTo(immutableAad, headerAadBytes.Length);
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(
                BuildNonce(noncePrefix, 0),
                ReadOnlySpan<byte>.Empty,
                header.Slice(headerTagOffset, tagSize),
                Span<byte>.Empty,
                immutableAad);

            var immutableDigest = SHA256.HashData(immutableAad);
            var plaintext = new byte[originalLength];
            prefix.CopyTo(plaintext, 0);
            var chunks = new List<VectorChunk>();
            long processed = 0;
            long chunkIndex = 0;
            while (processed < bodyLength)
            {
                var plainLength = (int)Math.Min(chunkSize, bodyLength - processed);
                var cipherOffset = checked(encryptedOffset + chunkIndex * (chunkSize + tagSize));
                var cipher = container.AsSpan(checked((int)cipherOffset), plainLength);
                var tag = container.AsSpan(checked((int)cipherOffset) + plainLength, tagSize);
                var chunkAad = new byte[40];
                immutableDigest.CopyTo(chunkAad, 0);
                BinaryPrimitives.WriteInt64BigEndian(chunkAad.AsSpan(32, 8), chunkIndex);
                var destination = plaintext.AsSpan(prefixLength + checked((int)processed), plainLength);
                var nonce = BuildNonce(noncePrefix, checked((uint)chunkIndex + 1));
                aes.Decrypt(nonce, cipher, tag, destination, chunkAad);
                chunks.Add(new VectorChunk
                {
                    Index = chunkIndex,
                    PlaintextLength = plainLength,
                    CipherOffset = cipherOffset,
                    NonceHex = Hex(nonce),
                    AadHex = Hex(chunkAad),
                    TagHex = Hex(tag)
                });
                processed += plainLength;
                chunkIndex++;
            }

            return new ReferenceResult(prefixLength, encryptedOffset, plaintext, chunks);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] BuildNonce(ReadOnlySpan<byte> prefix, uint counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), counter);
        return nonce;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Hex(SHA256.HashData(bytes));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class FixedEntropySource : ISecvid03EntropySource
    {
        public Secvid03Entropy Create() =>
            new(
                Convert.FromHexString(SaltHex),
                Convert.FromHexString(FileIdHex),
                Convert.FromHexString(NoncePrefixHex));
    }

    private sealed record ReferenceResult(
        int OriginalHeaderLength,
        long EncryptedDataOffset,
        byte[] Plaintext,
        List<VectorChunk> Chunks);

    private sealed class VectorManifest
    {
        public int SchemaVersion { get; set; }
        public string Format { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SaltHex { get; set; } = string.Empty;
        public string FileIdHex { get; set; } = string.Empty;
        public string NoncePrefixHex { get; set; } = string.Empty;
        public long PlaintextLength { get; set; }
        public string PlaintextSha256 { get; set; } = string.Empty;
        public long ContainerLength { get; set; }
        public string ContainerSha256 { get; set; } = string.Empty;
        public string FixedHeaderHex { get; set; } = string.Empty;
        public int OriginalHeaderLength { get; set; }
        public long EncryptedDataOffset { get; set; }
        public List<VectorChunk> Chunks { get; set; } = [];
    }

    private sealed class VectorChunk : IEquatable<VectorChunk>
    {
        public long Index { get; set; }
        public int PlaintextLength { get; set; }
        public long CipherOffset { get; set; }
        public string NonceHex { get; set; } = string.Empty;
        public string AadHex { get; set; } = string.Empty;
        public string TagHex { get; set; } = string.Empty;

        public bool Equals(VectorChunk? other) =>
            other is not null &&
            Index == other.Index &&
            PlaintextLength == other.PlaintextLength &&
            CipherOffset == other.CipherOffset &&
            NonceHex == other.NonceHex &&
            AadHex == other.AadHex &&
            TagHex == other.TagHex;

        public override bool Equals(object? obj) => Equals(obj as VectorChunk);

        public override int GetHashCode() =>
            HashCode.Combine(Index, PlaintextLength, CipherOffset, NonceHex, AadHex, TagHex);
    }
}
