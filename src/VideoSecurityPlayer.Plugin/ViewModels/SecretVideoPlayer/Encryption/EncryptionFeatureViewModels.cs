namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

/// <summary>加密队列、选择项、重试、清理和公开信息编辑的功能切片。</summary>
public sealed class EncryptionQueueViewModel(EncryptionBatchViewModel owner)
{
    public EncryptionBatchViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}
