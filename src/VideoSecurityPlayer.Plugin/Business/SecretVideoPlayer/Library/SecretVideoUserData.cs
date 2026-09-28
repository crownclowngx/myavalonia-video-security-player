using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>跨新建播放器 Document 复用的非敏感播放偏好。</summary>
public sealed record PlaybackPreferences(int Volume, float Rate)
{
    public static PlaybackPreferences Default { get; } = new(50, 1.0f);
}

/// <summary>媒体库在下次创建 Document 时恢复的浏览设置。</summary>
public sealed record VideoLibrarySettings(
    string RecentFolder,
    bool IncludeSubdirectories,
    VideoLibrarySortField SortField,
    VideoLibrarySortDirection SortDirection,
    VideoLibraryStatusFilter StatusFilter,
    bool IsLibraryPaneOpen,
    bool IsLibrarySettingsExpanded = false,
    IReadOnlyList<string>? RecentFolders = null,
    double LibraryPaneWidth = 400)
{
    public static VideoLibrarySettings Default { get; } = new(
        string.Empty,
        false,
        VideoLibrarySortField.FileName,
        VideoLibrarySortDirection.Ascending,
        VideoLibraryStatusFilter.All,
        true,
        false);
}

/// <summary>
/// 一条允许落盘的播放历史。它只包含恢复所需的文件身份和时间信息。
/// </summary>
/// <remarks>
/// 路径和播放位置是当前用户可读的明文隐私数据；密码、标题、描述、轨道和任何解密材料
/// 都不属于该模型。FileId 只是索引键，媒体加载仍必须通过密码认证。
/// </remarks>
public sealed record VideoPlaybackHistoryEntry(
    string FilePath,
    string FileId,
    long OriginalFileLength,
    long PositionMs,
    long DurationMs,
    DateTimeOffset LastPlayedUtc,
    bool IsCompleted)
{
    public VideoPlaybackHistoryState State =>
        IsCompleted ? VideoPlaybackHistoryState.Completed : VideoPlaybackHistoryState.InProgress;
}

public enum PlaybackHistoryChangeKind
{
    Upserted,
    Removed,
    Cleared
}

public sealed class PlaybackHistoryChangedEventArgs(
    PlaybackHistoryChangeKind kind,
    string? filePath = null) : EventArgs
{
    public PlaybackHistoryChangeKind Kind { get; } = kind;
    public string? FilePath { get; } = filePath;
}

/// <summary>播放器只依赖这一小组偏好，不接触目录路径或播放历史。</summary>
public interface IPlaybackPreferenceStore
{
    PlaybackPreferences CurrentPreferences { get; }

    void UpdatePreferences(PlaybackPreferences preferences);
}

/// <summary>媒体库浏览设置的独立持久化端口。</summary>
public interface IVideoLibrarySettingsStore
{
    VideoLibrarySettings CurrentSettings { get; }

    void UpdateSettings(VideoLibrarySettings settings);
}

/// <summary>播放历史查询和清除端口。</summary>
public interface IPlaybackHistoryStore
{
    event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;

    VideoPlaybackHistoryEntry? Find(
        string filePath,
        string fileId,
        long originalFileLength);

    IReadOnlyList<VideoPlaybackHistoryEntry> GetAll();

    void Upsert(VideoPlaybackHistoryEntry entry);

    void Remove(string filePath, string fileId, long originalFileLength);

    void Clear();
}

/// <summary>向 UI 暴露不含路径和异常文本的用户数据恢复状态。</summary>
public interface ISecretVideoUserDataDiagnostics
{
    string LoadWarning { get; }
}

/// <summary>唯一用户数据存储：窄端口共享数据所有权，磁盘提交由一个写入器串行完成。</summary>
/// <remarks>
/// 最近使用值可以先在内存更新；新文档默认配置只读取已提交快照。准备临时文件期间允许
/// 新修改进入，最终替换前在同一锁内复核修订号，旧快照不能覆盖更新后的状态。
/// </remarks>
public sealed class SecretVideoUserDataStore : IPlaybackPreferenceStore, IVideoLibrarySettingsStore,
    IPlaybackHistoryStore, ISecretVideoUserDataDiagnostics, IVideoSettingsReader, IVideoSettingsEditor,
    IUserDataMaintenance, IPlaybackHistoryWriter, IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _filePath;
    private readonly IUserDataFileWriter _writer;
    private readonly bool _readOnly;
    private UserDataDocument _document;
    private VideoSettings _committedSettings;
    private CancellationTokenSource? _scheduledWrite;
    private long _revision;
    private long _savedRevision;
    private long _recordingRevision;
    private bool _recordingEnabled;
    private bool _disposed;

    public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;
    public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryInvalidated;
    public event EventHandler? SettingsChanged;
    public string LoadWarning { get; }
    public PlaybackPreferences CurrentPreferences { get { lock (_sync) return _document.Preferences; } }
    public VideoLibrarySettings CurrentSettings { get { lock (_sync) return _document.LibrarySettings; } }
    public VideoSettings CurrentVideoSettings { get { lock (_sync) return _committedSettings; } }

    public SecretVideoUserDataStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyAvaloniaManagement",
        "MySmallTools", "secret-video-player", "user-data-v1.json")) { }

    internal SecretVideoUserDataStore(string filePath, IUserDataFileWriter? writer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _writer = writer ?? new UserDataFileWriter();
        _document = UserDataCodec.Load(_filePath, out var warning, out _readOnly);
        LoadWarning = warning;
        _committedSettings = _document.Settings!;
        _recordingEnabled = _committedSettings.RecordHistory;
    }

    public void UpdatePreferences(PlaybackPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_readOnly) return;
            var value = UserDataCodec.Normalize(preferences);
            if (_document.Preferences == value) return;
            _document = _document with { Preferences = value };
            ChangedUnderLock();
        }
    }

    public void UpdateSettings(VideoLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_readOnly) return;
            _document = _document with { LibrarySettings = UserDataCodec.Normalize(settings) };
            ChangedUnderLock();
        }
    }

    public VideoPlaybackHistoryEntry? Find(string filePath, string fileId, long originalFileLength)
    {
        var key = UserDataCodec.Key(filePath, fileId, originalFileLength);
        lock (_sync) return _document.History.FirstOrDefault(e =>
            string.Equals(UserDataCodec.Key(e), key, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<VideoPlaybackHistoryEntry> GetAll()
    {
        lock (_sync) return _document.History.ToArray();
    }

    public RecordingToken CaptureRecordingToken()
    {
        lock (_sync) return new(_recordingRevision, _recordingEnabled && !_readOnly && !_disposed);
    }

    /// <summary>兼容显式历史导入；播放器回调必须使用带原始令牌的 TryUpsert。</summary>
    public void Upsert(VideoPlaybackHistoryEntry entry) => TryUpsert(entry, CaptureRecordingToken());

    public bool TryUpsert(VideoPlaybackHistoryEntry entry, RecordingToken token)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var value = UserDataCodec.Normalize(entry);
        lock (_sync)
        {
            if (_disposed || _readOnly ||
                !RecordingPolicy.CanWrite(token, new(_recordingRevision, _recordingEnabled))) return false;
            var key = UserDataCodec.Key(value);
            _document = _document with { History = _document.History.Where(e =>
                !string.Equals(UserDataCodec.Key(e), key, StringComparison.OrdinalIgnoreCase))
                .Append(value).OrderByDescending(e => e.LastPlayedUtc).Take(1000).ToArray() };
            ChangedUnderLock();
        }
        HistoryChanged?.Invoke(this, new(PlaybackHistoryChangeKind.Upserted, value.FilePath));
        return true;
    }

    public Task<SettingsSaveResult> SavePlaybackAsync(PlaybackDefaults value, CancellationToken token = default) =>
        SaveSettingsAsync(s => s with { Playback = VideoDefaultsPolicy.Normalize(value) }, token);
    public Task<SettingsSaveResult> SaveLibraryAsync(LibraryDefaults value, CancellationToken token = default) =>
        SaveSettingsAsync(s => s with { Library = VideoDefaultsPolicy.Normalize(value) }, token);
    public Task<SettingsSaveResult> SaveRuntimeAsync(RuntimeSelection value, CancellationToken token = default) =>
        SaveSettingsAsync(s => s with { Runtime = UserDataCodec.Normalize(value) }, token);

    public Task<SettingsSaveResult> SaveRecordingAsync(bool enabled, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_readOnly)
            {
                // 禁用在内存立即生效；重新启用也更换令牌，关闭期间缓存的 Flush 仍不可补写。
                _recordingEnabled = enabled;
                _recordingRevision++;
            }
        }
        return SaveSettingsAsync(s => s with { RecordHistory = enabled }, token);
    }

    private Task<SettingsSaveResult> SaveSettingsAsync(Func<VideoSettings, VideoSettings> change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_readOnly) return Task.FromResult(new SettingsSaveResult(false, LoadWarning));
            _document = _document with { Settings = change(_document.Settings!) };
            _revision++;
        }
        return CommitWithLifetimeAsync(null, token);
    }

    public Task<SettingsSaveResult> RetryAsync(CancellationToken token = default) =>
        CommitWithLifetimeAsync(null, token);

    public void Clear() => ClearHistoryAsync().GetAwaiter().GetResult();
    public void Remove(string filePath, string fileId, long originalFileLength) =>
        RemoveHistoryAsync(filePath, fileId, originalFileLength).GetAwaiter().GetResult();

    public Task<SettingsSaveResult> ClearHistoryAsync(CancellationToken token = default) =>
        ChangeHistoryAsync(new(PlaybackHistoryChangeKind.Cleared),
            document => document with { History = [] }, token);

    public Task<SettingsSaveResult> RemoveHistoryAsync(string path, string fileId, long length, CancellationToken token = default)
    {
        var key = UserDataCodec.Key(path, fileId, length);
        return ChangeHistoryAsync(new(PlaybackHistoryChangeKind.Removed, Path.GetFullPath(path)),
            document => document with { History = document.History.Where(e =>
                !string.Equals(UserDataCodec.Key(e), key, StringComparison.OrdinalIgnoreCase)).ToArray() }, token);
    }

    private async Task<SettingsSaveResult> ChangeHistoryAsync(PlaybackHistoryChangedEventArgs args,
        Func<UserDataDocument, UserDataDocument> change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync) { ThrowIfDisposed(); _recordingRevision++; }
        // 先抑制所有已排队回调；失败保留原记录显示，但不能让旧回调趁失败恢复写入。
        HistoryInvalidated?.Invoke(this, args);
        var result = await CommitWithLifetimeAsync(change, token).ConfigureAwait(false);
        if (result.Success) HistoryChanged?.Invoke(this, args);
        return result;
    }

    public async Task<SettingsSaveResult> RemoveRecentFolderAsync(string? path, CancellationToken token = default)
    {
        var result = await CommitWithLifetimeAsync(document =>
        {
            var library = document.LibrarySettings;
            var remaining = (library.RecentFolders ?? []).Where(p => path is not null &&
                !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            return document with { LibrarySettings = library with { RecentFolders = remaining,
                RecentFolder = remaining.Contains(library.RecentFolder, StringComparer.OrdinalIgnoreCase)
                    ? library.RecentFolder : remaining.FirstOrDefault() ?? "" } };
        }, token).ConfigureAwait(false);
        return result;
    }

    private async Task<SettingsSaveResult> CommitWithLifetimeAsync(
        Func<UserDataDocument, UserDataDocument>? change, CancellationToken token)
    {
        lock (_sync) ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        return await CommitAsync(change, linked.Token).ConfigureAwait(false);
    }

    private async Task<SettingsSaveResult> CommitAsync(
        Func<UserDataDocument, UserDataDocument>? change, CancellationToken token)
    {
        if (_readOnly) return new(false, LoadWarning);
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                UserDataDocument snapshot;
                long revision;
                lock (_sync)
                {
                    if (change is null && _revision == _savedRevision) return new(true, "已保存");
                    snapshot = change?.Invoke(_document) ?? _document;
                    revision = _revision;
                }
                using var prepared = await _writer.PrepareAsync(_filePath, UserDataCodec.Encode(snapshot), token).ConfigureAwait(false);
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    // 文件写入可能较慢。若期间发生了新修改，丢弃临时文件，重新准备最新快照。
                    // 最后一次修订检查、原子替换和发布已提交值必须处于同一同步边界。
                    if (revision != _revision) continue;
                    prepared.Commit();
                    if (change is not null) { _document = snapshot; _revision++; }
                    _savedRevision = _revision;
                    _committedSettings = snapshot.Settings!;
                }
                if (!_disposed) SettingsChanged?.Invoke(this, EventArgs.Empty);
                return new(true, "已保存");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "保存失败，请检查写入权限后重试");
        }
        finally { _writeGate.Release(); }
    }

    private void ChangedUnderLock()
    {
        _revision++;
        var previous = _scheduledWrite;
        var next = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledWrite = next;
        previous?.Cancel();
        _ = WriteAfterDelayAsync(next);
    }

    private async Task WriteAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(500, cancellation.Token).ConfigureAwait(false);
            await CommitAsync(null, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_scheduledWrite, cancellation)) _scheduledWrite = null;
            }
            cancellation.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _scheduledWrite?.Cancel();
            _scheduledWrite = null;
        }
        _lifetime.Cancel();
        // 单一有界文件在退出时提交最近值；未知版本只读，不因 Dispose 被默认值覆盖。
        CommitAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        HistoryChanged = null;
        HistoryInvalidated = null;
        SettingsChanged = null;
        // 已排队的取消回调仍可能完成，不提前 Dispose 信号量/令牌源使它们访问失效对象。
        GC.SuppressFinalize(this);
    }
}
