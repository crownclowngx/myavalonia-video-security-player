using System.Security.Cryptography;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

/// <summary>
/// 一次 SECVID03 加密所需的公开随机参数。
/// </summary>
internal sealed record Secvid03Entropy(byte[] Salt, byte[] FileId, byte[] NoncePrefix);

/// <summary>
/// 隔离加密器的非确定性边界，使固定格式向量可以通过真实写入链路重复生成。
/// </summary>
internal interface ISecvid03EntropySource
{
    Secvid03Entropy Create();
}

internal sealed class RandomSecvid03EntropySource : ISecvid03EntropySource
{
    public static RandomSecvid03EntropySource Instance { get; } = new();

    private RandomSecvid03EntropySource()
    {
    }

    public Secvid03Entropy Create() =>
        new(
            RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(8));
}
