using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.Features.Settings;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;

namespace VideoSecurityPlayer.Standalone;

internal sealed record S1SmokeConfiguration(string Scenario, string DataRoot, string ReportPath, string MediaRoot,
    string ExpectedSource, string CustomDirectory, string? MusicPluginDirectory = null);

/// <summary>显式 S1 开发场景；每个场景在新进程运行，记录真实来源、版本和逐项证据。</summary>
internal static class S1PlaybackSmoke
{
    internal static S1SmokeConfiguration? Configuration { get; private set; }
    internal static void ReadConfiguration(string path)
    {
        Configuration = JsonSerializer.Deserialize<S1SmokeConfiguration>(File.ReadAllText(path))
            ?? throw new ArgumentException("S1 配置无效。");
        // 故障场景会临时移走自己的 DLL，必须由脚本创建独立应用副本并声明归属。
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, ".s1-native-owned")))
            throw new InvalidOperationException("S1 smoke 必须在有归属标记的隔离应用目录执行。");
    }

    internal static void Configure(IServiceCollection services)
    {
        if (Configuration?.Scenario != "load-failure") return;
        services.Replace(ServiceDescriptor.Singleton<LibVlcRuntime>(p => new LibVlcRuntime(
            () => p.GetRequiredService<IPlaybackRuntimeResolver>().Resolve(), directory =>
            {
                var owned = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Common", "libvlc"));
                if (!string.Equals(Path.GetFullPath(directory), owned, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("故障注入仅允许本次隔离 Common 副本。");
                var dll = Path.Combine(owned, "libvlc.dll");
                var moved = dll + ".s1-held";
                File.Move(dll, moved);
                try { LibVlcNativeBinding.Initialize(directory); }
                finally { File.Move(moved, dll); }
            }, () => LibVlcNativeBinding.Version)));
    }

    internal static async Task RunAsync(MainWindow window, PreviewRuntime runtime)
    {
        var config = Configuration!;
        var checks = new List<string>();
        object? musicEvidence = null;
        PlaybackRuntimeSnapshot? actual = null;
        string? failure = null;
        S1MusicCoexistence? music = null;
        var passed = false;
        try
        {
            Directory.CreateDirectory(config.DataRoot);
            var editor = runtime.GetService<IVideoSettingsEditor>();
            var status = runtime.GetService<IPlaybackRuntimeStatus>();
            var toolContribution = runtime.Registration.Tools.Single();
            var tool = (VideoSettingsTool)runtime.GetToolModel(toolContribution);
            if (status.Snapshot.LoadAttempted) throw new InvalidOperationException("独立打开设置触发了原生加载。");
            checks.Add("独立设置使用真实 DI 单例且未初始化原生库");
            if (config.Scenario == "custom") await editor.SaveRuntimeAsync(new(RuntimeSourceMode.Custom, config.CustomDirectory));
            var resolution = runtime.GetService<IPlaybackRuntimeResolver>().Resolve();
            if (config.Scenario == "missing")
            {
                if (resolution.IsReady || status.Snapshot.LoadAttempted) throw new InvalidOperationException("缺库场景误判可用。");
                await tool.Runtime.CheckCommand.ExecuteAsync(null);
                if (status.Snapshot.LoadAttempted) throw new InvalidOperationException("文件检测触发了加载。");
                checks.Add("精简缺库仍能打开设置、文件检测且保持 NotAttempted");
                // 加密是纯业务功能，不因原生运行库缺失而受阻。
                await EncryptAsync(config, "synthetic-av-short.mp4");
                checks.Add("缺库仍可完成真实文件加密");
                passed = true;
                return;
            }
            if (!resolution.IsReady || resolution.Source.ToString() != config.ExpectedSource)
                throw new InvalidOperationException("预检来源不符：" + JsonSerializer.Serialize(resolution));
            if (config.Scenario == "load-failure")
            {
                try { runtime.GetService<IPlaybackRuntimeInitializer>().EnsureInitialized(); }
                catch (PlaybackDeploymentException) { }
                if (status.Snapshot.Status != RuntimeLoadStatus.Failed) throw new InvalidOperationException("真实 DLL 绑定故障没有冻结 Failed。");
                await SaveCustomAsync(tool, config.CustomDirectory);
                try { runtime.GetService<IPlaybackRuntimeInitializer>().EnsureInitialized(); }
                catch (PlaybackDeploymentException) { }
                if (status.Snapshot.Status != RuntimeLoadStatus.Failed || status.Snapshot.Source != PlaybackRuntimeSource.Common)
                    throw new InvalidOperationException("绑定失败后发生静默换源。");
                checks.Add("文件预检后真实绑定失败；修复文件并保存新目录仍保持 Failed，等待新进程重启");
                passed = true;
                return;
            }
            if (config.MusicPluginDirectory is not null)
                music = new(config.MusicPluginDirectory, resolution.Directory, Path.Combine(config.DataRoot, "music"));
            if (config.Scenario == "music-first") await music!.StartAsync();
            var contribution = runtime.Registration.Documents.Single(d => d.ModelType == typeof(SecretVideoPlayerViewModel));
            var tab = await window.OpenDocumentAsync(contribution);
            var document = (PreviewDocument)tab.Tag!;
            var model = (SecretVideoPlayerViewModel)document.Model;
            var player = model.PlayerViewModel;
            var encrypted = await EncryptAsync(config, "synthetic-multitrack-subtitles.mp4");
            await StartAsync(model, encrypted);
            actual = status.Snapshot;
            if (actual.Status != RuntimeLoadStatus.Ready || actual.Source.ToString() != config.ExpectedSource || !actual.Version.StartsWith("3.0.23", StringComparison.Ordinal))
                throw new InvalidOperationException("实际运行库事实不符合预期：" + actual);
            checks.Add("真实 MP4 播放推进；实际句柄来源与 3.0.23 版本确认");
            if (config.Scenario == "video-first") await music!.StartAsync();
            if (music is not null)
            {
                var audioStart = music.Position;
                var videoStart = player.PlaybackSnapshot.PositionMs;
                await UntilAsync(() => music.Position > audioStart + 200 && player.PlaybackSnapshot.PositionMs > videoStart + 200);
                if (!string.Equals(music.ActiveDirectory, actual.Directory, StringComparison.OrdinalIgnoreCase) || !music.ActiveVersion.StartsWith("3.0.23", StringComparison.Ordinal))
                    throw new InvalidOperationException("音乐与视频未使用预期共用库。");
                musicEvidence = new { music.ActiveDirectory, music.ActiveVersion, LoadContext = "S1.Music.Private", audioStart, audioEnd = music.Position };
                checks.Add("同一进程、两个私有托管桥接、音乐与视频同时推进，实际目录与版本一致");
            }
            await player.PauseCommand.ExecuteAsync(null);
            if (!(await player.SeekMediaAsync(500, true)).Success) throw new InvalidOperationException("MP4 定位失败。");
            await UntilAsync(() => player.PlaybackSnapshot.Controls.AudioTracks.Count >= 2 && player.PlaybackSnapshot.Controls.SubtitleTracks.Any(t => t.Id >= 0));
            if (!(await player.SelectAudioTrackAsync(player.PlaybackSnapshot.Controls.AudioTracks.Last().Id)).Success ||
                !(await player.SelectSubtitleTrackAsync(player.PlaybackSnapshot.Controls.SubtitleTracks.First(t => t.Id >= 0).Id)).Success)
                throw new InvalidOperationException("音轨/字幕选择失败。");
            checks.Add("真实暂停、Seek、双音轨与字幕切换");
            var view = document.View!.GetLogicalDescendants().OfType<VideoPlayerControl>().Single();
            var surface = view.GetLogicalDescendants().OfType<EmbeddedVideoSurface>().Single();
            var identity = surface.CurrentSurface ?? throw new InvalidOperationException("缺少 HWND 表面。");
            foreach (var width in new[] { 1280d, 760d })
            {
                window.Width = width; window.Height = 800;
                view.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200);
                if (surface.CurrentSurface != identity || !player.PlaybackSnapshot.HasMedia || player.PlaybackSnapshot.State != PlaybackState.Paused)
                    throw new InvalidOperationException("展开设置重建了视频表面或改变暂停状态。");
                view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "关闭设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            checks.Add("1280/760 宽度展开收起页内设置，HWND/代次及暂停媒体保持");
            // 真正的独立窗口重建 Tool View；不会把文档表面搬到 Tool 窗口中。
            var floating = new Window { Width = 400, Height = 650, Content = runtime.CreateToolView(toolContribution) };
            floating.Show(window); await Task.Delay(100); floating.Content = null; floating.Close();
            var rebuilt = new Window { Width = 320, Height = 650, Content = runtime.CreateToolView(toolContribution) };
            rebuilt.Show(window); await Task.Delay(100);
            if (!ReferenceEquals(((Control)rebuilt.Content!).DataContext, tool) || surface.CurrentSurface != identity)
                throw new InvalidOperationException("真实多窗重建没有复用设置模型或影响播放器表面。");
            rebuilt.Content = null; rebuilt.Close();
            checks.Add("真实多窗 Tool View 关闭重建复用模型，原播放表面保持");
            if (config.Scenario == "frozen")
            {
                await SaveCustomAsync(tool, config.CustomDirectory);
                runtime.GetService<IPlaybackRuntimeInitializer>().EnsureInitialized();
                if (status.Snapshot != actual) throw new InvalidOperationException("加载后保存设置发生了热切换。");
                checks.Add("已加载 Common 后保存指定目录，当前进程保持原库，已保存选择供下一进程使用");
            }
            await window.CloseDocumentAsync(tab);
            File.Move(encrypted, encrypted + ".closed", true);
            music?.Dispose(); music = null;
            var webmTab = await window.OpenDocumentAsync(contribution);
            var webmModel = (SecretVideoPlayerViewModel)((PreviewDocument)webmTab.Tag!).Model;
            var webm = await EncryptAsync(config, "synthetic-silent-multiblock.webm");
            await StartAsync(webmModel, webm);
            if (!(await webmModel.PlayerViewModel.SeekMediaAsync(1000, true)).Success) throw new InvalidOperationException("WebM 定位失败。");
            await window.CloseDocumentAsync(webmTab);
            File.Move(webm, webm + ".closed", true);
            checks.Add("真实 WebM 播放/Seek；两个媒体关闭后文件可移动，音乐会话也释放文件占用");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); Environment.ExitCode = 1; }
        finally
        {
            actual ??= runtime.GetService<IPlaybackRuntimeStatus>().Snapshot;
            music?.Dispose();
            await runtime.DisposeAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(config.ReportPath)!);
            await File.WriteAllTextAsync(config.ReportPath, JsonSerializer.Serialize(new
            { Passed = passed, config.Scenario, ProcessId = Environment.ProcessId, Actual = actual, Music = musicEvidence, Checks = checks, Failure = failure }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
    }

    private static async Task SaveCustomAsync(VideoSettingsTool tool, string directory)
    {
        tool.Runtime.Automatic = false; tool.Runtime.DirectoryPath = directory;
        await tool.Runtime.SaveConfigurationCommand.ExecuteAsync(null);
        if (tool.Runtime.Save.HasFailure) throw new InvalidOperationException(tool.Runtime.Save.Message);
    }
    private static async Task<string> EncryptAsync(S1SmokeConfiguration config, string file)
    {
        var output = Path.Combine(config.DataRoot, file + ".secvid");
        await new Secvid03Encryptor().EncryptAsync(new(Path.Combine(config.MediaRoot, file), output, "S1 smoke", ""), "S1-Smoke-Password");
        return output;
    }
    private static async Task StartAsync(SecretVideoPlayerViewModel model, string encrypted)
    {
        await model.Source.SelectFileAsync(encrypted);
        model.Source.Password = "S1-Smoke-Password";
        await model.Source.PlayVideoCommand.ExecuteAsync(null);
        await UntilAsync(() => model.PlayerViewModel.PlaybackSnapshot.PositionMs > 100);
        if (!model.PlayerViewModel.PlaybackSnapshot.HasMedia) throw new InvalidOperationException(model.Source.StatusMessage);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("S1 原生状态转换超时。");
            await Task.Delay(50);
        }
    }
}
