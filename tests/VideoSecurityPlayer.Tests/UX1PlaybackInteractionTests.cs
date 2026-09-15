using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>通过记录型播放端口验证用户流程，不启动原生库或发布验收程序。</summary>
public sealed class UX1PlaybackInteractionTests
{
    [Theory]
    [InlineData(3200, 10000, false, false, 3200)]
    [InlineData(3200, 10000, false, true, 0)]
    [InlineData(3200, 10000, true, false, 0)]
    [InlineData(-1, 10000, false, false, 0)]
    [InlineData(0, 10000, false, false, 0)]
    [InlineData(10000, 10000, false, false, 0)]
    [InlineData(12000, 10000, false, false, 0)]
    [InlineData(3200, 0, false, false, 3200)]
    public void 续播排除完成越界与显式从头播放(long position, long duration, bool completed, bool fromStart, long expected)
    {
        var entry = new VideoPlaybackHistoryEntry("file", "id", 1, position, duration, DateTimeOffset.UtcNow, completed);
        Assert.Equal(expected, PlaybackResumePolicy.GetPosition(entry, fromStart));
        Assert.Equal(0, PlaybackResumePolicy.GetPosition(null));
    }

    [Theory]
    [InlineData(-1000, "00:00")]
    [InlineData(3200, "00:03")]
    [InlineData(3661000, "1:01:01")]
    [InlineData(90061000, "25:01:01")]
    public void 时间格式覆盖负数小时与超过一天(long position, string expected)
    {
        Assert.Equal(expected, PlaybackResumePolicy.FormatTime(position));
        Assert.Equal(position > 0 ? $"继续播放 · {expected}" : "播放", PlaybackResumePolicy.PlayLabel(position));
    }

    [Fact]
    public async Task 密码确认继续既有意图而取消后不再起播()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SelectedItem = Assert.Single(fixture.Browser.VisibleItems);
        await fixture.Library.PlaySelectedCommand.ExecuteAsync(null);
        Assert.True(fixture.Library.IsPasswordPromptOpen);
        fixture.Library.Password = "password";
        Assert.Equal(0, fixture.Session.LoadAtPositionAndPlayCalls);
        await fixture.Library.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Session.LoadAtPositionAndPlayCalls);
        Assert.Equal(3200, fixture.Session.LastRequestedPositionMs);

        fixture.Library.Password = "";
        await fixture.Library.PlaySelectedCommand.ExecuteAsync(null);
        fixture.Library.CancelOpeningCommand.Execute(null);
        fixture.Library.Password = "password";
        await fixture.Library.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Session.LoadAtPositionAndPlayCalls);
    }

    [Fact]
    public async Task 密码等待期间取消选择会丢弃旧目标()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SelectedItem = Assert.Single(fixture.Browser.VisibleItems);
        await fixture.Library.LoadSelectedCommand.ExecuteAsync(null);
        Assert.Equal("确认并加载", fixture.Library.PasswordActionText);
        fixture.Browser.SelectedItem = null;
        fixture.Library.Password = "password";
        await fixture.Library.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.False(fixture.Library.IsPasswordPromptOpen);
        Assert.Equal(0, fixture.Session.LoadAtPositionCalls);
    }

    [Fact]
    public async Task 加载取消后迟到结果不覆盖取消状态()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        fixture.Browser.SelectedItem = Assert.Single(fixture.Browser.VisibleItems);
        fixture.Library.Password = "password";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.BeforeOpen = _ => { entered.SetResult(); return release.Task; };
        var opening = fixture.Library.PlaySelectedCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Library.CancelOpeningCommand.Execute(null);
        release.SetResult();
        await opening;
        Assert.Equal("已取消打开视频", fixture.Library.StatusMessage);
        Assert.Empty(fixture.Library.CurrentPlayingPath);
        Assert.False(fixture.Library.IsOpening);
    }

    [Fact]
    public async Task 历史进度与当前播放状态分别展示()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        await fixture.Browser.LoadFolderAsync(fixture.DirectoryPath);
        var item = Assert.Single(fixture.Browser.VisibleItems);
        fixture.Browser.SelectedItem = item;
        Assert.Equal("未看完", item.HistoryStateText);
        Assert.Equal("00:03 / 00:10", item.HistoryProgressText);
        Assert.Equal(32, item.HistoryPercentage);
        Assert.False(item.IsCurrentMedia);
        Assert.Equal("继续播放 · 00:03", fixture.Library.PlayButtonText);
        fixture.Library.Password = "password";
        await fixture.Library.PlaySelectedCommand.ExecuteAsync(null);
        Assert.Equal("正在播放", item.CurrentPlaybackText);
        item.SetPlaybackState(PlaybackState.Paused);
        Assert.Equal("已暂停", item.CurrentPlaybackText);
        Assert.Equal("未看完", item.HistoryStateText);
        item.SetPlaybackState(null);
        Assert.False(item.IsCurrentMedia);
    }

    [Fact]
    public async Task 单文件主播放恢复历史且仅加载保留暂停()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        using var document = new SecretVideoPlayerViewModel(fixture.Player, fixture.Lifetime, fixture.Scanner, fixture.History);
        await document.SelectFileAsync(fixture.FilePath);
        document.Password = "password";
        Assert.Equal("继续播放 · 00:03", document.Source.PlayButtonText);
        await document.Source.PlayVideoCommand.ExecuteAsync(null);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
        Assert.Equal(3200, fixture.Session.LastRequestedPositionMs);
        Assert.False(document.Source.IsSourceExpanded);
        await document.Source.LoadVideoCommand.ExecuteAsync(null);
        Assert.Equal(PlaybackState.Ready, fixture.Session.Snapshot.State);
        await document.Source.PlayFromStartCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Session.Snapshot.PositionMs);
    }

    [Fact]
    public async Task 单文件取消拒绝迟到结果并允许重新发起()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        using var document = new SecretVideoPlayerViewModel(fixture.Player, fixture.Lifetime, fixture.Scanner, fixture.History);
        await document.SelectFileAsync(fixture.FilePath);
        document.Password = "password";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.BeforeOpen = _ => { entered.SetResult(); return release.Task; };
        var opening = document.Source.PlayVideoCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        document.Source.CancelLoadCommand.Execute(null);
        release.SetResult();
        await opening;
        Assert.Equal("已取消打开视频", document.StatusMessage);
        Assert.False(document.IsLoading);
        fixture.Session.BeforeOpen = null;
        await document.Source.PlayVideoCommand.ExecuteAsync(null);
        Assert.Equal("正在播放", document.StatusMessage);
    }
}
