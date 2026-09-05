using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;

public sealed record VideoEncryptionRequest(
    string InputPath,
    string OutputPath,
    string PublicTitle,
    string PublicDescription);

public interface ISecvid03Encryptor
{
    Task EncryptAsync(
        VideoEncryptionRequest request,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IVideoEncryptionService
{
    Task<VideoPreflightResult> PreflightAsync(
        VideoEncryptionRequest request,
        CancellationToken cancellationToken = default);

    Task EncryptAsync(
        VideoEncryptionRequest request,
        string password,
        IProgress<VideoTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
