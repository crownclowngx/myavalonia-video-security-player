namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

/// <summary>
/// 四块明文 LRU 缓存的无敏感累计统计。
/// </summary>
/// <remarks>
/// 这里只记录次数，不记录块编号、Seek 位置或任何明文字节，避免性能诊断扩大隐私边界。
/// </remarks>
public readonly record struct PlaybackCacheStatistics(
    long Requests,
    long Hits,
    long Misses,
    long Evictions);
