using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class UX1InputAndOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ux1-input-" + Guid.NewGuid().ToString("N"));
    public UX1InputAndOutputTests() => Directory.CreateDirectory(_root);
    private string FileAt(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task 目录导入过滤格式并按递归选项保留结构(bool recursive, int count)
    {
        var first = FileAt("a.MP4");
        FileAt("lesson/b.mkv");
        FileAt("notes.txt");
        var result = await new VideoInputDiscovery().DiscoverAsync([_root, first], VideoInputKind.PlainVideo, recursive, default);
        Assert.Equal(count, result.Files.Count);
        Assert.Equal(2, result.SkippedCount);
        Assert.All(result.Files, file => Assert.False(Path.IsPathRooted(file.RelativeDirectory)));
        Assert.Empty(result.Issues);
        if (recursive) Assert.Contains(result.Files, file => file.RelativeDirectory.EndsWith("lesson"));
    }

    [Fact]
    public async Task 解密导入仅接受加密文件并保留缺失输入问题()
    {
        var input = FileAt("a.SECVID");
        var plain = FileAt("b.mp4");
        var result = await new VideoInputDiscovery().DiscoverAsync([input, input.ToLowerInvariant(), plain, Path.Combine(_root, "missing")],
            VideoInputKind.EncryptedVideo, true, default);
        Assert.Equal(input, Assert.Single(result.Files).Path);
        Assert.Equal(2, result.SkippedCount);
        Assert.Single(result.Issues);
    }

    [Fact]
    public async Task 已取消发现不会返回部分队列()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoInputDiscovery()
            .DiscoverAsync([_root], VideoInputKind.PlainVideo, true, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 导入取消或关闭后忽略迟到发现结果(bool close)
    {
        using var lifetime = new TestDocumentLifetime();
        var discovery = new HeldDiscovery();
        var accepted = 0;
        using var vm = new BatchImportViewModel(discovery, lifetime, VideoInputKind.PlainVideo,
            () => true, _ => { accepted++; return Task.CompletedTask; });
        var operation = vm.ImportAsync(["input"]);
        Assert.True(vm.IsCollecting);
        if (close) lifetime.Close(); else vm.CancelImportCommand.Execute(null);
        discovery.Completion.SetResult(new([new("one.mp4")], 0, []));
        await operation;
        Assert.Equal(0, accepted);
        if (!close) Assert.Contains("取消", vm.Summary);
    }

    [Fact]
    public async Task 发现期间拒绝重复导入且页面变忙后不交付()
    {
        using var lifetime = new TestDocumentLifetime();
        var discovery = new HeldDiscovery();
        var busy = false;
        using var vm = new BatchImportViewModel(discovery, lifetime, VideoInputKind.PlainVideo,
            () => !busy, _ => throw new InvalidOperationException("不得接受"));
        var operation = vm.ImportAsync(["input"]);
        await vm.ImportAsync(["another"]);
        busy = true;
        discovery.Completion.SetResult(new([], 0, []));
        await operation;
        Assert.Equal(1, discovery.Calls);
        Assert.False(vm.IsCollecting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 输出建议可平铺或保留目录但不会创建目录(bool preserve)
    {
        var output = Path.Combine(_root, "output");
        var path = EncryptionOutputPolicy.Create("one.mp4", output, "course/lesson", preserve);
        Assert.Equal(Path.GetFullPath(Path.Combine(output, preserve ? "course/lesson" : "", "one_encrypted.secvid")), path);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("D:/outside")]
    public void 输出结构拒绝越过统一目录(string relative) =>
        Assert.Throws<ArgumentException>(() => EncryptionOutputPolicy.Create("one.mp4", _root, relative, true));

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void 统一输出必须指定绝对目录(string output) =>
        Assert.Throws<ArgumentException>(() => EncryptionOutputPolicy.Create("one.mp4", output, "", false));

    [Fact]
    public async Task 多选配置只修改所选未成功项并废弃旧计划()
    {
        using var lifetime = new TestDocumentLifetime();
        using var vm = new EncryptionBatchViewModel(new R1BatchViewModelTests.EncryptionService(),
            new R1BatchViewModelTests.EncryptionPlanner(), new R1BatchViewModelTests.Runner<PreparedEncryptionItem>(), lifetime);
        await vm.AddFilesAsync(["one.mp4", "two.mp4", "three.mp4"]);
        await vm.CheckBatchCommand.ExecuteAsync(null);
        var completed = vm.Items[0];
        completed.Status.State = VideoTaskState.Succeeded;
        var original = completed.RequestedOutputPath;
        var unselected = vm.Items[2].RequestedOutputPath;
        vm.UnifiedOutputDirectory = Path.Combine(_root, "outputs");
        vm.BatchDescription = "课程说明";
        vm.ApplyOutput([completed, vm.Items[1]]);
        vm.ApplyDescription([completed, vm.Items[1]]);
        Assert.Equal(original, completed.RequestedOutputPath);
        Assert.Equal("", completed.PublicDescription);
        Assert.Equal("课程说明", vm.Items[1].PublicDescription);
        Assert.StartsWith(vm.UnifiedOutputDirectory, vm.Items[1].RequestedOutputPath);
        Assert.Equal(unselected, vm.Items[2].RequestedOutputPath);
        Assert.False(vm.HasPreparedPlan);
        vm.ShowOnlyFailures = true;
        Assert.Empty(vm.VisibleItems);
        vm.Items[2].Status.State = VideoTaskState.Cancelled;
        Assert.Same(vm.Items[2], Assert.Single(vm.VisibleItems));
    }

    private sealed class HeldDiscovery : IVideoInputDiscovery
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<VideoInputDiscoveryResult> Completion { get; } = new();
        public Task<VideoInputDiscoveryResult> DiscoverAsync(IReadOnlyList<string> paths, VideoInputKind kind,
            bool recursive, CancellationToken cancellationToken) { Calls++; return Completion.Task; }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
