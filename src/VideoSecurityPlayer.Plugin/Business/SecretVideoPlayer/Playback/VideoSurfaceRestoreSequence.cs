using LibVLCSharp.Shared;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// 可替换的视频表面恢复操作，便于验证 vout 重建顺序而不依赖真实窗口。
/// </summary>
internal interface IVideoSurfaceRestoreOperations
{
    long Length { get; }
    bool Play();
    Task WaitForVideoOutputAsync(CancellationToken cancellationToken);
    Task SeekAsync(long positionMs, bool waitForFrame, CancellationToken cancellationToken);
    Task PauseAtAsync(long positionMs, CancellationToken cancellationToken);
}

internal static class VideoSurfaceRestoreSequence
{
    public static async Task<bool> ExecuteAsync(
        IVideoSurfaceRestoreOperations operations,
        long positionMs,
        bool restorePaused,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operations.Play())
        {
            return false;
        }

        await operations.WaitForVideoOutputAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var maximumPosition = Math.Max(0, operations.Length - 250);
        var targetPosition = Math.Clamp(positionMs, 0, maximumPosition);
        // A surface restore must re-issue the seek even when MediaPlayer.Time
        // still reports the old value. Play can asynchronously reset that
        // stale value to zero while the new vout is starting.
        await operations.SeekAsync(targetPosition, waitForFrame: true, cancellationToken);

        if (restorePaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operations.PauseAtAsync(targetPosition, cancellationToken);
        }

        return true;
    }
}

/// <summary>
/// 基于 LibVLC MediaPlayer 事件实现真实的输出和首帧等待。
/// </summary>
internal sealed class LibVlcVideoSurfaceRestoreOperations(MediaPlayer player) : IVideoSurfaceRestoreOperations
{
    public long Length => player.Length;

    public bool Play() => player.Play();

    public async Task WaitForVideoOutputAsync(CancellationToken cancellationToken)
    {
        var outputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void TryComplete()
        {
            if ((player.IsPlaying || player.State == VLCState.Playing) && player.VoutCount > 0)
            {
                outputReady.TrySetResult();
            }
        }

        void OnPlaying(object? sender, EventArgs args) => TryComplete();
        void OnVout(object? sender, MediaPlayerVoutEventArgs args) => TryComplete();

        player.Playing += OnPlaying;
        player.Vout += OnVout;
        try
        {
            TryComplete();
            await outputReady.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            player.Playing -= OnPlaying;
            player.Vout -= OnVout;
        }
    }

    public async Task SeekAsync(long positionMs, bool waitForFrame, CancellationToken cancellationToken)
    {
        if (!waitForFrame && Math.Abs(player.Time - positionMs) <= 100)
        {
            return;
        }

        var frameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seekIssued = 0;

        void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
        {
            // 压缩媒体通常只能定位到相邻关键帧；750 ms 是 G3 恢复验收容差，
            // 这里额外留出事件采样误差，避免把 seek 前的普通时间事件当成完成。
            if (Volatile.Read(ref seekIssued) != 0 &&
                Math.Abs(args.Time - positionMs) <= 750)
            {
                frameReady.TrySetResult();
            }
        }

        player.TimeChanged += OnTimeChanged;
        try
        {
            Volatile.Write(ref seekIssued, 1);
            player.Time = positionMs;
            await frameReady.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            player.TimeChanged -= OnTimeChanged;
        }
    }

    public async Task PauseAtAsync(
        long positionMs,
        CancellationToken cancellationToken)
    {
        var paused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void TryComplete()
        {
            if (player.State == VLCState.Paused)
            {
                paused.TrySetResult();
            }
        }

        void OnPaused(object? sender, EventArgs args) => TryComplete();

        player.Paused += OnPaused;
        try
        {
            player.SetPause(true);
            TryComplete();
            await paused.Task.WaitAsync(cancellationToken);

            // Play can reset a callback-based input to zero after the first
            // seek event. Re-assert the target only after Paused is confirmed;
            // setting Time while paused is stable even when no TimeChanged is
            // emitted for that final assignment.
            player.Time = positionMs;
        }
        finally
        {
            player.Paused -= OnPaused;
        }
    }
}
