using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class S1SettingsTests
{
    [Theory]
    [InlineData("{\"version\":2,\"settings\":{}}")]
    [InlineData("{\"version\":2,\"settings\":{\"playback\":{},\"library\":{},\"runtime\":{}}}")]
    public void 部分设置文件只恢复缺失字段的产品默认值(string json)
    {
        using var fixture = new DataFixture();
        File.WriteAllText(fixture.Path, json);
        using var store = fixture.Open();
        Assert.Equal(VideoSettings.Default, store.CurrentVideoSettings);
    }

    [Fact]
    public async Task 旧文件迁移保留记录且读取和退出不主动重写()
    {
        using var fixture = new DataFixture();
        var legacy = new UserDataDocument(1, new(73, 1.5f), VideoLibrarySettings.Default with
        { IncludeSubdirectories = true, SortField = VideoLibrarySortField.ModifiedTime, LibraryPaneWidth = 450 }, [fixture.Entry]);
        var original = UserDataCodec.Encode(legacy);
        File.WriteAllBytes(fixture.Path, original);
        using (var store = fixture.Open())
        {
            Assert.Equal(legacy.Preferences, store.CurrentPreferences);
            Assert.True(store.CurrentVideoSettings.Library.Recursive);
            Assert.Equal(450, store.CurrentVideoSettings.Library.PaneWidth);
            Assert.Single(store.GetAll());
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        using var updated = fixture.Open();
        Assert.True((await updated.SavePlaybackAsync(new(FixedVolume: 32))).Success);
        using var json = JsonDocument.Parse(File.ReadAllBytes(fixture.Path));
        Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
        Assert.Single(updated.GetAll());
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    public void 迁移策略区分新旧版本和缺失字段(int version, bool supplied)
    {
        var settings = supplied ? VideoSettings.Default with { RecordHistory = false } : null;
        var result = UserDataMigration.ResolveSettings(version, settings, VideoLibrarySettings.Default);
        Assert.Equal(version == 2 && supplied ? settings : VideoSettings.Default, result);
    }

    [Fact]
    public async Task 更高版本即使内容未知也绝不覆盖()
    {
        using var fixture = new DataFixture();
        const string future = "{\"version\":99,\"preferences\":\"future-format\",\"private\":\"keep\"}";
        File.WriteAllText(fixture.Path, future);
        using (var store = fixture.Open())
        {
            Assert.Contains("更新版本", store.LoadWarning);
            store.UpdatePreferences(new(90, 2));
            store.Upsert(fixture.Entry);
            Assert.False((await store.SaveRecordingAsync(false)).Success);
            Assert.False((await store.ClearHistoryAsync()).Success);
        }
        Assert.Equal(future, File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\":\"wrong\"}")]
    [InlineData("{broken")]
    [InlineData("{\"version\":0}")]
    public void 损坏结构回退且不产生恢复备份(string json)
    {
        using var fixture = new DataFixture();
        File.WriteAllText(fixture.Path, json);
        using var store = fixture.Open();
        Assert.NotEmpty(store.LoadWarning);
        Assert.Empty(store.GetAll());
        Assert.Equal(VideoSettings.Default, store.CurrentVideoSettings);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task 保存失败不发布默认值且可重试最新草稿()
    {
        using var fixture = new DataFixture();
        var writer = new ControlledWriter { Fail = true };
        using var store = fixture.Open(writer);
        Assert.False((await store.SavePlaybackAsync(new(FixedVolume: 70))).Success);
        Assert.Equal(50, store.CurrentVideoSettings.Playback.FixedVolume);
        Assert.False((await store.SavePlaybackAsync(new(FixedVolume: 85))).Success);
        writer.Fail = false;
        Assert.True((await store.RetryAsync()).Success);
        Assert.Equal(85, store.CurrentVideoSettings.Playback.FixedVolume);
        using var reloaded = fixture.Open();
        Assert.Equal(85, reloaded.CurrentVideoSettings.Playback.FixedVolume);
    }

    [Fact]
    public async Task 慢写入期间修改设置会丢弃旧快照并提交最新修订()
    {
        using var fixture = new DataFixture();
        var writer = new ControlledWriter { BlockNext = true };
        using var store = fixture.Open(writer);
        var first = store.SavePlaybackAsync(new(FixedVolume: 10));
        await writer.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = store.SavePlaybackAsync(new(FixedVolume: 90));
        Assert.Equal(50, store.CurrentVideoSettings.Playback.FixedVolume);
        writer.Release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.Success));
        using var reloaded = fixture.Open();
        Assert.Equal(90, reloaded.CurrentVideoSettings.Playback.FixedVolume);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task 取消准备中的保存不发布成功并能显式重试()
    {
        using var fixture = new DataFixture();
        var writer = new ControlledWriter { BlockNext = true };
        using var store = fixture.Open(writer);
        using var cancellation = new CancellationTokenSource();
        var pending = store.SaveLibraryAsync(new(Recursive: true), cancellation.Token);
        await writer.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(store.CurrentVideoSettings.Library.Recursive);
        Assert.True((await store.RetryAsync()).Success);
        Assert.True(store.CurrentVideoSettings.Library.Recursive);
    }

    [Fact]
    public async Task 清理失败仍抑制旧令牌而原历史保持可见()
    {
        using var fixture = new DataFixture();
        var writer = new ControlledWriter();
        using var store = fixture.Open(writer);
        store.Upsert(fixture.Entry);
        Assert.True((await store.RetryAsync()).Success);
        var stale = store.CaptureRecordingToken();
        writer.Fail = true;
        Assert.False((await store.ClearHistoryAsync()).Success);
        Assert.Single(store.GetAll());
        Assert.False(store.TryUpsert(fixture.Entry with { PositionMs = 99 }, stale));
        writer.Fail = false;
        Assert.True((await store.ClearHistoryAsync()).Success);
        Assert.Empty(store.GetAll());
        Assert.False(store.TryUpsert(fixture.Entry, stale));
    }

    [Fact]
    public async Task 禁用立即拒写且重新启用拒绝缓存令牌()
    {
        using var fixture = new DataFixture();
        var writer = new ControlledWriter { Fail = true };
        using var store = fixture.Open(writer);
        var before = store.CaptureRecordingToken();
        Assert.False((await store.SaveRecordingAsync(false)).Success);
        Assert.False(store.TryUpsert(fixture.Entry, before));
        var disabled = store.CaptureRecordingToken();
        writer.Fail = false;
        Assert.True((await store.SaveRecordingAsync(true)).Success);
        Assert.False(store.TryUpsert(fixture.Entry, disabled));
        Assert.True(store.TryUpsert(fixture.Entry, store.CaptureRecordingToken()));
    }

    [Fact]
    public async Task 最近目录清理不删除目录且其他字段更新不能复活记录()
    {
        using var fixture = new DataFixture();
        using var store = fixture.Open();
        store.UpdateSettings(VideoLibrarySettings.Default with { RecentFolder = fixture.Root });
        Assert.True((await store.RemoveRecentFolderAsync(fixture.Root)).Success);
        store.UpdateSettings(store.CurrentSettings with { LibraryPaneWidth = 450 });
        Assert.Empty(store.CurrentSettings.RecentFolders ?? []);
        Assert.Equal("", store.CurrentSettings.RecentFolder);
        Assert.True(Directory.Exists(fixture.Root));
        Assert.True((await store.RemoveRecentFolderAsync(null)).Success);
    }

    [Fact]
    public async Task 移除目标只删除指定历史并使旧请求失效()
    {
        using var fixture = new DataFixture();
        using var store = fixture.Open();
        store.Upsert(fixture.Entry);
        store.Upsert(fixture.Entry with { FileId = "BB" });
        var old = store.CaptureRecordingToken();
        Assert.True((await store.RemoveHistoryAsync(fixture.Entry.FilePath, "AA", 100)).Success);
        Assert.Equal("BB", Assert.Single(store.GetAll()).FileId);
        Assert.False(store.TryUpsert(fixture.Entry, old));
    }

    [Theory]
    [InlineData(true, true, 1, 1, true)]
    [InlineData(false, true, 1, 1, false)]
    [InlineData(true, false, 1, 1, false)]
    [InlineData(true, true, 1, 2, false)]
    public void 记录屏障规则(bool observed, bool current, long a, long b, bool expected) =>
        Assert.Equal(expected, RecordingPolicy.CanWrite(new(a, observed), new(b, current)));

    internal sealed class DataFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s1-data-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "user-data-v1.json");
        public VideoPlaybackHistoryEntry Entry => new(System.IO.Path.Combine(Root, "sample.secvid"), "AA", 100, 20, 100, DateTimeOffset.UtcNow, false);
        public DataFixture() => Directory.CreateDirectory(Root);
        public SecretVideoUserDataStore Open(IUserDataFileWriter? writer = null) => new(Path, writer);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class ControlledWriter : IUserDataFileWriter
    {
        public bool Fail { get; set; }
        public bool BlockNext { get; set; }
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IPreparedUserDataWrite> PrepareAsync(string path, byte[] bytes, CancellationToken token)
        {
            if (Fail) throw new IOException("simulated");
            var pending = await new UserDataFileWriter().PrepareAsync(path, bytes, token);
            if (BlockNext)
            {
                BlockNext = false;
                Prepared.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch { pending.Dispose(); throw; }
            }
            return pending;
        }
    }
}
