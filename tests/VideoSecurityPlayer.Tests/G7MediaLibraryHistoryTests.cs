using System.Runtime.CompilerServices;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[Collection(Secvid03Collection.Name)]
public sealed class G7MediaLibraryHistoryTests(Secvid03Fixture fixture)
{
    [Fact]
    public async Task 扫描选项支持递归且公开信息修改不改变文件身份()
    {
        var root = CreateDirectory();
        try
        {
            var top = Path.Combine(root, "top.secvid");
            File.Copy(fixture.EncryptedPath, top);
            var childFolder = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            var child = Path.Combine(childFolder, "child.SECVID");
            File.Copy(fixture.EncryptedPath, child);
            var scanner = new VideoLibraryScanner();

            var topOnly = await ReadAllAsync(scanner.ScanAsync(
                root,
                VideoLibraryScanOptions.TopDirectoryOnly,
                CancellationToken.None));
            var recursive = await ReadAllAsync(scanner.ScanAsync(
                root,
                new VideoLibraryScanOptions(true),
                CancellationToken.None));
            var original = Assert.Single(topOnly);
            var identity = (original.FileId, original.OriginalFileLength);

            VideoSecurityPlayer.Business.SecretVideoPlayer.Container.EncryptedVideoContainer
                .UpdatePublicInfo(top, "修改后的标题", "新的公开描述");
            var updated = await scanner.ReadFileAsync(top, CancellationToken.None);
            Assert.NotNull(updated);

            Assert.Equal(2, recursive.Count);
            Assert.NotEmpty(original.FileId);
            Assert.Equal(identity, (updated!.FileId, updated.OriginalFileLength));
            Assert.Equal("修改后的标题", updated.PublicTitle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 用户数据限制历史容量并且不写入公开信息或密码()
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "user-data-v1.json");
        try
        {
            using (var store = new SecretVideoUserDataStore(path))
            {
                store.UpdatePreferences(new PlaybackPreferences(73, 1.5f));
                store.UpdateSettings(VideoLibrarySettings.Default with
                {
                    RecentFolder = root,
                    IncludeSubdirectories = true,
                    IsLibrarySettingsExpanded = true
                });
                for (var index = 0; index < 1001; index++)
                {
                    store.Upsert(new VideoPlaybackHistoryEntry(
                        Path.Combine(root, $"{index}.secvid"),
                        index.ToString("X32"),
                        index + 1,
                        index,
                        10_000,
                        DateTimeOffset.UnixEpoch.AddSeconds(index),
                        false));
                }
                Assert.Equal(1000, store.GetAll().Count);
                Assert.DoesNotContain(
                    store.GetAll(),
                    item => string.Equals(
                        Path.GetFileName(item.FilePath),
                        "0.secvid",
                        StringComparison.Ordinal));
            }

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("correct-password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("公开描述", json, StringComparison.Ordinal);
            Assert.DoesNotContain("derivedKey", json, StringComparison.OrdinalIgnoreCase);

            using var reloaded = new SecretVideoUserDataStore(path);
            Assert.Equal(new PlaybackPreferences(73, 1.5f), reloaded.CurrentPreferences);
            Assert.True(reloaded.CurrentSettings.IncludeSubdirectories);
            Assert.True(reloaded.CurrentSettings.IsLibrarySettingsExpanded);
            Assert.Equal(1000, reloaded.GetAll().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 旧版用户数据缺少设置展开字段时兼容加载并默认折叠()
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "user-data-v1.json");
        try
        {
            var legacyDocument = new
            {
                version = 1,
                preferences = new { volume = 50, rate = 1.0f },
                librarySettings = new
                {
                    recentFolder = root,
                    includeSubdirectories = true,
                    sortField = "ModifiedTime",
                    sortDirection = "Descending",
                    statusFilter = "InProgress",
                    isLibraryPaneOpen = true
                },
                history = Array.Empty<object>()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(legacyDocument));

            using var store = new SecretVideoUserDataStore(path);

            Assert.False(store.CurrentSettings.IsLibrarySettingsExpanded);
            Assert.True(store.CurrentSettings.IncludeSubdirectories);
            Assert.Equal(VideoLibrarySortField.ModifiedTime, store.CurrentSettings.SortField);
            Assert.Equal(
                VideoLibrarySortDirection.Descending,
                store.CurrentSettings.SortDirection);
            Assert.Equal(
                VideoLibraryStatusFilter.InProgress,
                store.CurrentSettings.StatusFilter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 损坏或超大用户数据回退默认值且不创建路径备份()
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "user-data-v1.json");
        try
        {
            File.WriteAllText(path, "{ invalid json containing private-path");
            using (var corrupted = new SecretVideoUserDataStore(path))
            {
                Assert.Equal(PlaybackPreferences.Default, corrupted.CurrentPreferences);
                Assert.Empty(corrupted.GetAll());
            }
            Assert.Empty(Directory.EnumerateFiles(root, "*.corrupt*"));

            File.WriteAllBytes(path, new byte[2 * 1024 * 1024 + 1]);
            using var oversized = new SecretVideoUserDataStore(path);
            Assert.Equal(VideoLibrarySettings.Default, oversized.CurrentSettings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 千条投影支持历史排序和状态筛选()
    {
        var root = Path.Combine(Path.GetTempPath(), "g7-thousand-" + Guid.NewGuid().ToString("N"));
        var results = Enumerable.Range(0, 1000)
            .Select(index => new VideoLibraryScanResult(
                Path.Combine(root, $"{index:D4}.secvid"),
                $"{index:D4}",
                $"标题 {1000 - index:D4}",
                index == 777 ? "needle" : string.Empty,
                VideoLibraryMetadataState.Ready,
                string.Empty,
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                100,
                90,
                index.ToString("X32")))
            .ToArray();
        var userData = new MemoryUserDataStore();
        for (var index = 0; index < 20; index++)
        {
            userData.Upsert(new VideoPlaybackHistoryEntry(
                results[index].FilePath,
                results[index].FileId,
                results[index].OriginalFileLength,
                index,
                100,
                DateTimeOffset.UnixEpoch.AddHours(index),
                index % 2 == 0));
        }
        using var lifetime = new TestDocumentLifetime();
        using var browser = new VideoLibraryBrowserViewModel(
            new FixedScanner(results),
            lifetime,
            userData,
            userData);

        var started = DateTime.UtcNow;
        await browser.LoadFolderAsync(root);
        browser.SortField = VideoLibrarySortField.LastPlayedTime;
        browser.SortDirection = VideoLibrarySortDirection.Descending;
        browser.StatusFilter = VideoLibraryStatusFilter.Completed;
        await Task.Delay(250);

        Assert.Equal(10, browser.VisibleItems.Count);
        Assert.Equal(VideoPlaybackHistoryState.Completed, browser.VisibleItems[0].HistoryState);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));

        browser.StatusFilter = VideoLibraryStatusFilter.All;
        browser.SearchText = "needle";
        await Task.Delay(250);
        Assert.Equal("0777", Assert.Single(browser.VisibleItems).FileNameWithoutExtension);
    }

    [Fact]
    public async Task 目录会话合并新增改名和删除事件()
    {
        var root = CreateDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var updates = new VideoLibraryCatalogSession(new VideoLibraryScanner())
            .ObserveAsync(
                root,
                VideoLibraryScanOptions.TopDirectoryOnly,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        try
        {
            Assert.True(await MoveNextAsync(updates));
            Assert.False(updates.Current.IsScanning);

            var added = Path.Combine(root, "added.secvid");
            File.Copy(fixture.EncryptedPath, added);
            var addBatch = await ReadUntilAsync(
                updates,
                batch => batch.Upserts.Any(item =>
                    string.Equals(item.FilePath, added, StringComparison.OrdinalIgnoreCase)));
            Assert.Single(addBatch.Upserts, item =>
                string.Equals(item.FilePath, added, StringComparison.OrdinalIgnoreCase));

            var renamed = Path.Combine(root, "renamed.secvid");
            File.Move(added, renamed);
            var renameBatch = await ReadUntilAsync(
                updates,
                batch =>
                    batch.RemovedPaths.Any(path =>
                        string.Equals(path, added, StringComparison.OrdinalIgnoreCase)) &&
                    batch.Upserts.Any(item =>
                        string.Equals(item.FilePath, renamed, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(
                renameBatch.RemovedPaths,
                path => string.Equals(path, added, StringComparison.OrdinalIgnoreCase));

            File.Delete(renamed);
            var deleteBatch = await ReadUntilAsync(
                updates,
                batch => batch.RemovedPaths.Any(path =>
                    string.Equals(path, renamed, StringComparison.OrdinalIgnoreCase)));
            Assert.Empty(deleteBatch.Upserts);
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 历史协调器按完成规则保存并在清除后抑制当前代次()
    {
        var session = new FakePlaybackSession();
        var history = new MemoryUserDataStore();
        using var coordinator = new PlaybackHistoryCoordinator(session, history);
        var item = Ready("video.secvid", "AABB", 1000);
        coordinator.Track(item, 1);

        session.Raise(PlaybackSnapshot.Empty with
        {
            MediaGeneration = 1,
            State = PlaybackState.Playing,
            HasMedia = true,
            DurationMs = 20 * 60 * 1000,
            PositionMs = 20 * 60 * 1000 - 20 * 1000
        });
        var completed = Assert.Single(history.GetAll());
        Assert.True(completed.IsCompleted);
        Assert.Equal(0, completed.PositionMs);

        history.Clear();
        session.Raise(PlaybackSnapshot.Empty with
        {
            MediaGeneration = 1,
            State = PlaybackState.Paused,
            HasMedia = true,
            DurationMs = 20 * 60 * 1000,
            PositionMs = 1000
        });
        Assert.Empty(history.GetAll());
    }

    private string CreateDirectory()
    {
        var path = Path.Combine(
            fixture.DirectoryPath,
            "g7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static VideoLibraryScanResult Ready(
        string path,
        string fileId,
        long originalLength) =>
        new(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path),
            string.Empty,
            string.Empty,
            VideoLibraryMetadataState.Ready,
            string.Empty,
            default,
            100,
            originalLength,
            fileId);

    private static async Task<List<VideoLibraryScanResult>> ReadAllAsync(
        IAsyncEnumerable<VideoLibraryScanResult> source)
    {
        var results = new List<VideoLibraryScanResult>();
        await foreach (var item in source)
            results.Add(item);
        return results;
    }

    private static async Task<bool> MoveNextAsync(
        IAsyncEnumerator<VideoLibraryCatalogBatch> updates) =>
        await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task<VideoLibraryCatalogBatch> ReadUntilAsync(
        IAsyncEnumerator<VideoLibraryCatalogBatch> updates,
        Func<VideoLibraryCatalogBatch, bool> predicate)
    {
        while (await MoveNextAsync(updates))
        {
            if (predicate(updates.Current))
                return updates.Current;
        }
        throw new Xunit.Sdk.XunitException("目录会话在目标增量批次前意外结束。");
    }

    private sealed class FixedScanner(IReadOnlyList<VideoLibraryScanResult> results)
        : IVideoLibraryScanner
    {
        public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
                await Task.Yield();
            }
        }
    }

    private sealed class MemoryUserDataStore :
        IVideoLibrarySettingsStore,
        IPlaybackHistoryStore
    {
        private readonly List<VideoPlaybackHistoryEntry> _items = [];
        public event EventHandler<PlaybackHistoryChangedEventArgs>? HistoryChanged;
        public VideoLibrarySettings CurrentSettings { get; private set; } =
            VideoLibrarySettings.Default;
        public void UpdateSettings(VideoLibrarySettings settings) => CurrentSettings = settings;
        public VideoPlaybackHistoryEntry? Find(string filePath, string fileId, long originalFileLength) =>
            _items.FirstOrDefault(item =>
                string.Equals(item.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FileId, fileId, StringComparison.OrdinalIgnoreCase) &&
                item.OriginalFileLength == originalFileLength);
        public IReadOnlyList<VideoPlaybackHistoryEntry> GetAll() => _items.ToArray();
        public void Upsert(VideoPlaybackHistoryEntry entry)
        {
            _items.RemoveAll(item =>
                string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FileId, entry.FileId, StringComparison.OrdinalIgnoreCase));
            _items.Add(entry);
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(
                    PlaybackHistoryChangeKind.Upserted,
                    entry.FilePath));
        }
        public void Remove(string filePath, string fileId, long originalFileLength)
        {
            _items.RemoveAll(item =>
                string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FileId, fileId, StringComparison.OrdinalIgnoreCase) &&
                item.OriginalFileLength == originalFileLength);
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(
                    PlaybackHistoryChangeKind.Removed,
                    filePath));
        }
        public void Clear()
        {
            _items.Clear();
            HistoryChanged?.Invoke(
                this,
                new PlaybackHistoryChangedEventArgs(PlaybackHistoryChangeKind.Cleared));
        }
    }

    private sealed class FakePlaybackSession : ISecureVideoPlaybackSession
    {
        public event EventHandler<PlaybackChangedEventArgs>? Changed;
        public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Empty;
        public void Raise(PlaybackSnapshot snapshot)
        {
            Snapshot = snapshot;
            Changed?.Invoke(this, new PlaybackChangedEventArgs(snapshot));
        }
        public Task<PlaybackOperationResult> LoadAsync(string filePath, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> LoadAndPlayAsync(string filePath, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> PlayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> PauseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> SeekAsync(long positionMs, bool waitForFrame = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> SeekRelativeAsync(long deltaMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> SetRateAsync(float rate, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> SelectAudioTrackAsync(int trackId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> SelectSubtitleTrackAsync(int trackId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public Task<PlaybackOperationResult> ReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public bool SetVolume(int volume) => true;
        public void DetachSurface(VideoSurfaceIdentity surface) { }
        public Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(VideoSurfaceIdentity surface, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
        public void Dispose() { }
    }
}
