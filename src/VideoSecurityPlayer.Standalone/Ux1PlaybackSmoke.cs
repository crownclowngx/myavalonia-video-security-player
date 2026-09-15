using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Standalone;

/// <summary>UX1 专用真实媒体场景，要求多音轨及内嵌字幕样本，由显式 --smoke-ux1 触发。</summary>
internal static class Ux1PlaybackSmoke
{

    internal static async Task RunAsync(MainWindow window, PreviewRuntime runtime, string dataRoot)
    {
        string? failure = null;
        var passed = false;
        var checks = new List<string>();
        try
        {
            var encrypted = Path.Combine(dataRoot, "smoke.secvid");
            Directory.CreateDirectory(dataRoot);
            await new Secvid03Encryptor().EncryptAsync(new(PlaybackSmoke.MediaPath!, encrypted, "Standalone smoke", ""), "Standalone-Smoke-Password");
            var contribution = runtime.Registration.Documents.Single(item => item.ModelType == typeof(SecretVideoPlayerViewModel));
            var first = await window.OpenDocumentAsync(contribution);
            var document = (PreviewDocument)first.Tag!;
            var model = (SecretVideoPlayerViewModel)document.Model;
            var player = model.PlayerViewModel;
            // 验证真实用户入口：来源选择、密码、主按钮与原生视频表面共同工作。
            await model.Source.SelectFileAsync(encrypted);
            model.Source.Password = "Standalone-Smoke-Password";
            await model.Source.PlayVideoCommand.ExecuteAsync(null);
            if (!player.PlaybackSnapshot.HasMedia) throw new InvalidOperationException("来源入口未加载媒体：" + model.Source.StatusMessage);
            await UntilAsync(() => player.PlaybackSnapshot.PositionMs > 0);
            checks.Add("来源主按钮启动真实视频并推进位置");
            await player.PauseCommand.ExecuteAsync(null);
            await player.SeekMediaAsync(500, true);
            await player.SetPlaybackRateAsync(1.25f);
            await UntilAsync(() => player.PlaybackSnapshot.Controls.AudioTracks.Count >= 2 &&
                player.PlaybackSnapshot.Controls.SubtitleTracks.Any(track => track.Id >= 0));
            var audioTrack = player.PlaybackSnapshot.Controls.AudioTracks.Last().Id;
            var subtitleTrack = player.PlaybackSnapshot.Controls.SubtitleTracks.First(track => track.Id >= 0).Id;
            if (!(await player.SelectAudioTrackAsync(audioTrack)).Success || !(await player.SelectSubtitleTrackAsync(subtitleTrack)).Success)
                throw new InvalidOperationException("真实样本无法选择目标音轨或字幕轨");
            var beforeEdit = player.PlaybackSnapshot;
            await model.PublicInfo.EditPublicInfoCommand.ExecuteAsync(null);
            model.PublicInfo.EditableTitle = "UX1 真实媒体编辑恢复";
            await model.PublicInfo.SavePublicInfoCommand.ExecuteAsync(null);
            if (model.PublicInfo.IsEditingPublicInfo || EncryptedVideoContainer.ReadPublicInfo(encrypted).Title != "UX1 真实媒体编辑恢复")
                throw new InvalidOperationException("公开信息未保存：" + model.Source.StatusMessage);
            var afterEdit = player.PlaybackSnapshot;
            if (!afterEdit.HasMedia || afterEdit.State == PlaybackState.Playing || Math.Abs(afterEdit.PositionMs - beforeEdit.PositionMs) > 300 ||
                Math.Abs(afterEdit.Controls.Rate - beforeEdit.Controls.Rate) > 0.01)
                throw new InvalidOperationException($"保存恢复不匹配：原 {beforeEdit.State}/{beforeEdit.PositionMs}ms/{beforeEdit.Controls.Rate}，现 {afterEdit.State}/{afterEdit.PositionMs}ms/{afterEdit.Controls.Rate}，有媒体 {afterEdit.HasMedia}；{model.Source.StatusMessage}");
            if (afterEdit.Controls.SelectedAudioTrackId != audioTrack || afterEdit.Controls.SelectedSubtitleTrackId != subtitleTrack)
                throw new InvalidOperationException("保存恢复后没有保持原音轨及字幕选择");
            checks.Add("原生媒体释放后保存公开信息并恢复位置、暂停、倍速、音轨与字幕轨");
            player.Volume = 37;
            player.ToggleMuteCommand.Execute(null);
            if (player.Volume != 0) throw new InvalidOperationException("静音未生效");
            player.ToggleMuteCommand.Execute(null);
            if (player.Volume != 37) throw new InvalidOperationException("音量未恢复");
            checks.Add("静音与恢复原音量");
            window.Width = 760;
            window.Height = 600;
            player.ToggleFullscreenCommand.Execute(null);
            await UntilAsync(() => player.IsFullscreen && !player.IsFullscreenTransitioning);
            if (!window.FindControl<Border>("FullscreenLayer")!.IsVisible) throw new InvalidOperationException("Fullscreen overlay was not displayed.");
            player.ToggleFullscreenCommand.Execute(null);
            await UntilAsync(() => !player.IsFullscreen && !player.IsFullscreenTransitioning);
            if (player.PlaybackSnapshot.State != PlaybackState.Paused ||
                Math.Abs(player.PlaybackSnapshot.PositionMs - afterEdit.PositionMs) > 750)
                throw new InvalidOperationException("全屏切换后暂停位置发生变化");
            checks.Add("760 × 600 窗口进入及退出内容区全屏并保持暂停位置");
            var second = await window.OpenDocumentAsync(contribution);
            window.FindControl<TabControl>("Documents")!.SelectedItem = first;
            await Task.Delay(250);
            var seek = await player.SeekMediaAsync(500, false);
            if (!seek.Success) throw new InvalidOperationException($"Seek failed: {seek.Failure?.Code}");
            checks.Add("多标签切换后仍可定位");
            // 同一来源再次明确播放应读取刚保存的历史，覆盖真实冷启动的“恢复后继续播放”分支。
            await model.Source.PlayVideoCommand.ExecuteAsync(null);
            if (!player.IsPlaying || player.PlaybackSnapshot.PositionMs < 250)
                throw new InvalidOperationException("从历史继续播放未恢复位置：" + model.Source.StatusMessage);
            checks.Add("再次通过来源主按钮恢复历史并继续真实播放");
            await window.CloseDocumentAsync(second);
            await window.CloseDocumentAsync(first);
            if (!document.ClosingToken.IsCancellationRequested) throw new InvalidOperationException("Document close signal missing.");
            await runtime.DisposeAsync();
            File.Move(encrypted, encrypted + ".closed");
            checks.Add("关闭 Document 取消作用域并释放文件占用");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); Environment.ExitCode = 1; }
        finally
        {
            await runtime.DisposeAsync();
            var report = Path.GetFullPath(PlaybackSmoke.ReportPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Failure = failure }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            // 清理仅限本次独立验证在临时目录创建的根，拒绝意外传入任意工程路径。
            var fullRoot = Path.GetFullPath(dataRoot);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullRoot).StartsWith("video-standalone-smoke-", StringComparison.Ordinal) && Directory.Exists(fullRoot))
                Directory.Delete(fullRoot, true);
        }
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Standalone playback transition timed out.");
            await Task.Delay(50);
        }
    }
}
