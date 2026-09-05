using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 批量解密 Document 的兼容外壳；实现和状态由功能包中的
/// <see cref="DecryptionBatchViewModel"/> 统一拥有。
/// </summary>
public sealed class VideoDecryptorViewModel : DecryptionBatchViewModel, IPluginDocument
{
    private string _title = "批量视频解密器";

    public VideoDecryptorViewModel(
        IVideoDecryptionService decryptionService,
        ISequentialVideoQueueRunner<CandidateDecryptionPreflight> queueRunner,
        IDocumentLifetime documentLifetime)
        : base(decryptionService, queueRunner, documentLifetime)
    {
    }

    public DocumentPresentationState Presentation => new(_title);

    public event EventHandler? PresentationChanged;

    public ValueTask InitializeAsync(
        DocumentActivation activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation)
        {
            // 解密候选与执行队列不进入 Document envelope，恢复输入不能被当作空队列新建。
            throw new NotSupportedException("批量视频解密器只支持新建激活。");
        }

        var title = string.IsNullOrWhiteSpace(activation.Title) ? "批量视频解密器" : activation.Title;
        if (!string.Equals(_title, title, StringComparison.Ordinal))
        {
            _title = title;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }
}
