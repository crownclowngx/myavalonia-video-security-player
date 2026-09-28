using System.Runtime.InteropServices;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class S1RuntimeTests
{
    [Theory]
    [InlineData(true, true, PlaybackRuntimeSource.Common, true)]
    [InlineData(true, false, PlaybackRuntimeSource.Common, true)]
    [InlineData(false, true, PlaybackRuntimeSource.Bundled, true)]
    [InlineData(false, false, PlaybackRuntimeSource.Bundled, false)]
    public void 自动选择先检查Common且仅预检失败时降级(bool common, bool bundled, PlaybackRuntimeSource source, bool ready)
    {
        using var data = new S1SettingsTests.DataFixture();
        using var settings = data.Open();
        var commonPath = Path.Combine(data.Root, "Common");
        var bundledPath = Path.Combine(data.Root, "native");
        var calls = new List<string>();
        var resolver = new PlaybackRuntimeResolver(settings, new(data.Root, bundledPath), commonPath, layout =>
        {
            calls.Add(layout.RuntimeDirectory);
            return Result(layout.RuntimeDirectory, layout.RuntimeDirectory == commonPath ? common : bundled);
        });
        var actual = resolver.Resolve();
        Assert.Equal(source, actual.Source);
        Assert.Equal(ready, actual.IsReady);
        Assert.Equal(common ? 1 : 2, calls.Count);
        Assert.Equal(!common, actual.Note.Length > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    public void 指定无效路径不回退其他来源(string path)
    {
        using var data = new S1SettingsTests.DataFixture();
        using var settings = data.Open();
        var resolver = new PlaybackRuntimeResolver(settings, new(data.Root, data.Root), data.Root,
            _ => throw new InvalidOperationException("不得检查其他来源"));
        var result = resolver.Resolve(new(RuntimeSourceMode.Custom, path));
        Assert.False(result.IsReady);
        Assert.Equal(PlaybackRuntimeSource.Custom, result.Source);
        Assert.Equal(DeploymentIssueCode.InvalidRuntimeDirectory, Assert.Single(result.Check.Issues).Code);
    }

    [Fact]
    public void 指定目录读失败保留问题且不降级()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var settings = data.Open();
        var resolver = new PlaybackRuntimeResolver(settings, new(data.Root, data.Root), data.Root,
            _ => throw new UnauthorizedAccessException());
        Assert.False(resolver.Resolve(new(RuntimeSourceMode.Custom, data.Root)).IsReady);
    }

    [Fact]
    public void 预检失败不冻结加载且修复后只初始化一次()
    {
        var ready = false;
        var calls = 0;
        var runtime = new LibVlcRuntime(() => new(PlaybackRuntimeSource.Common, Result(Path.GetTempPath(), ready), ""),
            _ => Interlocked.Increment(ref calls), () => "3.0.23");
        Assert.Throws<PlaybackDeploymentException>(runtime.EnsureInitialized);
        Assert.False(runtime.Snapshot.LoadAttempted);
        ready = true;
        Parallel.For(0, 30, _ => runtime.EnsureInitialized());
        Assert.Equal(1, calls);
        Assert.Equal(RuntimeLoadStatus.Ready, runtime.Snapshot.Status);
        Assert.Equal("3.0.23", runtime.Snapshot.Version);
    }

    [Fact]
    public void 真实加载失败固定路径且不会重试或换库()
    {
        var path = Path.Combine(Path.GetTempPath(), "first");
        var original = path;
        var calls = 0;
        var runtime = new LibVlcRuntime(() => new(PlaybackRuntimeSource.Custom, Result(path, true), ""),
            _ => { calls++; throw new DllNotFoundException(); }, () => "");
        Assert.Throws<PlaybackDeploymentException>(runtime.EnsureInitialized);
        path = Path.Combine(Path.GetTempPath(), "second");
        Assert.Throws<PlaybackDeploymentException>(runtime.EnsureInitialized);
        Assert.Equal(1, calls);
        Assert.Equal(RuntimeLoadStatus.Failed, runtime.Snapshot.Status);
        Assert.Equal(original, runtime.Snapshot.Directory);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void 绑定状态只能单向结束且不可重新开始(bool success)
    {
        var state = new RuntimeLoadState();
        Assert.Throws<InvalidOperationException>(() => state.Complete(true));
        state.Begin(new(PlaybackRuntimeSource.Bundled, Result(Path.GetTempPath(), true), ""));
        Assert.Equal(RuntimeLoadStatus.Loading, state.Snapshot.Status);
        state.Complete(success, success ? "3.0.23" : "");
        Assert.Equal(success ? RuntimeLoadStatus.Ready : RuntimeLoadStatus.Failed, state.Snapshot.Status);
        Assert.Throws<InvalidOperationException>(() => state.Begin(new(PlaybackRuntimeSource.Common, Result(Path.GetTempPath(), true), "")));
    }

    [Theory]
    [InlineData(PlaybackRuntimeSource.Common, false)]
    [InlineData(PlaybackRuntimeSource.Bundled, true)]
    [InlineData(PlaybackRuntimeSource.Custom, false)]
    public void 诊断的内置标记采用实际来源(PlaybackRuntimeSource source, bool bundled)
    {
        var runtime = new LibVlcRuntime(() => new(source, Result(Path.GetTempPath(), true), ""), _ => { }, () => "3.0.23");
        var platform = new PlaybackPlatformStatus(new(() => true, () => Architecture.X64), new ReadyProbe(), runtime);
        Assert.False(platform.Capabilities.UsesBundledRuntime);
        runtime.EnsureInitialized();
        Assert.Equal(bundled, platform.Capabilities.UsesBundledRuntime);
    }

    private static DeploymentCheckResult Result(string path, bool ready) => new(Path.GetTempPath(), path,
        ready ? [] : [new(DeploymentIssueCode.NativeLibraryMissing, "缺少运行库", path, "重新选择")]);
    private sealed class ReadyProbe : IPlaybackDeploymentProbe
    { public DeploymentCheckResult Check() => Result(Path.GetTempPath(), true); }
}
