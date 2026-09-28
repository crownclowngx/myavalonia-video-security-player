using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.Features.Settings;
using VideoSecurityPlayer.Standalone;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class S1SettingsToolTests
{
    [Fact]
    public void 编辑后立即退出会提交最后一次已接受的草稿()
    {
        using var data = new S1SettingsTests.DataFixture();
        using (var store = data.Open())
        {
            using var model = new PlaybackSettingsViewModel(store, store);
            model.FixedVolume = 29;
        }
        using var restored = data.Open();
        Assert.Equal(29, restored.CurrentVideoSettings.Playback.FixedVolume);
    }

    [Fact]
    public async Task Tool复用单例且独立打开不初始化原生后端()
    {
        using var data = new S1SettingsTests.DataFixture();
        var native = new CountingInitializer();
        await using var preview = new PreviewRuntime(data.Root, services =>
            services.Replace(ServiceDescriptor.Scoped<IPlaybackBackendInitializer>(_ => native)));
        var tool = Assert.Single(preview.Registration.Tools);
        var first = Assert.IsType<VideoSettingsTool>(preview.GetToolModel(tool));
        first.RuntimeExpanded = true;
        Assert.Same(first, preview.GetToolModel(tool));
        Assert.True(Assert.IsType<VideoSettingsTool>(preview.GetToolModel(tool)).RuntimeExpanded);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void 预览拒绝重复Tool身份与单例捕获文档作用域()
    {
        var registration = new PreviewRegistration();
        var descriptor = new ToolDescriptor(new("s1.tool"), "测试", "", ToolDockSide.Right, ToolCloseBehavior.Hide);
        registration.AddTool<object, Border>(descriptor);
        Assert.Throws<InvalidOperationException>(() => registration.AddTool<object, Border>(descriptor));
        Assert.Throws<AggregateException>(() => new PreviewRuntime(module: new BadModule()));
    }

    [Fact]
    public async Task 四组草稿的自动保存互不取消且可重建模型恢复()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        using var playback = new PlaybackSettingsViewModel(store, store);
        using var library = new LibrarySettingsViewModel(store, store);
        playback.RememberVolume = false;
        playback.FixedVolume = 27;
        playback.RememberRate = false;
        playback.FixedRate = 1.5f;
        playback.SeekSeconds = 10;
        playback.Resume = false;
        library.RestoreFolder = false;
        library.Recursive = true;
        library.ResetLayoutCommand.Execute(null);
        await Task.WhenAll(playback.Save.PendingTask, library.Save.PendingTask);
        Assert.Equal(27, store.CurrentVideoSettings.Playback.FixedVolume);
        Assert.Equal(10, store.CurrentVideoSettings.Playback.SeekSeconds);
        Assert.False(store.CurrentVideoSettings.Library.RestoreFolder);
        Assert.True(store.CurrentVideoSettings.Library.Recursive);
        using var restored = new PlaybackSettingsViewModel(store, store);
        Assert.Equal(27, restored.FixedVolume);
    }

    [Fact]
    public async Task 保存反馈忽略旧完成并重试最后一次失败操作()
    {
        using var feedback = new SettingsSaveFeedback();
        var slow = new TaskCompletionSource<SettingsSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = feedback.RunAsync(_ => slow.Task, false);
        var fail = true;
        await feedback.RunAsync(_ => Task.FromResult(new SettingsSaveResult(!fail, fail ? "失败" : "最新已保存")), false);
        slow.SetResult(new(true, "旧结果"));
        await first;
        Assert.True(feedback.HasFailure);
        Assert.Equal("失败", feedback.Message);
        fail = false;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feedback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(feedback.Message) && feedback.Message == "最新已保存") published.TrySetResult();
        };
        await feedback.RetryCommand.ExecuteAsync(null);
        // PendingTask 表示磁盘操作完成；UI 投影允许在随后一个消息循环发布，不能以调度快慢判断失败。
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(feedback.HasFailure);
        Assert.Equal("最新已保存", feedback.Message);
    }

    [Fact]
    public async Task 迟到的目录检测不覆盖新草稿且保存前必须复检()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var resolver = new Probe(selection =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); release.Task.GetAwaiter().GetResult(); }
            return Result(selection.Directory, true);
        });
        using var model = new RuntimeSettingsViewModel(store, store, resolver, new Status(), new Diagnostics());
        model.Automatic = false;
        model.DirectoryPath = Path.Combine(data.Root, "old");
        var first = model.CheckCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latest = Path.Combine(data.Root, "latest");
        model.DirectoryPath = latest;
        await model.CheckCommand.ExecuteAsync(null);
        release.SetResult(); await first;
        Assert.Contains(latest, model.CheckMessage);
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(latest, store.CurrentVideoSettings.Runtime.Directory);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task 无效运行库保存保留旧选择且失败加载仍可检测()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        using var model = new RuntimeSettingsViewModel(store, store, new Probe(s => Result(s.Directory, false)),
            new Status(new(RuntimeLoadStatus.Failed, data.Root, PlaybackRuntimeSource.Custom)), new Diagnostics());
        model.Automatic = false; model.DirectoryPath = data.Root;
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.True(model.Save.HasFailure);
        Assert.Equal(RuntimeSourceMode.Auto, store.CurrentVideoSettings.Runtime.Mode);
        Assert.Contains("重启", model.ActualState);
        await model.CheckCommand.ExecuteAsync(null);
        Assert.Contains("缺失", model.CheckMessage);
    }

    [Fact]
    public async Task 清理需要确认意图且取消不改变记录()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        store.Upsert(data.Entry);
        using var model = new RecordSettingsViewModel(store, store, store, store, store);
        model.RequestClearHistoryCommand.Execute(null);
        model.CancelClearHistoryCommand.Execute(null);
        Assert.Single(store.GetAll());
        model.RequestClearHistoryCommand.Execute(null);
        await model.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Empty(store.GetAll());
        Assert.False(model.ConfirmHistory);
        model.Enabled = false;
        Assert.False(store.CaptureRecordingToken().Enabled);
        await model.Save.PendingTask;
    }

    [Fact]
    public async Task 环境导出无文档也可运行且不泄露用户路径()
    {
        using var data = new S1SettingsTests.DataFixture();
        using var store = data.Open();
        await store.SaveRuntimeAsync(new(RuntimeSourceMode.Custom, data.Root));
        store.Upsert(data.Entry);
        var service = new EnvironmentDiagnostics(store, new Probe(s => Result(s.Directory, false)),
            new Status(new(RuntimeLoadStatus.Failed, data.Root, PlaybackRuntimeSource.Custom)));
        var json = Encoding.UTF8.GetString(await service.CreateAsync());
        Assert.DoesNotContain(data.Root, json);
        Assert.DoesNotContain("sample.secvid", json);
        Assert.Contains("NativeLibraryMissing", json);
        Assert.Contains("Failed", json);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(new CancellationToken(true)));
    }

    private static PlaybackRuntimeResolution Result(string path, bool ready) => new(PlaybackRuntimeSource.Custom,
        new(Path.GetTempPath(), path, ready ? [] : [new(DeploymentIssueCode.NativeLibraryMissing, "核心库缺失", path, "重新选择")]), "");
    private sealed class Probe(Func<RuntimeSelection, PlaybackRuntimeResolution> resolve) : IPlaybackRuntimeResolver
    { public PlaybackRuntimeResolution Resolve(RuntimeSelection? selection = null) => resolve(selection ?? new()); }
    private sealed class Status(PlaybackRuntimeSnapshot? value = null) : IPlaybackRuntimeStatus
    {
        public PlaybackRuntimeSnapshot Snapshot => value ?? new(RuntimeLoadStatus.NotAttempted);
        public event EventHandler? Changed { add { } remove { } }
    }
    private sealed class Diagnostics : IEnvironmentDiagnostics
    { public Task<byte[]> CreateAsync(CancellationToken token = default) => Task.FromResult(Array.Empty<byte>()); }
    private sealed class CountingInitializer : IPlaybackBackendInitializer
    {
        public int Calls;
        public void Initialize() { Calls++; throw new InvalidOperationException("Tool 不应触发后端初始化"); }
    }
    private sealed class BadTool(IDocumentLifetime lifetime) { public IDocumentLifetime Lifetime { get; } = lifetime; }
    private sealed class BadModule : IPluginModule
    {
        public void Configure(IPluginRegistration registration) => registration.AddTool<BadTool, Border>(
            new(new("s1.bad"), "错误生命周期", "", ToolDockSide.Right, ToolCloseBehavior.Hide));
    }
}
