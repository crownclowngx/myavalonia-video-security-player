using System.Security.Cryptography;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class RealMediaAssetTests
{
    private static readonly string AssetDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "TestAssets",
        "RealMedia");

    public static IEnumerable<object[]> AssetFileNames =>
        LoadManifest().Assets.Select(asset => new object[] { asset.FileName });

    [Theory]
    [MemberData(nameof(AssetFileNames))]
    public void Asset_MatchesManifestIntegrityAndContainerSignature(string fileName)
    {
        var manifest = LoadManifest();
        var asset = Assert.Single(manifest.Assets, item => item.FileName == fileName);
        Assert.Equal(Path.GetFileName(asset.FileName), asset.FileName);
        Assert.True(asset.ExpectedDurationMs > 0, $"{fileName} 缺少有效的预期时长。");
        Assert.False(string.IsNullOrWhiteSpace(asset.SourceDescription));
        Assert.Equal("CC0-1.0", asset.SpdxLicense);
        Assert.False(string.IsNullOrWhiteSpace(asset.GenerationCommand));

        var path = Path.Combine(AssetDirectory, asset.FileName);
        Assert.True(File.Exists(path), $"清单中的真实媒体不存在: {asset.FileName}");

        var fileInfo = new FileInfo(path);
        Assert.Equal(asset.ByteLength, fileInfo.Length);
        using (var stream = File.OpenRead(path))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            Assert.Equal(asset.Sha256, actualHash);
        }

        Span<byte> signature = stackalloc byte[12];
        using var signatureStream = File.OpenRead(path);
        Assert.Equal(signature.Length, signatureStream.Read(signature));
        if (asset.Container == "mp4")
        {
            Assert.Equal("ftyp"u8.ToArray(), signature.Slice(4, 4).ToArray());
        }
        else if (asset.Container == "webm")
        {
            Assert.Equal(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, signature.Slice(0, 4).ToArray());
        }
        else
        {
            Assert.Fail($"清单声明了未受支持的容器类型: {asset.Container}");
        }
    }

    [Fact]
    public void Manifest_CoversG0MatrixAndHasNoUndeclaredMedia()
    {
        var manifest = LoadManifest();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("FFmpeg", manifest.Generator.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Generator.Version));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Generator.Source));

        var declaredFiles = manifest.Assets
            .Select(asset => asset.FileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualFiles = Directory.EnumerateFiles(AssetDirectory)
            .Where(path => Path.GetExtension(path) is ".mp4" or ".webm")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declaredFiles, actualFiles);
        Assert.Contains(manifest.Assets, asset => asset.Container == "mp4");
        Assert.Contains(manifest.Assets, asset => asset.Container == "webm");
        Assert.Contains(manifest.Assets, asset => asset.HasAudio);
        Assert.Contains(manifest.Assets, asset => !asset.HasAudio);
        Assert.Contains(manifest.Assets, asset => asset.ExpectedDurationMs <= 3_000);
        Assert.Contains(manifest.Assets, asset => asset.ByteLength > 2L * Secvid03Format.ChunkSize);
    }

    private static RealMediaManifest LoadManifest()
    {
        var path = Path.Combine(AssetDirectory, "manifest.json");
        Assert.True(File.Exists(path), $"真实媒体清单不存在: {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RealMediaManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("真实媒体清单不是有效 JSON。");
    }

    private sealed record RealMediaManifest(
        int SchemaVersion,
        GeneratorInfo Generator,
        RealMediaAsset[] Assets);

    private sealed record GeneratorInfo(string Name, string Version, string Source);

    private sealed record RealMediaAsset(
        string FileName,
        string Purpose,
        string Container,
        string VideoCodec,
        string? AudioCodec,
        int Width,
        int Height,
        bool HasAudio,
        long ExpectedDurationMs,
        long ByteLength,
        string Sha256,
        string SourceDescription,
        string SpdxLicense,
        string GenerationCommand);
}
