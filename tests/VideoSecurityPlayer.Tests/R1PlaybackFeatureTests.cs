using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>用可控端口验证部署与导出边界，不启动原生播放器，也不依赖计时等待。</summary>
public sealed class R1PlaybackFeatureTests
{
    [Fact]
    public void 部署聚合全部问题并在修复后清空状态()
    {
        var platform = new Platform { Result = Failed() };
        var initializer = new Initializer();
        using var vm = new PlaybackDeploymentViewModel(platform, initializer);
        var checks = 0;
        vm.Checked += (_, _) => checks++;
        vm.Check();
        Assert.False(vm.IsPlaybackAvailable);
        Assert.Contains("缺少运行库", vm.DeploymentIssueText);
        Assert.Contains("缺少插件", vm.DeploymentIssueText);
        Assert.Equal("native", vm.DeploymentCheckedPath);
        Assert.Equal("重新部署", vm.DeploymentSuggestedAction);
        Assert.Equal(0, initializer.Calls);
        platform.Result = Ready();
        vm.RetryDeploymentCheckCommand.Execute(null);
        Assert.True(vm.IsPlaybackAvailable);
        Assert.Empty(vm.DeploymentIssueText);
        Assert.Empty(vm.DeploymentCheckedPath);
        Assert.Empty(vm.DeploymentSuggestedAction);
        Assert.Equal("播放器部署自检通过", vm.StatusMessage);
        Assert.Equal(1, initializer.Calls);
        Assert.Equal(2, checks);
        vm.Dispose();
        vm.Check();
        vm.ReportFailure(new(PlaybackFailureCode.DeploymentUnavailable, "迟到故障"));
        Assert.Equal(2, checks);
        Assert.True(vm.IsPlaybackAvailable);
    }

    [Theory]
    [InlineData(false, true, "平台不支持")]
    [InlineData(true, false, null)]
    public void 无平台能力时不初始化后端(bool supported, bool video, string? reason)
    {
        var platform = new Platform();
        platform.Capabilities = platform.Capabilities with
        { IsSupported = supported, SupportsNativeVideoOutput = video, UnsupportedReason = reason };
        var initializer = new Initializer();
        using var vm = new PlaybackDeploymentViewModel(platform, initializer);
        vm.Check();
        Assert.False(vm.IsPlaybackAvailable);
        Assert.Contains(reason ?? "当前平台不支持原生视频输出。", vm.DeploymentIssueText);
        Assert.Equal(reason ?? "当前平台不支持原生视频输出。", vm.StatusMessage);
        Assert.Equal(0, initializer.Calls);
    }

    [Fact]
    public void 初始化失败和加载失败共用部署状态且允许重检()
    {
        var initializer = new Initializer { Failure = Failed() };
        using var vm = new PlaybackDeploymentViewModel(new Platform(), initializer);
        vm.Check();
        Assert.False(vm.IsPlaybackAvailable);
        Assert.Contains("DEPLOYMENT_", vm.DeploymentIssueText);
        Assert.Equal("runtime", vm.DeploymentCheckedPath);
        vm.ReportFailure(new(PlaybackFailureCode.DeploymentUnavailable, "加载失败"));
        Assert.Equal("[DEPLOYMENT_UNAVAILABLE] 加载失败", vm.DeploymentIssueText);
        Assert.Equal("请重新部署插件并重启宿主。", vm.DeploymentSuggestedAction);
        vm.ReportFailure(new(PlaybackFailureCode.DeploymentUnavailable, "指定故障",
            DiagnosticCode: "CODE", SuggestedAction: "指定建议"));
        Assert.Equal("[CODE] 指定故障", vm.DeploymentIssueText);
        Assert.Equal("指定建议", vm.DeploymentSuggestedAction);
        initializer.Failure = null;
        vm.Check();
        Assert.True(vm.IsPlaybackAvailable);
    }

    [Fact]
    public void 部署构造拒绝缺失端口()
    {
        Assert.Throws<ArgumentNullException>(() => new PlaybackDeploymentViewModel(null!, new Initializer()));
        Assert.Throws<ArgumentNullException>(() => new PlaybackDeploymentViewModel(new Platform(), null!));
    }

    [Fact]
    public async Task 导出失败释放门闩且下一次成功保留失败快照()
    {
        using var vm = new PlaybackDiagnosticsViewModel();
        Assert.False(vm.CanExportDiagnostics);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.CreateJsonAsync(null));
        Assert.Throws<ArgumentNullException>(() => vm.Configure(null!));
        var exporter = new Exporter { Fail = true };
        vm.Configure(exporter);
        var failure = new PlaybackFailure(PlaybackFailureCode.ControlUnavailable, "音轨不可用");
        await Assert.ThrowsAsync<IOException>(() => vm.CreateJsonAsync(failure));
        Assert.False(vm.IsExportingDiagnostics);
        Assert.True(vm.CanExportDiagnostics);
        vm.ReportFailed();
        Assert.Equal("无法写入所选位置", vm.DiagnosticsStatusMessage);
        exporter.Fail = false;
        var json = await vm.CreateJsonAsync(failure);
        Assert.Equal(new byte[] { 1, 2 }, json.ToArray());
        Assert.Same(failure, exporter.Failure);
        Assert.Empty(vm.DiagnosticsStatusMessage);
        vm.ReportSucceeded();
        Assert.Equal("脱敏诊断已导出", vm.DiagnosticsStatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 关闭取消导出且忽略迟到完成与保存回调(bool ignoreCancellation)
    {
        var exporter = new Exporter { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = ignoreCancellation };
        using var vm = new PlaybackDiagnosticsViewModel();
        vm.Configure(exporter);
        var task = vm.CreateJsonAsync(null);
        Assert.True(vm.IsExportingDiagnostics);
        Assert.False(vm.CanExportDiagnostics);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.CreateJsonAsync(null));
        vm.Dispose();
        vm.Dispose();
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        exporter.Completion.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        vm.ReportSucceeded();
        vm.ReportFailed();
        Assert.Equal(0, notifications);
        Assert.False(vm.IsExportingDiagnostics);
        Assert.False(vm.CanExportDiagnostics);
        Assert.True(exporter.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => vm.Configure(exporter));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.CreateJsonAsync(null));
    }

    [Fact]
    public async Task 调用方取消后仍可再次导出()
    {
        using var vm = new PlaybackDiagnosticsViewModel();
        vm.Configure(new Exporter());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.CreateJsonAsync(null, cancellation.Token));
        Assert.True(vm.CanExportDiagnostics);
        Assert.False((await vm.CreateJsonAsync(null)).IsEmpty);
    }

    private static DeploymentCheckResult Ready() => new("plugin", "runtime", []);
    private static DeploymentCheckResult Failed() => new("plugin", "runtime",
    [
        new(DeploymentIssueCode.NativeLibraryMissing, "缺少运行库", "native", "重新部署"),
        new(DeploymentIssueCode.NativePluginSetIncomplete, "缺少插件", "NATIVE", "重新部署")
    ]);
    private sealed class Platform : IPlaybackPlatformStatus
    {
        public PlaybackPlatformCapabilities Capabilities { get; set; } = new("windows-x64", true, true, true, true, true, true, null);
        public DeploymentCheckResult Result { get; set; } = Ready();
        public DeploymentCheckResult Check() => Result;
    }
    private sealed class Initializer : IPlaybackBackendInitializer
    {
        public int Calls { get; private set; }
        public DeploymentCheckResult? Failure { get; set; }
        public void Initialize()
        {
            Calls++;
            if (Failure is not null) throw new PlaybackDeploymentException(Failure);
        }
    }
    private sealed class Exporter : IPlaybackDiagnosticExporter
    {
        public bool Fail { get; set; }
        public bool IgnoreCancellation { get; init; }
        public TaskCompletionSource? Completion { get; init; }
        public PlaybackFailure? Failure { get; private set; }
        public CancellationToken Token { get; private set; }
        public async Task<ReadOnlyMemory<byte>> CreateJsonAsync(PlaybackFailure? lastFailure, CancellationToken cancellationToken = default)
        {
            Failure = lastFailure;
            Token = cancellationToken;
            if (Fail) throw new IOException("测试导出失败");
            if (Completion is not null)
                await Completion.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            return new byte[] { 1, 2 };
        }
    }
}
