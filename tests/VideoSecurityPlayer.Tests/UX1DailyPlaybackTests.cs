using System.Text.Json;
using Avalonia.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class UX1DailyPlaybackTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 62 ", 62000)]
    [InlineData("1:02", 62000)]
    [InlineData("01:02:03", 3723000)]
    [InlineData("25:00:00", 90000000)]
    public void 时间输入支持秒分秒与超过一天的时分秒(string input, long expected)
    {
        Assert.True(PlaybackTimePolicy.TryParse(input, 100000000, out var position));
        Assert.Equal(expected, position);
    }

    [Theory]
    [InlineData(null, 10000)]
    [InlineData("", 10000)]
    [InlineData("1", 0)]
    [InlineData("10", 10000)]
    [InlineData("-1", 10000)]
    [InlineData("1:60", 1000000)]
    [InlineData("1:60:00", 10000000)]
    [InlineData("0:0:0:0", 10000)]
    [InlineData("9.5", 10000)]
    [InlineData("a", 10000)]
    [InlineData("9223372036854775807:00", long.MaxValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void 非法时间和片尾不会变成播放请求(string? input, long duration) =>
        Assert.False(PlaybackTimePolicy.TryParse(input, duration, out _));

    [Fact]
    public async Task 跳转保持播放意图且无效输入不改变位置()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Player.LoadAndPlayMediaAsync(fixture.FilePath, "password");
        fixture.Player.JumpTimeText = "0:04";
        await fixture.Player.JumpToTimeCommand.ExecuteAsync(null);
        Assert.Equal(4000, fixture.Session.Snapshot.PositionMs);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
        fixture.Player.JumpTimeText = "10";
        await fixture.Player.JumpToTimeCommand.ExecuteAsync(null);
        Assert.Equal(4000, fixture.Session.Snapshot.PositionMs);
        Assert.Contains("片长范围", fixture.Player.JumpFeedback);
    }

    [Fact]
    public void 静音恢复最后非零音量并沿用输入焦点快捷键规则()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        fixture.Player.Volume = 73;
        fixture.Player.ToggleMuteCommand.Execute(null);
        Assert.True(fixture.Player.IsMuted);
        fixture.Player.ToggleMuteCommand.Execute(null);
        Assert.Equal(73, fixture.Player.Volume);
        fixture.Player.Volume = 25;
        fixture.Player.Volume = 0;
        fixture.Player.ToggleMuteCommand.Execute(null);
        Assert.Equal(25, fixture.Player.Volume);
        Assert.Equal(PlaybackShortcutAction.ToggleMute, PlaybackShortcutPolicy.Map(Key.M, KeyModifiers.None, false));
        Assert.Equal(PlaybackShortcutAction.None, PlaybackShortcutPolicy.Map(Key.M, KeyModifiers.Control, false));
    }

    [Fact]
    public void 最近目录去重规范化限长且兼容没有列表的旧设置()
    {
        var root = Path.Combine(Path.GetTempPath(), "ux1-recent");
        var old = Enumerable.Range(0, 15).Select(i => Path.Combine(root, i.ToString())).ToArray();
        var recent = RecentFolderPolicy.Update(old[8] + Path.DirectorySeparatorChar, old);
        Assert.Equal(10, recent.Count);
        Assert.Equal(old[8], recent[0]);
        Assert.Equal(1, recent.Count(path => path == old[8]));
        Assert.Empty(RecentFolderPolicy.Update(null, ["", " ", "invalid\0"]));
        Assert.Single(RecentFolderPolicy.Update(root, null));
    }

    [Fact]
    public void 用户设置可读取旧字段并保存最近目录和侧栏宽度()
    {
        var root = Path.Combine(Path.GetTempPath(), "ux1-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1,
                librarySettings = new { recentFolder = root, isLibraryPaneOpen = true }, history = Array.Empty<object>() }));
            using (var store = new SecretVideoUserDataStore(path))
            {
                Assert.Equal(400, store.CurrentSettings.LibraryPaneWidth);
                Assert.Equal(root, Assert.Single(store.CurrentSettings.RecentFolders!));
                store.UpdateSettings(store.CurrentSettings with { LibraryPaneWidth = 900,
                    RecentFolders = [Path.Combine(root, "other"), root] });
            }
            using var restored = new SecretVideoUserDataStore(path);
            Assert.Equal(600, restored.CurrentSettings.LibraryPaneWidth);
            Assert.Equal(2, restored.CurrentSettings.RecentFolders!.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 继续观看与定位复用当前目录而不发起加载()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SearchText = "找不到的内容";
        fixture.Browser.ShowContinueWatchingCommand.Execute(null);
        Assert.Equal(VideoLibraryStatusFilter.InProgress, fixture.Browser.StatusFilter);
        Assert.Equal(VideoLibrarySortField.LastPlayedTime, fixture.Browser.SortField);
        Assert.Single(fixture.Browser.VisibleItems);
        fixture.Browser.SearchText = "其他筛选";
        Assert.False(fixture.Browser.RevealItem("不存在"));
        Assert.Equal("其他筛选", fixture.Browser.SearchText);
        Assert.True(fixture.Browser.RevealItem(fixture.FilePath));
        Assert.Equal(VideoLibraryStatusFilter.All, fixture.Browser.StatusFilter);
        Assert.Equal("", fixture.Browser.SearchText);
        Assert.Equal(0, fixture.Session.LoadAtPositionAndPlayCalls);
    }

    [Fact]
    public async Task 媒体可加载但恢复回退时保留本次警告()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        using var source = new SingleVideoSourceViewModel(fixture.Player, _ => { }, fixture.Lifetime, fixture.Scanner, fixture.History);
        await source.SelectFileAsync(fixture.FilePath);
        source.Password = "password";
        fixture.Session.Warning = new PlaybackFailure(PlaybackFailureCode.ControlUnavailable, "历史位置恢复失败，已从头加载。");
        await source.PlayVideoCommand.ExecuteAsync(null);
        Assert.True(fixture.Session.Snapshot.HasMedia);
        Assert.NotNull(fixture.Player.LastFailure);
        Assert.Contains("已从头加载", source.StatusMessage);
    }

    [Fact]
    public async Task 移除最近目录记录不切换目录且偏好变化不会重新加入()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        Assert.Contains(fixture.DirectoryPath, fixture.Browser.RecentFolders);
        fixture.Browser.RemoveRecentFolderCommand.Execute(fixture.DirectoryPath);
        fixture.Browser.SortField = VideoLibrarySortField.ModifiedTime;
        Assert.Empty(fixture.Browser.RecentFolders);
        Assert.Equal(fixture.DirectoryPath, fixture.Browser.FolderPath);
        Assert.True(Directory.Exists(fixture.DirectoryPath));
        Assert.Single(fixture.Browser.VisibleItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 单文件输入密码后继续此前的仅加载或从头播放(bool fromStart)
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        using var source = new SingleVideoSourceViewModel(fixture.Player, _ => { }, fixture.Lifetime, fixture.Scanner, fixture.History);
        await source.SelectFileAsync(fixture.FilePath);
        if (fromStart) await source.PlayFromStartCommand.ExecuteAsync(null);
        else await source.LoadVideoCommand.ExecuteAsync(null);
        source.Password = "password";
        Assert.Equal(0, fixture.Session.LoadAtPositionCalls);
        await source.PlayVideoCommand.ExecuteAsync(null);
        Assert.Equal(fromStart ? PlaybackState.Playing : PlaybackState.Ready, fixture.Session.Snapshot.State);
        Assert.Equal(fromStart ? 0 : 3200, fixture.Session.Snapshot.PositionMs);
    }
}
