namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

/// <summary>解密候选检查、队列选择、重试和清理的功能切片。</summary>
public sealed class DecryptionQueueViewModel(DecryptionBatchViewModel owner)
{
    public DecryptionBatchViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}
