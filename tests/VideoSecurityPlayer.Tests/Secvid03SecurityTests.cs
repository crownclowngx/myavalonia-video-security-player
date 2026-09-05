using System.Buffers.Binary;
using System.Security.Cryptography;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[Collection(Secvid03Collection.Name)]
public sealed class Secvid03SecurityTests(Secvid03Fixture fixture)
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(40, 40, 0)]
    [InlineData(1_048_576, 0, 1)]
    [InlineData(1_048_577, 0, 2)]
    [InlineData(1_048_616, 40, 1)]
    public void Layout_CalculatesEmptyExactAndTailBoundaries(
        long originalLength,
        int prefixLength,
        long expectedChunks)
    {
        var layout = Secvid03Format.CalculateLayout(originalLength, prefixLength);

        Assert.Equal(expectedChunks, layout.ChunkCount);
        Assert.Equal(originalLength - prefixLength, layout.PlainBodyLength);
        Assert.Equal(Secvid03Format.OriginalHeaderOffset + prefixLength, layout.EncryptedDataOffset);
        Assert.Equal(
            layout.EncryptedDataOffset + layout.PlainBodyLength + expectedChunks * Secvid03Format.TagSize,
            layout.PhysicalFileLength);
    }

    [Fact]
    public void Layout_AcceptsMaximumNonceRangeAndRejectsNextChunk()
    {
        var maximumBodyLength = checked((long)uint.MaxValue * Secvid03Format.ChunkSize);
        var maximum = Secvid03Format.CalculateLayout(maximumBodyLength + 40, 40);

        Assert.Equal((long)uint.MaxValue, maximum.ChunkCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Secvid03Format.CalculateLayout(
                checked(maximumBodyLength + Secvid03Format.ChunkSize + 40),
                40));
    }

    [Fact]
    public void Layout_RejectsOversizedPrefixAndArithmeticOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Secvid03Format.CalculateLayout(41, 41));
        Assert.Throws<ArgumentOutOfRangeException>(() => Secvid03Format.CalculateLayout(int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Secvid03Format.CalculateLayout(long.MaxValue, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(1_048_608)]
    [InlineData(1_048_609)]
    public async Task EncryptAndRead_RoundTripsEmptyShortExactAndTailLengths(int length)
    {
        var input = Path.Combine(fixture.DirectoryPath, $"boundary-{length}.mp4");
        var encrypted = Path.Combine(fixture.DirectoryPath, $"boundary-{length}.secvid");
        var expected = new byte[length];
        for (var index = 0; index < expected.Length; index++)
            expected[index] = (byte)(index % 251);
        if (expected.Length >= 8)
            "ftyp"u8.CopyTo(expected.AsSpan(4, 4));
        await File.WriteAllBytesAsync(input, expected);

        await new Secvid03Encryptor().EncryptAsync(
            input,
            encrypted,
            Secvid03Fixture.Password,
            string.Empty,
            string.Empty);

        using var stream = SeekableEncryptedVideoStream.Open(encrypted, Secvid03Fixture.Password);
        using var actual = new MemoryStream();
        stream.CopyTo(actual);
        Assert.Equal(expected, actual.ToArray());
    }

    [Fact]
    public void HeaderParser_RejectsPhysicalTruncationAndTrailingBytes()
    {
        var container = File.ReadAllBytes(fixture.EncryptedPath);
        var header = container.AsSpan(0, Secvid03Format.FixedHeaderSize).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            Secvid03Format.ParseHeader(header, container.LongLength - 1));
        Assert.Throws<InvalidDataException>(() =>
            Secvid03Format.ParseHeader(header, container.LongLength + 1));
    }

    [Fact]
    public void FixedHeader_EverySingleByteMutationIsStructurallyOrCryptographicallyRejected()
    {
        var container = File.ReadAllBytes(fixture.EncryptedPath);
        var originalHeaderBytes = container.AsSpan(0, Secvid03Format.FixedHeaderSize).ToArray();
        var originalHeader = Secvid03Format.ParseHeader(originalHeaderBytes, container.LongLength);
        var prefix = container.AsSpan(
            Secvid03Format.OriginalHeaderOffset,
            originalHeader.OriginalHeaderLength).ToArray();
        var key = Secvid03Cryptography.DeriveKey(Secvid03Fixture.Password, originalHeader);

        try
        {
            for (var offset = 0; offset < originalHeaderBytes.Length; offset++)
            {
                var mutated = originalHeaderBytes.ToArray();
                mutated[offset] ^= 0x80;

                try
                {
                    var parsed = Secvid03Format.ParseHeader(mutated, container.LongLength);
                    Assert.Throws<Secvid03AuthenticationException>(() =>
                        Secvid03Cryptography.VerifyHeaderAndCreateDigest(parsed, prefix, key));
                }
                catch (InvalidDataException)
                {
                    // Canonical constants, lengths, UTF-8 or reserved bytes can fail before authentication.
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void OriginalPrefix_EverySingleByteMutationFailsHeaderAuthentication()
    {
        var container = File.ReadAllBytes(fixture.EncryptedPath);
        var header = Secvid03Format.ParseHeader(
            container.AsSpan(0, Secvid03Format.FixedHeaderSize),
            container.LongLength);
        var prefix = container.AsSpan(
            Secvid03Format.OriginalHeaderOffset,
            header.OriginalHeaderLength).ToArray();
        var key = Secvid03Cryptography.DeriveKey(Secvid03Fixture.Password, header);

        try
        {
            for (var offset = 0; offset < prefix.Length; offset++)
            {
                var mutated = prefix.ToArray();
                mutated[offset] ^= 0x80;
                Assert.Throws<Secvid03AuthenticationException>(() =>
                    Secvid03Cryptography.VerifyHeaderAndCreateDigest(header, mutated, key));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void AuthenticationContext_DisposeClearsKeyAndDigest()
    {
        var container = File.ReadAllBytes(fixture.EncryptedPath);
        var header = Secvid03Format.ParseHeader(
            container.AsSpan(0, Secvid03Format.FixedHeaderSize),
            container.LongLength);
        var prefix = container.AsSpan(
            Secvid03Format.OriginalHeaderOffset,
            header.OriginalHeaderLength);
        var context = Secvid03Cryptography.Authenticate(Secvid03Fixture.Password, header, prefix);
        var key = context.Key;
        var digest = context.ImmutableDigest;

        context.Dispose();

        Assert.All(key, value => Assert.Equal(0, value));
        Assert.All(digest, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ChunkAuthenticationFailure_ClearsDestinationBuffer()
    {
        var container = File.ReadAllBytes(fixture.EncryptedPath);
        var header = Secvid03Format.ParseHeader(
            container.AsSpan(0, Secvid03Format.FixedHeaderSize),
            container.LongLength);
        var prefix = container.AsSpan(
            Secvid03Format.OriginalHeaderOffset,
            header.OriginalHeaderLength);
        using var context = Secvid03Cryptography.Authenticate(Secvid03Fixture.Password, header, prefix);
        var cipher = container.AsSpan((int)header.EncryptedDataOffset, header.ChunkSize).ToArray();
        var tag = container.AsSpan(
            (int)header.EncryptedDataOffset + header.ChunkSize,
            Secvid03Format.TagSize).ToArray();
        tag[0] ^= 0x80;
        var plain = Enumerable.Repeat((byte)0x5A, header.ChunkSize).ToArray();

        Assert.Throws<Secvid03ContentAuthenticationException>(() =>
            Secvid03Cryptography.DecryptChunk(context, 0, cipher, tag, plain));
        Assert.All(plain, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task MalformedPrefixLength_IsRejectedBeforeKdfAndCreatesNoOutput()
    {
        var path = fixture.CopyEncrypted("oversized-prefix.secvid");
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), int.MaxValue);
        await File.WriteAllBytesAsync(path, bytes);
        var outputDirectory = Path.Combine(fixture.DirectoryPath, "must-not-exist");
        var outputPath = Path.Combine(outputDirectory, "output.mp4");

        Assert.Throws<InvalidDataException>(() =>
            SeekableEncryptedVideoStream.Open(path, string.Empty));

        var error = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Decryptor().DecryptAsync(
                path,
                outputPath,
                Secvid03Fixture.Password));

        Assert.Equal(VideoTaskFailureCode.InvalidFormat, error.FailureCode);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.partial-*"));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        File.Delete(path);
    }

    [Fact]
    public void PublicPaddingDamage_DoesNotPreventAuthenticatedPlayback()
    {
        var path = fixture.CopyEncrypted("public-padding.secvid");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = Secvid03Format.FixedHeaderSize + Secvid03Format.PublicInfoCapacity - 1;
            file.WriteByte(0x5A);
        }

        Assert.Throws<InvalidDataException>(() => EncryptedVideoContainer.ReadPublicInfo(path));
        using var stream = SeekableEncryptedVideoStream.Open(path, Secvid03Fixture.Password);
        var actual = new byte[128];
        Assert.Equal(actual.Length, stream.Read(actual));
        Assert.True(fixture.OriginalBytes.AsSpan(0, actual.Length).SequenceEqual(actual));
    }

    [Fact]
    public void PublicRewrite_ShorterPayloadClearsAllUnusedBytes()
    {
        var path = fixture.CopyEncrypted("public-cleared.secvid");
        EncryptedVideoContainer.UpdatePublicInfo(path, new string('题', 200), new string('旧', 10_000));
        EncryptedVideoContainer.UpdatePublicInfo(path, "短标题", "短描述");

        var bytes = File.ReadAllBytes(path);
        var publicRegion = bytes.AsSpan(
            Secvid03Format.FixedHeaderSize,
            Secvid03Format.PublicInfoCapacity);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(publicRegion.Slice(12, 4));

        Assert.All(publicRegion[totalLength..].ToArray(), value => Assert.Equal(0, value));
        var info = EncryptedVideoContainer.ReadPublicInfo(path);
        Assert.Equal("短标题", info.Title);
        Assert.Equal("短描述", info.Description);
    }

    [Fact]
    public void SwappingAuthenticatedBlocks_IsRejectedByChunkIndexAad()
    {
        var path = fixture.CopyEncrypted("swapped-blocks.secvid");
        var bytes = File.ReadAllBytes(path);
        var header = Secvid03Format.ParseHeader(
            bytes.AsSpan(0, Secvid03Format.FixedHeaderSize),
            bytes.LongLength);
        var physicalBlockLength = header.ChunkSize + Secvid03Format.TagSize;
        var firstOffset = checked((int)header.EncryptedDataOffset);
        var secondOffset = firstOffset + physicalBlockLength;
        var first = bytes.AsSpan(firstOffset, physicalBlockLength).ToArray();
        bytes.AsSpan(secondOffset, physicalBlockLength).CopyTo(bytes.AsSpan(firstOffset, physicalBlockLength));
        first.CopyTo(bytes.AsSpan(secondOffset, physicalBlockLength));
        File.WriteAllBytes(path, bytes);

        using var stream = SeekableEncryptedVideoStream.Open(path, Secvid03Fixture.Password);
        stream.Position = header.OriginalHeaderLength;
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[1]));
    }

    [Fact]
    public async Task CopyingCiphertextFromAnotherFile_IsRejectedByImmutableAad()
    {
        var secondContainer = Path.Combine(fixture.DirectoryPath, "second-container.secvid");
        await new Secvid03Encryptor().EncryptAsync(
            fixture.OriginalPath,
            secondContainer,
            Secvid03Fixture.Password,
            "第二个文件",
            string.Empty);

        var targetPath = fixture.CopyEncrypted("cross-file-block.secvid");
        var target = File.ReadAllBytes(targetPath);
        var source = File.ReadAllBytes(secondContainer);
        var targetHeader = Secvid03Format.ParseHeader(
            target.AsSpan(0, Secvid03Format.FixedHeaderSize),
            target.LongLength);
        var sourceHeader = Secvid03Format.ParseHeader(
            source.AsSpan(0, Secvid03Format.FixedHeaderSize),
            source.LongLength);
        Assert.Equal(targetHeader.ChunkSize, sourceHeader.ChunkSize);
        var physicalBlockLength = targetHeader.ChunkSize + Secvid03Format.TagSize;
        source.AsSpan((int)sourceHeader.EncryptedDataOffset, physicalBlockLength)
            .CopyTo(target.AsSpan((int)targetHeader.EncryptedDataOffset, physicalBlockLength));
        await File.WriteAllBytesAsync(targetPath, target);

        using var stream = SeekableEncryptedVideoStream.Open(targetPath, Secvid03Fixture.Password);
        stream.Position = targetHeader.OriginalHeaderLength;
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[1]));
    }
}
