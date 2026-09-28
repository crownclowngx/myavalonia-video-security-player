using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class S1DefaultsTests
{
    [Theory]
    [InlineData(0.5f)] [InlineData(0.75f)] [InlineData(1f)] [InlineData(1.25f)] [InlineData(1.5f)] [InlineData(2f)]
    public void 固定和记忆速率保留支持的六档(float rate)
    {
        Assert.Equal(rate, VideoDefaultsPolicy.Resolve(new PlaybackDefaults(RememberRate: false, FixedRate: rate), new(33, 1)).Rate);
        Assert.Equal(rate, VideoDefaultsPolicy.Resolve(new PlaybackDefaults(), new PlaybackPreferences(33, rate)).Rate);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-1)] [InlineData(3)]
    public void 非法倍速恢复默认(float rate) => Assert.Equal(1, VideoDefaultsPolicy.NormalizeRate(rate));

    [Theory]
    [InlineData(5, 5)] [InlineData(10, 10)] [InlineData(30, 30)] [InlineData(0, 5)]
    public void 步长限制为明确选项(int input, int expected) =>
        Assert.Equal(expected, VideoDefaultsPolicy.Normalize(new PlaybackDefaults(SeekSeconds: input)).SeekSeconds);

    [Fact]
    public void 无效库默认值和音量被规范化()
    {
        Assert.Equal(new PlaybackPreferences(100, 1), VideoDefaultsPolicy.Resolve(new PlaybackDefaults(RememberVolume: false, FixedVolume: 200), new(20, 1)));
        Assert.Equal(0, VideoDefaultsPolicy.Resolve(new PlaybackDefaults(), new PlaybackPreferences(-1, 1)).Volume);
        var value = VideoDefaultsPolicy.Normalize(new LibraryDefaults(SortField: (VideoLibrarySortField)999,
            SortDirection: (VideoLibrarySortDirection)999, PaneWidth: double.NaN));
        Assert.Equal(new LibraryDefaults(), value);
        Assert.Equal(340, VideoDefaultsPolicy.Normalize(new LibraryDefaults(PaneWidth: -1)).PaneWidth);
        Assert.Equal(600, VideoDefaultsPolicy.Normalize(new LibraryDefaults(PaneWidth: 900)).PaneWidth);
    }

    [Fact]
    public async Task 两个已开文档保持原状态而新文档使用已提交默认值()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        store.UpdatePreferences(new(73, 1.5f));
        using var first = new G7LibraryActivationTests.LibraryFixture(store);
        using var second = new G7LibraryActivationTests.LibraryFixture(store);
        Assert.Equal(73, first.Player.Volume);
        Assert.True((await store.SavePlaybackAsync(new(false, 20, false, 2, false, 30))).Success);
        Assert.True((await store.SaveLibraryAsync(new(RestoreFolder: false, Recursive: true, PaneOpen: false))).Success);
        using var next = new G7LibraryActivationTests.LibraryFixture(store);
        Assert.Equal(73, first.Player.Volume);
        Assert.Equal(1.5f, second.Player.SelectedRate);
        Assert.Equal(20, next.Player.Volume);
        Assert.Equal(2, next.Player.SelectedRate);
        Assert.Contains("30 秒", next.Player.ShortcutHelp);
        Assert.Contains("5 秒", first.Player.ShortcutHelp);
        Assert.False(first.Browser.IncludeSubdirectories);
        Assert.True(next.Browser.IncludeSubdirectories);
        Assert.True(second.Library.IsLibraryPaneOpen);
        Assert.False(next.Library.IsLibraryPaneOpen);
        first.Player.Volume = 99;
        first.Browser.IncludeSubdirectories = false;
        Assert.Equal(20, store.CurrentVideoSettings.Playback.FixedVolume);
        Assert.True(store.CurrentVideoSettings.Library.Recursive);
    }

    [Fact]
    public async Task 关闭恢复目录不会自动打开历史目录且不会删除最近目录()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        store.UpdateSettings(VideoLibrarySettings.Default with { RecentFolder = data.Root });
        await store.SaveLibraryAsync(new(RestoreFolder: false));
        using var fixture = new G7LibraryActivationTests.LibraryFixture(store);
        await fixture.Browser.InitializeRecentFolderAsync();
        Assert.Equal("", fixture.Browser.FolderPath);
        Assert.Contains(data.Root, fixture.Browser.RecentFolders);
        Assert.Equal(data.Root, VideoDefaultsPolicy.Resolve(new(), store.CurrentSettings).RecentFolder);
    }

    [Fact]
    public async Task 两种播放器都遵守从头默认且仅加载不播放()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        await store.SavePlaybackAsync(new(Resume: false));
        using var fixture = new G7LibraryActivationTests.LibraryFixture(store);
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Library.Password = "password";
        await fixture.Library.PlaySelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Session.Snapshot.PositionMs);
        using var source = new SingleVideoSourceViewModel(fixture.Player, _ => { }, fixture.Lifetime,
            fixture.Scanner, fixture.History);
        await source.SelectFileAsync(fixture.FilePath);
        source.Password = "password";
        Assert.Equal("播放", source.PlayButtonText);
        await source.LoadVideoCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Session.Snapshot.PositionMs);
        Assert.NotEqual(VideoSecurityPlayer.Business.SecretVideoPlayer.Playback.PlaybackState.Playing, fixture.Session.Snapshot.State);
    }
}
