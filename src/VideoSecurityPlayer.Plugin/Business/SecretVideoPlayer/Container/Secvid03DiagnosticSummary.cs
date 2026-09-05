namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

/// <summary>
/// 已通过固定头认证的 SECVID03 结构摘要。
/// </summary>
/// <remarks>
/// 本类型刻意不保存路径、文件名、FileId、公开元数据、salt、nonce 或认证标签。
/// 它只回答排障所需的结构问题，不能用于识别某个用户媒体。
/// </remarks>
internal sealed record Secvid03DiagnosticSummary(
    string Format,
    int Version,
    int OriginalHeaderLength,
    long OriginalFileLength,
    int ChunkSize,
    long ChunkCount,
    int TagSize,
    string Kdf,
    int KdfIterations);
