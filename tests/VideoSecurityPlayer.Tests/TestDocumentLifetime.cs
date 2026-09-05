using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Tests;

/// <summary>由测试显式拥有的 Host Document 关闭信号。</summary>
/// <remarks>
/// 测试必须决定何时关闭文档，生产模型不得在构造失败时悄悄创建一个永不取消的替代令牌。
/// 该类型因此既可作为普通非关闭依赖，也可主动触发关闭竞争测试。
/// </remarks>
internal sealed class TestDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();

    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;

    public void Close() => _closing.Cancel();
    public void Dispose() => _closing.Dispose();
}

/// <summary>集中组装测试所需的真实批处理对象图，不向生产代码增加便利构造函数。</summary>
internal static class TestViewModelFactory
{
    public static VideoEncryptorViewModel CreateEncryptor(
        IVideoEncryptionService service,
        TestDocumentLifetime lifetime) =>
        new(
            service,
            new VideoBatchEncryptionService(service, new OutputPathConflictResolver()),
            new SequentialVideoQueueRunner<PreparedEncryptionItem>(),
            lifetime);

    public static VideoDecryptorViewModel CreateDecryptor(
        IVideoDecryptionService service,
        TestDocumentLifetime lifetime) =>
        new(
            service,
            new SequentialVideoQueueRunner<CandidateDecryptionPreflight>(),
            lifetime);
}
