using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using System.Runtime.InteropServices;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Secvid03Collection : ICollectionFixture<Secvid03Fixture>
{
    public const string Name = "SECVID03";
}

public sealed class Secvid03Fixture : IDisposable
{
    public const string Password = "correct-password";
    public const int FixedHeaderSize = 256;
    public const int PublicRegionSize = 64 * 1024;
    public const int OriginalPrefixSize = 32;
    public const int ChunkSize = 1024 * 1024;

    public Secvid03Fixture()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "VideoSecurityPlayer-Secvid03Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        OriginalPath = Path.Combine(DirectoryPath, "原始😀视频.mp4");
        EncryptedPath = Path.Combine(DirectoryPath, "video.secvid");

        OriginalBytes = new byte[ChunkSize * 5 + 12_345];
        new Random(20260721).NextBytes(OriginalBytes);
        OriginalBytes[0] = 0;
        OriginalBytes[1] = 0;
        OriginalBytes[2] = 0;
        OriginalBytes[3] = 32;
        "ftyp"u8.CopyTo(OriginalBytes.AsSpan(4));
        File.WriteAllBytes(OriginalPath, OriginalBytes);

        new Secvid03Encryptor().EncryptAsync(
            OriginalPath, EncryptedPath, Password, "初始标题", "第一行\nSecond line 😀").GetAwaiter().GetResult();
    }

    public string DirectoryPath { get; }
    public string OriginalPath { get; }
    public string EncryptedPath { get; }
    public byte[] OriginalBytes { get; }

    public string CopyEncrypted(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.Copy(EncryptedPath, path, true);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, true); }
        catch { }
    }
}

[Collection(Secvid03Collection.Name)]
public sealed class Secvid03Tests(Secvid03Fixture fixture)
{
    [Fact]
    public void LibVlcRuntime_UsesPluginLocalWindowsX64Directory()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var runtime = new LibVlcRuntime();
        Assert.EndsWith(Path.Combine("native", "win-x64", "libvlc"), runtime.RuntimeDirectory);
        Assert.True(File.Exists(Path.Combine(runtime.RuntimeDirectory, "libvlc.dll")));
        runtime.EnsureInitialized();
        using var libVlc = new LibVLC("--no-video-title-show");
    }

    [Fact]
    public void PublicInfo_RoundTripsUnicodeAndUpdatesOnlyFixedRegion()
    {
        var path = fixture.CopyEncrypted("metadata.secvid");
        var before = File.ReadAllBytes(path);
        var title = new string('题', 200);
        var description = string.Concat(Enumerable.Repeat("😀", 10_000));

        EncryptedVideoContainer.UpdatePublicInfo(path, title, description);
        var info = EncryptedVideoContainer.ReadPublicInfo(path);
        var after = File.ReadAllBytes(path);

        Assert.Equal(3, info.Version);
        Assert.Equal(Path.GetFileName(fixture.OriginalPath), info.OriginalFileName);
        Assert.Equal(".mp4", info.OriginalExtension);
        Assert.Equal(title, info.Title);
        Assert.Equal(description, info.Description);
        Assert.Equal(fixture.OriginalBytes.LongLength, info.OriginalFileLength);
        Assert.True(before.AsSpan(0, Secvid03Fixture.FixedHeaderSize)
            .SequenceEqual(after.AsSpan(0, Secvid03Fixture.FixedHeaderSize)));
        var bodyOffset = Secvid03Fixture.FixedHeaderSize + Secvid03Fixture.PublicRegionSize;
        Assert.True(before.AsSpan(bodyOffset).SequenceEqual(after.AsSpan(bodyOffset)));
    }

    [Fact]
    public void PublicInfo_AcceptsEmptyAndExactLimits_RejectsOverLimitAndControls()
    {
        var path = fixture.CopyEncrypted("limits.secvid");
        EncryptedVideoContainer.UpdatePublicInfo(path, string.Empty, string.Empty);
        var emptyInfo = EncryptedVideoContainer.ReadPublicInfo(path);
        Assert.Empty(emptyInfo.Title);
        Assert.Empty(emptyInfo.Description);

        EncryptedVideoContainer.UpdatePublicInfo(path, string.Empty, new string('中', 10_000));
        var info = EncryptedVideoContainer.ReadPublicInfo(path);
        Assert.Empty(info.Title);
        Assert.Equal(10_000, EncryptedVideoContainer.CountRunes(info.Description));

        Assert.Throws<ArgumentException>(() =>
            EncryptedVideoContainer.UpdatePublicInfo(path, new string('a', 201), string.Empty));
        Assert.Throws<ArgumentException>(() =>
            EncryptedVideoContainer.UpdatePublicInfo(path, string.Empty, new string('a', 10_001)));
        Assert.Throws<ArgumentException>(() =>
            EncryptedVideoContainer.UpdatePublicInfo(path, "bad\0title", string.Empty));
    }

    [Fact]
    public void SeekableStream_SequentialAndRandomReadsMatchOriginal()
    {
        using var stream = SeekableEncryptedVideoStream.Open(fixture.EncryptedPath, Secvid03Fixture.Password);
        Assert.True(stream.CanSeek);
        Assert.Equal(fixture.OriginalBytes.LongLength, stream.Length);

        var probes = new (long Position, int Length)[]
        {
            (0, 64),
            (Secvid03Fixture.OriginalPrefixSize - 7, 64),
            (Secvid03Fixture.OriginalPrefixSize + Secvid03Fixture.ChunkSize - 19, 80),
            (Secvid03Fixture.OriginalPrefixSize + Secvid03Fixture.ChunkSize * 2L - 10, 50),
            (Secvid03Fixture.OriginalPrefixSize + Secvid03Fixture.ChunkSize * 4L + 123, 256),
            (fixture.OriginalBytes.LongLength - 20, 100)
        };

        foreach (var probe in probes)
        {
            stream.Seek(probe.Position, SeekOrigin.Begin);
            var actual = new byte[probe.Length];
            var read = stream.Read(actual);
            var expectedLength = (int)Math.Min(probe.Length, fixture.OriginalBytes.LongLength - probe.Position);
            Assert.Equal(expectedLength, read);
            Assert.True(fixture.OriginalBytes.AsSpan((int)probe.Position, read).SequenceEqual(actual.AsSpan(0, read)));
        }

        stream.Position = 0;
        using var output = new MemoryStream();
        stream.CopyTo(output, 73_177);
        Assert.Equal(fixture.OriginalBytes, output.ToArray());
    }

    [Fact]
    public void SeekableStream_ReusesFixedBuffersAcrossRepeatedCacheMisses()
    {
        using var stream = SeekableEncryptedVideoStream.Open(
            fixture.EncryptedPath,
            Secvid03Fixture.Password);
        var plaintextBuffers = stream.PlaintextBuffers.ToArray();
        var destination = new byte[1];

        Assert.Equal(4, plaintextBuffers.Length);
        Assert.Equal(4, plaintextBuffers.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(plaintextBuffers, buffer => Assert.Equal(Secvid03Fixture.ChunkSize, buffer.Length));
        Assert.Equal(Secvid03Fixture.ChunkSize, stream.CipherBuffer.Length);

        for (var iteration = 0; iteration < 40; iteration++)
        {
            var chunkIndex = iteration % 5;
            var position = Secvid03Fixture.OriginalPrefixSize +
                           chunkIndex * (long)Secvid03Fixture.ChunkSize +
                           127;
            stream.Position = position;
            Assert.Equal(1, stream.Read(destination));
            Assert.Equal(fixture.OriginalBytes[position], destination[0]);
        }

        Assert.Equal(plaintextBuffers, stream.PlaintextBuffers);
    }

    [Fact]
    public void SeekableStream_RepeatedCacheMissesHaveBoundedManagedAllocation()
    {
        using var stream = SeekableEncryptedVideoStream.Open(
            fixture.EncryptedPath,
            Secvid03Fixture.Password);
        var destination = new byte[1];

        // 预热 JIT、AES-GCM 和所有缓存槽，测量阶段只覆盖持续播放时的稳定热路径。
        for (var chunkIndex = 0; chunkIndex < 5; chunkIndex++)
        {
            stream.Position = Secvid03Fixture.OriginalPrefixSize +
                              chunkIndex * (long)Secvid03Fixture.ChunkSize;
            Assert.Equal(1, stream.Read(destination));
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var chunkIndex = iteration % 5;
            stream.Position = Secvid03Fixture.OriginalPrefixSize +
                              chunkIndex * (long)Secvid03Fixture.ChunkSize +
                              311;
            Assert.Equal(1, stream.Read(destination));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // 旧实现会为 128 次缓存未命中分配约 256 MiB；固定槽实现应只产生少量密码学对象。
        Assert.True(
            allocated < 16L * 1024 * 1024,
            $"稳定读取阶段分配过多: {allocated / (1024d * 1024):F2} MiB");
    }

    [Fact]
    public void SeekableStream_DisposeClearsAllOwnedPlaybackBuffers()
    {
        var stream = SeekableEncryptedVideoStream.Open(
            fixture.EncryptedPath,
            Secvid03Fixture.Password);
        var plaintextBuffers = stream.PlaintextBuffers.ToArray();
        var cipherBuffer = stream.CipherBuffer;
        var destination = new byte[1];

        for (var chunkIndex = 0; chunkIndex < 5; chunkIndex++)
        {
            stream.Position = Secvid03Fixture.OriginalPrefixSize +
                              chunkIndex * (long)Secvid03Fixture.ChunkSize;
            Assert.Equal(1, stream.Read(destination));
        }
        Assert.Contains(
            plaintextBuffers,
            buffer => buffer.AsSpan().IndexOfAnyExcept((byte)0) >= 0);

        stream.Dispose();

        Assert.All(
            plaintextBuffers,
            buffer => Assert.Equal(-1, buffer.AsSpan().IndexOfAnyExcept((byte)0)));
        Assert.Equal(-1, cipherBuffer.AsSpan().IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void SeekableStream_AuthenticationFailureClearsTheCandidateSlot()
    {
        var path = fixture.CopyEncrypted("slot-authentication-failure.secvid");
        var container = File.ReadAllBytes(path);
        var header = Secvid03Format.ParseHeader(
            container.AsSpan(0, Secvid03Format.FixedHeaderSize),
            container.LongLength);
        FlipByte(path, header.EncryptedDataOffset + header.ChunkSize);

        using var stream = SeekableEncryptedVideoStream.Open(path, Secvid03Fixture.Password);
        var plaintextBuffers = stream.PlaintextBuffers.ToArray();
        stream.Position = header.OriginalHeaderLength;

        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[1]));
        Assert.All(
            plaintextBuffers,
            buffer => Assert.Equal(-1, buffer.AsSpan().IndexOfAnyExcept((byte)0)));
    }

    [Fact]
    public void Authentication_RejectsWrongPasswordHeaderAndCiphertextTampering()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SeekableEncryptedVideoStream.Open(fixture.EncryptedPath, "wrong-password"));

        var headerPath = fixture.CopyEncrypted("header-tampered.secvid");
        FlipByte(headerPath, 88);
        Assert.Throws<UnauthorizedAccessException>(() =>
            SeekableEncryptedVideoStream.Open(headerPath, Secvid03Fixture.Password));

        var cipherPath = fixture.CopyEncrypted("cipher-tampered.secvid");
        var cipherOffset = Secvid03Fixture.FixedHeaderSize + Secvid03Fixture.PublicRegionSize + Secvid03Fixture.OriginalPrefixSize;
        FlipByte(cipherPath, cipherOffset + 123);
        using var stream = SeekableEncryptedVideoStream.Open(cipherPath, Secvid03Fixture.Password);
        stream.Position = Secvid03Fixture.OriginalPrefixSize;
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[1]));

        var tagPath = fixture.CopyEncrypted("tag-tampered.secvid");
        FlipByte(tagPath, cipherOffset + Secvid03Fixture.ChunkSize);
        using var tagStream = SeekableEncryptedVideoStream.Open(tagPath, Secvid03Fixture.Password);
        tagStream.Position = Secvid03Fixture.OriginalPrefixSize;
        Assert.Throws<InvalidDataException>(() => tagStream.Read(new byte[1]));
    }

    [Fact]
    public void CorruptPublicCrc_DoesNotPreventPasswordVerificationOrPlaybackReads()
    {
        var path = fixture.CopyEncrypted("bad-public-crc.secvid");
        FlipByte(path, Secvid03Fixture.FixedHeaderSize + 28);
        Assert.Throws<InvalidDataException>(() => EncryptedVideoContainer.ReadPublicInfo(path));

        using (var stream = SeekableEncryptedVideoStream.Open(path, Secvid03Fixture.Password))
        {
            var actual = new byte[128];
            Assert.Equal(actual.Length, stream.Read(actual));
            Assert.True(fixture.OriginalBytes.AsSpan(0, actual.Length).SequenceEqual(actual));
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RepeatedOpenReadDispose_ReleasesTheContainerHandle()
    {
        var path = fixture.CopyEncrypted("repeated.secvid");
        for (var i = 0; i < 10; i++)
        {
            using var stream = SeekableEncryptedVideoStream.Open(path, Secvid03Fixture.Password);
            stream.Position = i * 97_531L % stream.Length;
            Assert.True(stream.Read(new byte[257]) > 0);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x80));
    }
}
