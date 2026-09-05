using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Standalone;

/// <summary>显式命令行触发的开发验收，运行真实独立窗口和原生视频表面。</summary>
internal static class PlaybackSmoke
{
    internal static string? MediaPath { get; set; }
    internal static string? ReportPath { get; set; }

    internal static async Task RunAsync(MainWindow window, PreviewRuntime runtime, string dataRoot)
    {
        string? failure = null;
        var passed = false;
        try
        {
            var encrypted = Path.Combine(dataRoot, "smoke.secvid");
            Directory.CreateDirectory(dataRoot);
            await new Secvid03Encryptor().EncryptAsync(new(MediaPath!, encrypted, "Standalone smoke", ""), "Standalone-Smoke-Password");
            var contribution = runtime.Registration.Documents.Single(item => item.ModelType == typeof(SecretVideoPlayerViewModel));
            var first = await window.OpenDocumentAsync(contribution);
            var document = (PreviewDocument)first.Tag!;
            var model = (SecretVideoPlayerViewModel)document.Model;
            var player = model.PlayerViewModel;
            var load = await player.LoadAndPlayMediaAsync(encrypted, "Standalone-Smoke-Password");
            if (!load) throw new InvalidOperationException("Playback failed.");
            await UntilAsync(() => player.PlaybackSnapshot.PositionMs > 0);
            player.ToggleFullscreenCommand.Execute(null);
            await UntilAsync(() => player.IsFullscreen && !player.IsFullscreenTransitioning);
            if (!window.FindControl<Border>("FullscreenLayer")!.IsVisible) throw new InvalidOperationException("Fullscreen overlay was not displayed.");
            player.ToggleFullscreenCommand.Execute(null);
            await UntilAsync(() => !player.IsFullscreen && !player.IsFullscreenTransitioning);
            var second = await window.OpenDocumentAsync(contribution);
            window.FindControl<TabControl>("Documents")!.SelectedItem = first;
            await Task.Delay(250);
            var seek = await player.SeekMediaAsync(500, false);
            if (!seek.Success) throw new InvalidOperationException($"Seek failed: {seek.Failure?.Code}");
            await window.CloseDocumentAsync(second);
            await window.CloseDocumentAsync(first);
            if (!document.ClosingToken.IsCancellationRequested) throw new InvalidOperationException("Document close signal missing.");
            await runtime.DisposeAsync();
            File.Move(encrypted, encrypted + ".closed");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); Environment.ExitCode = 1; }
        finally
        {
            await runtime.DisposeAsync();
            var report = Path.GetFullPath(ReportPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = passed, Failure = failure }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            Directory.Delete(dataRoot, true);
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
