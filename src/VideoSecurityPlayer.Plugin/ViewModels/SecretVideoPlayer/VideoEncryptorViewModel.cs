using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 批量加密 Document 的兼容外壳；实现和状态由功能包中的
/// <see cref="EncryptionBatchViewModel"/> 统一拥有。
/// </summary>
public sealed class VideoEncryptorViewModel : EncryptionBatchViewModel, IPluginDocument
{
    private string _title = "视频文件加密器";

    public VideoEncryptorViewModel(
        IVideoEncryptionService singleFileService,
        IVideoBatchEncryptionService batchService,
        ISequentialVideoQueueRunner<PreparedEncryptionItem> queueRunner,
        IDocumentLifetime documentLifetime)
        : base(singleFileService, batchService, queueRunner, documentLifetime)
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
            // 批处理队列不是 Document 持久化内容；未知恢复输入必须显式失败。
            throw new NotSupportedException("视频文件加密器只支持新建激活。");
        }

        var title = string.IsNullOrWhiteSpace(activation.Title) ? "视频文件加密器" : activation.Title;
        if (!string.Equals(_title, title, StringComparison.Ordinal))
        {
            _title = title;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }
}
