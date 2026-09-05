using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>
/// 把当前媒体的播放快照转换为低频、非敏感的历史记录。
/// </summary>
/// <remarks>
/// 该服务是 Document-scoped：它只知道本 Document 当前媒体的公开身份，不保存密码。
/// JSON 存储是进程级单例，因此不同 Document 可以共享历史，但播放代次和清除抑制仍彼此隔离。
/// </remarks>
public sealed class PlaybackHistoryCoordinator : IDisposable
{
    private static readonly TimeSpan PeriodicSaveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongMediaThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongMediaRemainingThreshold = TimeSpan.FromSeconds(30);
    private readonly ISecureVideoPlaybackSession _session;
    private readonly IPlaybackHistoryStore _historyStore;
    private readonly object _sync = new();
    private TrackedMedia? _current;
    private DateTimeOffset _lastSavedUtc;
    private PlaybackState _lastState = PlaybackState.Empty;
    private long _suppressedGeneration;
    private bool _disposed;

    public PlaybackHistoryCoordinator(
        ISecureVideoPlaybackSession session,
        IPlaybackHistoryStore historyStore)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _session.Changed += OnPlaybackChanged;
        _historyStore.HistoryChanged += OnHistoryChanged;
    }

    /// <summary>
    /// 在媒体成功提交后登记其已认证的播放代次和扫描身份。
    /// Ready 状态本身不会创建历史；只有真正开始播放后才会落盘。
    /// </summary>
    public void Track(VideoLibraryScanResult item, long mediaGeneration)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.State != VideoLibraryMetadataState.Ready ||
            mediaGeneration <= 0 ||
            string.IsNullOrWhiteSpace(item.FileId))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
                return;
            _current = new TrackedMedia(
                Path.GetFullPath(item.FilePath),
                item.FileId,
                item.OriginalFileLength,
                mediaGeneration);
            _lastSavedUtc = default;
            _lastState = PlaybackState.Ready;
            if (_suppressedGeneration != mediaGeneration)
                _suppressedGeneration = 0;
        }
    }

    /// <summary>
    /// 清除当前媒体后，抑制本代次的定时回写，直到用户重新加载并获得新媒体代次。
    /// </summary>
    public void SuppressCurrentGeneration()
    {
        lock (_sync)
        {
            if (_current is not null)
                _suppressedGeneration = _current.MediaGeneration;
        }
    }

    /// <summary>在媒体切换前同步保存最后可观察位置。</summary>
    public void FlushCurrent()
    {
        PlaybackSnapshot snapshot;
        TrackedMedia? media;
        lock (_sync)
        {
            media = _current;
            snapshot = _session.Snapshot;
            if (media is not null && media.MediaGeneration == _suppressedGeneration)
                return;
        }
        if (media is not null)
            Save(media, snapshot);
    }

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs e)
    {
        TrackedMedia? media;
        bool shouldSave;
        lock (_sync)
        {
            if (_disposed ||
                _current is not { } current ||
                current.MediaGeneration != e.Snapshot.MediaGeneration ||
                _suppressedGeneration == current.MediaGeneration)
            {
                return;
            }

            media = current;
            var now = DateTimeOffset.UtcNow;
            var stateBoundary = e.Snapshot.State != _lastState &&
                                e.Snapshot.State is PlaybackState.Paused or
                                    PlaybackState.Stopped or
                                    PlaybackState.Ended or
                                    PlaybackState.Faulted;
            var periodic = e.Snapshot.State == PlaybackState.Playing &&
                           now - _lastSavedUtc >= PeriodicSaveInterval;
            shouldSave = stateBoundary || periodic;
            _lastState = e.Snapshot.State;
            if (shouldSave)
                _lastSavedUtc = now;
        }

        if (shouldSave)
            Save(media, e.Snapshot);
    }

    private void Save(TrackedMedia media, PlaybackSnapshot snapshot)
    {
        if (snapshot.MediaGeneration != media.MediaGeneration ||
            snapshot.DurationMs <= 0 ||
            snapshot.State is PlaybackState.Empty or PlaybackState.Ready or PlaybackState.Disposed)
        {
            return;
        }

        var position = Math.Clamp(snapshot.PositionMs, 0, snapshot.DurationMs);
        var ratio = (double)position / snapshot.DurationMs;
        var completed = snapshot.State == PlaybackState.Ended ||
                        ratio >= 0.95d ||
                        (snapshot.DurationMs >= LongMediaThreshold.TotalMilliseconds &&
                         snapshot.DurationMs - position <=
                         LongMediaRemainingThreshold.TotalMilliseconds);
        _historyStore.Upsert(new VideoPlaybackHistoryEntry(
            media.FilePath,
            media.FileId,
            media.OriginalFileLength,
            completed ? 0 : position,
            snapshot.DurationMs,
            DateTimeOffset.UtcNow,
            completed));
    }

    private void OnHistoryChanged(object? sender, PlaybackHistoryChangedEventArgs e)
    {
        if (e.Kind == PlaybackHistoryChangeKind.Upserted)
            return;

        lock (_sync)
        {
            if (_current is null)
                return;
            if (e.Kind == PlaybackHistoryChangeKind.Cleared ||
                string.Equals(
                    _current.FilePath,
                    e.FilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _suppressedGeneration = _current.MediaGeneration;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        FlushCurrent();
        _session.Changed -= OnPlaybackChanged;
        _historyStore.HistoryChanged -= OnHistoryChanged;
        GC.SuppressFinalize(this);
    }

    private sealed record TrackedMedia(
        string FilePath,
        string FileId,
        long OriginalFileLength,
        long MediaGeneration);
}
