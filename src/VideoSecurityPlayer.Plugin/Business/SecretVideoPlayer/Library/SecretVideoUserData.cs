using System.Text.Json;
using System.Text.Json.Serialization;

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
    bool IsLibrarySettingsExpanded = false)
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

/// <summary>
/// 把三个窄端口存入一个有版本的当前用户 JSON 文件。
/// </summary>
/// <remarks>
/// 单一文件避免清除历史后又从备份或第二缓存恢复。内存更新立即可见，磁盘写入在 500ms
/// 窗口内合并并由一个信号量串行化；进程退出 Dispose 时还会同步刷新最后快照。
/// </remarks>
public sealed class SecretVideoUserDataStore :
    IPlaybackPreferenceStore,
    IVideoLibrarySettingsStore,
    IPlaybackHistoryStore,
    ISecretVideoUserDataDiagnostics,
    IDisposable
{
    private const int CurrentVersion = 1;
    private const int MaximumHistoryEntries = 1000;
    private const long MaximumInputBytes = 2 * 1024 * 1024;
    private static readonly float[] SupportedRates = [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _filePath;
    private readonly CancellationTokenSource _lifetime = new();
    private UserDataDocument _document;
    private CancellationTokenSource? _scheduledWrite;
    private bool _disposed;

    public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;
    public string LoadWarning { get; }

    public PlaybackPreferences CurrentPreferences
    {
        get
        {
            lock (_sync)
                return _document.Preferences;
        }
    }

    public VideoLibrarySettings CurrentSettings
    {
        get
        {
            lock (_sync)
                return _document.LibrarySettings;
        }
    }

    public SecretVideoUserDataStore()
        : this(GetDefaultPath())
    {
    }

    internal SecretVideoUserDataStore(string filePath)
    {
        _filePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(filePath)
                ? throw new ArgumentException("用户数据路径不能为空。", nameof(filePath))
                : filePath);
        _document = LoadOrDefault(_filePath, out var warning);
        LoadWarning = warning;
    }

    public void UpdatePreferences(PlaybackPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var sanitized = Sanitize(preferences);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_document.Preferences == sanitized)
                return;
            _document = _document with { Preferences = sanitized };
            ScheduleWriteUnderLock();
        }
    }

    public void UpdateSettings(VideoLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sanitized = Sanitize(settings);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_document.LibrarySettings == sanitized)
                return;
            _document = _document with { LibrarySettings = sanitized };
            ScheduleWriteUnderLock();
        }
    }

    public VideoPlaybackHistoryEntry? Find(
        string filePath,
        string fileId,
        long originalFileLength)
    {
        var key = CreateHistoryKey(filePath, fileId, originalFileLength);
        lock (_sync)
        {
            return _document.History.FirstOrDefault(
                entry => string.Equals(
                    CreateHistoryKey(entry.FilePath, entry.FileId, entry.OriginalFileLength),
                    key,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<VideoPlaybackHistoryEntry> GetAll()
    {
        lock (_sync)
            return _document.History.ToArray();
    }

    public void Upsert(VideoPlaybackHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sanitized = Sanitize(entry);
        lock (_sync)
        {
            ThrowIfDisposed();
            var key = CreateHistoryKey(
                sanitized.FilePath,
                sanitized.FileId,
                sanitized.OriginalFileLength);
            var history = _document.History
                .Where(existing => !string.Equals(
                    CreateHistoryKey(
                        existing.FilePath,
                        existing.FileId,
                        existing.OriginalFileLength),
                    key,
                    StringComparison.OrdinalIgnoreCase))
                .Append(sanitized)
                .OrderByDescending(existing => existing.LastPlayedUtc)
                .Take(MaximumHistoryEntries)
                .ToArray();
            _document = _document with { History = history };
            ScheduleWriteUnderLock();
        }
        HistoryChanged?.Invoke(
            this,
            new PlaybackHistoryChangedEventArgs(
                PlaybackHistoryChangeKind.Upserted,
                sanitized.FilePath));
    }

    public void Remove(string filePath, string fileId, long originalFileLength)
    {
        var key = CreateHistoryKey(filePath, fileId, originalFileLength);
        lock (_sync)
        {
            ThrowIfDisposed();
            var history = _document.History
                .Where(entry => !string.Equals(
                    CreateHistoryKey(entry.FilePath, entry.FileId, entry.OriginalFileLength),
                    key,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (history.Length == _document.History.Count)
                return;
            _document = _document with { History = history };
            ScheduleWriteUnderLock();
        }
        HistoryChanged?.Invoke(
            this,
            new PlaybackHistoryChangedEventArgs(
                PlaybackHistoryChangeKind.Removed,
                Path.GetFullPath(filePath)));
    }

    public void Clear()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_document.History.Count == 0)
                return;
            _document = _document with { History = Array.Empty<VideoPlaybackHistoryEntry>() };
            ScheduleWriteUnderLock();
        }
        HistoryChanged?.Invoke(
            this,
            new PlaybackHistoryChangedEventArgs(PlaybackHistoryChangeKind.Cleared));
    }

    private void ScheduleWriteUnderLock()
    {
        var previous = _scheduledWrite;
        var replacement =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledWrite = replacement;
        previous?.Cancel();
        _ = WriteAfterDelayAsync(replacement);
    }

    private async Task WriteAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token)
                .ConfigureAwait(false);
            await WriteCurrentAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // 设置持久化失败不能让播放器崩溃；内存快照仍可供当前进程使用。
            // 后续任意设置或历史变化会再次尝试完整原子写入。
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_scheduledWrite, cancellation))
                    _scheduledWrite = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task WriteCurrentAsync(CancellationToken cancellationToken)
    {
        UserDataDocument snapshot;
        lock (_sync)
            snapshot = _document;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.partial-{Guid.NewGuid():N}");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _writeGate.Release();
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // 唯一 GUID 临时文件不会被下一次读取采用，清理失败不影响正式文件。
            }
        }
    }

    private static UserDataDocument LoadOrDefault(string filePath, out string warning)
    {
        warning = string.Empty;
        try
        {
            var file = new FileInfo(filePath);
            if (!file.Exists)
                return UserDataDocument.Default;
            if (file.Length <= 0 || file.Length > MaximumInputBytes)
            {
                warning = "播放设置文件无效或过大，已使用默认设置";
                return UserDataDocument.Default;
            }

            var document = JsonSerializer.Deserialize<UserDataDocument>(
                File.ReadAllBytes(filePath),
                JsonOptions);
            if (document is null || document.Version != CurrentVersion)
            {
                warning = "播放设置版本无效，已使用默认设置";
                return UserDataDocument.Default;
            }

            var history = (document.History ?? Array.Empty<VideoPlaybackHistoryEntry>())
                .Select(Sanitize)
                .Where(entry => entry.FileId.Length > 0)
                .OrderByDescending(entry => entry.LastPlayedUtc)
                .Take(MaximumHistoryEntries)
                .ToArray();
            return new UserDataDocument(
                CurrentVersion,
                Sanitize(document.Preferences),
                Sanitize(document.LibrarySettings),
                history);
        }
        catch
        {
            // 不复制损坏文件，避免清空历史后又从备份恢复，也避免额外保留明文路径。
            warning = "播放设置文件已损坏，已使用默认设置";
            return UserDataDocument.Default;
        }
    }

    private static PlaybackPreferences Sanitize(PlaybackPreferences? value)
    {
        var volume = Math.Clamp(value?.Volume ?? PlaybackPreferences.Default.Volume, 0, 100);
        var requestedRate = value?.Rate ?? PlaybackPreferences.Default.Rate;
        var rate = SupportedRates.FirstOrDefault(
            candidate => Math.Abs(candidate - requestedRate) < 0.0001f);
        return new PlaybackPreferences(
            volume,
            rate <= 0 ? PlaybackPreferences.Default.Rate : rate);
    }

    private static VideoLibrarySettings Sanitize(VideoLibrarySettings? value)
    {
        if (value is null)
            return VideoLibrarySettings.Default;
        var recentFolder = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(value.RecentFolder))
                recentFolder = Path.GetFullPath(value.RecentFolder);
        }
        catch
        {
        }
        return new VideoLibrarySettings(
            recentFolder,
            value.IncludeSubdirectories,
            Enum.IsDefined(value.SortField) ? value.SortField : VideoLibrarySortField.FileName,
            Enum.IsDefined(value.SortDirection)
                ? value.SortDirection
                : VideoLibrarySortDirection.Ascending,
             Enum.IsDefined(value.StatusFilter)
                 ? value.StatusFilter
                 : VideoLibraryStatusFilter.All,
            value.IsLibraryPaneOpen,
            value.IsLibrarySettingsExpanded);
    }

    private static VideoPlaybackHistoryEntry Sanitize(VideoPlaybackHistoryEntry value)
    {
        var path = Path.GetFullPath(value.FilePath);
        var duration = Math.Max(0, value.DurationMs);
        var position = Math.Clamp(value.PositionMs, 0, duration);
        return value with
        {
            FilePath = path,
            FileId = value.FileId.Trim().ToUpperInvariant(),
            OriginalFileLength = Math.Max(0, value.OriginalFileLength),
            PositionMs = value.IsCompleted ? 0 : position,
            DurationMs = duration,
            LastPlayedUtc = value.LastPlayedUtc == default
                ? DateTimeOffset.UtcNow
                : value.LastPlayedUtc
        };
    }

    private static string CreateHistoryKey(
        string filePath,
        string fileId,
        long originalFileLength) =>
        $"{Path.GetFullPath(filePath)}\n{fileId.Trim()}\n{originalFileLength}";

    private static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyAvaloniaManagement",
        "MySmallTools",
        "secret-video-player",
        "user-data-v1.json");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _scheduledWrite?.Cancel();
            _scheduledWrite = null;
        }

        _lifetime.Cancel();
        try
        {
            // 根 DI 容器使用同步 Dispose；这里对最后一个有界 JSON 快照执行同步等待，
            // 确保宿主退出不会丢掉最后不足 500ms 的滑块或位置变化。
            WriteCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }
        _lifetime.Dispose();
        _writeGate.Dispose();
        HistoryChanged = null;
        GC.SuppressFinalize(this);
    }

    private sealed record UserDataDocument(
        int Version,
        PlaybackPreferences Preferences,
        VideoLibrarySettings LibrarySettings,
        IReadOnlyList<VideoPlaybackHistoryEntry> History)
    {
        public static UserDataDocument Default { get; } = new(
            CurrentVersion,
            PlaybackPreferences.Default,
            VideoLibrarySettings.Default,
            Array.Empty<VideoPlaybackHistoryEntry>());
    }
}
