using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class UX1BatchInteractionTests
{
    [Theory]
    [InlineData(false, false, true, true, true, true, true, "添加")]
    [InlineData(true, true, true, true, true, true, true, "正在处理")]
    [InlineData(true, false, false, true, true, true, true, "输出目录")]
    [InlineData(true, false, true, false, true, true, true, "密码")]
    [InlineData(true, false, true, true, false, true, true, "不一致")]
    [InlineData(true, false, true, true, true, false, true, "重新检查")]
    [InlineData(true, false, true, true, true, true, false, "没有可执行")]
    [InlineData(true, false, true, true, true, true, true, "")]
    public void 开始条件提供可行动说明(bool work, bool busy, bool output, bool password, bool matches, bool plan, bool runnable, string hint) =>
        Assert.Contains(hint, BatchInteractionPolicy.GetStartHint(work, busy, output, password, matches, plan, runnable));

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    public void 重试仅在无待确认条件时自动执行(bool canStart, bool issues, bool unchanged, bool expected) =>
        Assert.Equal(expected, BatchInteractionPolicy.CanRunRetry(canStart, issues, unchanged));

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public async Task 加密重试限定目标并在警告或改名时展示计划(bool warning, bool rename, int runs)
    {
        using var lifetime = new TestDocumentLifetime();
        var planner = new R1BatchViewModelTests.EncryptionPlanner();
        var runner = new R1BatchViewModelTests.Runner<PreparedEncryptionItem> { AutoComplete = true };
        using var vm = new EncryptionBatchViewModel(new R1BatchViewModelTests.EncryptionService(), planner, runner, lifetime);
        await vm.AddFilesAsync(["one.mp4", "two.mp4", "three.mp4"]);
        vm.Password = vm.ConfirmPassword = "123456";
        await vm.CheckBatchCommand.ExecuteAsync(null);
        vm.Items[0].Status.State = VideoTaskState.Failed;
        vm.Items[1].Status.State = VideoTaskState.Succeeded;
        vm.Items[2].Status.State = VideoTaskState.Cancelled;
        vm.SelectedItem = vm.Items[0];
        planner.Issues = warning ? [Warning()] : [];
        planner.Rename = rename;
        await vm.RetrySelectedCommand.ExecuteAsync(null);
        Assert.Equal(runs, runner.RunCalls);
        Assert.Equal(vm.Items[0].ItemId, Assert.Single(planner.LastRequests).ItemId);
        Assert.Equal(VideoTaskState.Succeeded, vm.Items[1].Status.State);
        Assert.Equal(VideoTaskState.Cancelled, vm.Items[2].Status.State);
        if (runs == 0) Assert.Contains("查看问题和输出路径", vm.StatusMessage);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public async Task 解密重试跳过成功并重新确认警告与改名(bool warning, bool rename, int runs)
    {
        using var lifetime = new TestDocumentLifetime();
        var service = new R1BatchViewModelTests.DecryptionService();
        var runner = new R1BatchViewModelTests.Runner<CandidateDecryptionPreflight> { AutoComplete = true };
        using var vm = new DecryptionBatchViewModel(service, runner, lifetime);
        await vm.AddFilesAsync(["one.secvid", "two.secvid"]);
        vm.OutputDirectory = "output";
        vm.Password = "123456";
        await vm.CheckBatchCommand.ExecuteAsync(null);
        vm.Items[0].State = VideoTaskState.Failed;
        vm.Items[1].State = VideoTaskState.Succeeded;
        service.Issues = warning ? [Warning()] : [];
        service.Rename = rename;
        await vm.RetryAllCommand.ExecuteAsync(null);
        Assert.Equal(runs, runner.RunCalls);
        Assert.Equal(VideoTaskState.Succeeded, vm.Items[1].State);
        if (runs > 0) Assert.Equal(vm.Items[0].ItemId, Assert.Single(runner.LastItems).ItemId);
    }

    [Fact]
    public async Task 修改密码与配置会更新禁用原因()
    {
        using var lifetime = new TestDocumentLifetime();
        using var vm = new EncryptionBatchViewModel(new R1BatchViewModelTests.EncryptionService(),
            new R1BatchViewModelTests.EncryptionPlanner(), new R1BatchViewModelTests.Runner<PreparedEncryptionItem>(), lifetime);
        await vm.AddFilesAsync(["one.mp4"]);
        vm.Password = "123456";
        Assert.Contains("不一致", vm.StartHint);
        vm.ConfirmPassword = vm.Password;
        await vm.CheckBatchCommand.ExecuteAsync(null);
        Assert.False(vm.HasStartHint);
        vm.VideoTitle = "新的公开标题";
        Assert.Contains("重新检查", vm.StartHint);
    }

    [Fact]
    public async Task 重新检查失败时加解密均不能执行旧计划()
    {
        using var lifetime = new TestDocumentLifetime();
        var planner = new R1BatchViewModelTests.EncryptionPlanner();
        var service = new R1BatchViewModelTests.DecryptionService();
        using var encryption = new EncryptionBatchViewModel(new R1BatchViewModelTests.EncryptionService(),
            planner, new R1BatchViewModelTests.Runner<PreparedEncryptionItem>(), lifetime);
        using var decryption = new DecryptionBatchViewModel(service,
            new R1BatchViewModelTests.Runner<CandidateDecryptionPreflight>(), lifetime);
        await encryption.AddFilesAsync(["one.mp4"]);
        await decryption.AddFilesAsync(["one.secvid"]);
        encryption.Password = encryption.ConfirmPassword = decryption.Password = "123456";
        decryption.OutputDirectory = "output";
        await encryption.CheckBatchCommand.ExecuteAsync(null);
        await decryption.CheckBatchCommand.ExecuteAsync(null);
        Assert.True(encryption.StartBatchCommand.CanExecute(null));
        Assert.True(decryption.StartBatchCommand.CanExecute(null));
        planner.Fail = service.Fail = true;
        await encryption.CheckBatchCommand.ExecuteAsync(null);
        await decryption.CheckBatchCommand.ExecuteAsync(null);
        Assert.False(encryption.StartBatchCommand.CanExecute(null));
        Assert.False(decryption.StartBatchCommand.CanExecute(null));
        Assert.Contains("重新检查", encryption.StartHint);
        Assert.Contains("重新检查", decryption.StartHint);
    }

    private static VideoPreflightIssue Warning() => new(VideoTaskFailureCode.DiskIo,
        PreflightSeverity.Warning, "需要确认的环境变化", "检查输出设备");
}
