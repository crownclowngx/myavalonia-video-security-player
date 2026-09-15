using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>在可控文件端口验证编辑恢复，不依赖磁盘权限、计时或真实原生窗口。</summary>
public sealed class UX1PublicInfoEditingTests
{
    [Fact]
    public async Task 打开并取消编辑不释放当前视频()
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        var store = new InfoStore();
        using var document = CreateDocument(fixture, store);
        await document.SelectFileAsync(fixture.FilePath);
        document.Password = "password";
        await document.Source.PlayVideoCommand.ExecuteAsync(null);
        var releases = fixture.Session.ReleaseCalls;
        await document.PublicInfo.EditPublicInfoCommand.ExecuteAsync(null);
        document.PublicInfo.EditableTitle = "草稿";
        document.PublicInfo.CancelEditPublicInfoCommand.Execute(null);
        Assert.Equal(releases, fixture.Session.ReleaseCalls);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
        Assert.Equal(0, store.Writes);
        Assert.Equal("原标题", document.PublicTitle);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 保存成功或失败均恢复位置且失败保留草稿(bool fail, bool paused)
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        var store = new InfoStore { Fail = fail };
        using var document = CreateDocument(fixture, store);
        await document.SelectFileAsync(fixture.FilePath);
        document.Password = "password";
        await document.Source.PlayVideoCommand.ExecuteAsync(null);
        if (paused) await fixture.Session.PauseAsync();
        await document.PublicInfo.EditPublicInfoCommand.ExecuteAsync(null);
        document.PublicInfo.EditableTitle = "修改后的标题";
        await document.PublicInfo.SavePublicInfoCommand.ExecuteAsync(null);
        Assert.Equal(3200, fixture.Session.Snapshot.PositionMs);
        Assert.Equal(!paused, fixture.Session.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(fail, document.IsEditingPublicInfo);
        Assert.Equal("修改后的标题", document.EditableTitle);
        Assert.False(document.PublicInfo.IsSaving);
        Assert.False(document.Source.IsSavingPublicInfo);
        Assert.Equal(fail ? "原标题" : "修改后的标题", document.PublicTitle);
        if (fail) Assert.Contains("草稿已保留", document.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 保存期间关闭或换片不恢复旧媒体(bool close)
    {
        using var fixture = new G7LibraryActivationTests.LibraryFixture();
        var store = new InfoStore { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var document = CreateDocument(fixture, store);
        await document.SelectFileAsync(fixture.FilePath);
        document.Password = "password";
        await document.Source.PlayVideoCommand.ExecuteAsync(null);
        await document.PublicInfo.EditPublicInfoCommand.ExecuteAsync(null);
        var save = document.PublicInfo.SavePublicInfoCommand.ExecuteAsync(null);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(document.Source.PlayVideoCommand.CanExecute(null));
        if (close) document.Dispose();
        else document.FilePath = "different.secvid";
        store.Hold.SetResult();
        await save;
        Assert.Equal(1, fixture.Session.LoadAtPositionAndPlayCalls);
        Assert.Equal(PlaybackState.Empty, fixture.Session.Snapshot.State);
    }

    private static SecretVideoPlayerViewModel CreateDocument(G7LibraryActivationTests.LibraryFixture fixture, InfoStore store) =>
        new(fixture.Player, fixture.Lifetime, fixture.Scanner, fixture.History, publicInfoStore: store);

    private sealed class InfoStore : IPublicVideoInfoStore
    {
        private string _title = "原标题";
        public int Writes { get; private set; }
        public bool Fail { get; init; }
        public TaskCompletionSource? Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EncryptedVideoPublicInfo Read(string path) => new(3, "sample.mp4", ".mp4", _title, "描述", 12345, "id");
        public async Task UpdateAsync(string path, string title, string description, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Hold is not null) await Hold.Task;
            if (Fail) throw new IOException("测试注入的写入失败");
            Writes++;
            _title = title;
        }
    }
}
