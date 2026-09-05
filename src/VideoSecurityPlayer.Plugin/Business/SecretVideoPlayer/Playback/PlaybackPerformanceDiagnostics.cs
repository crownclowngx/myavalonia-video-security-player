using System.Diagnostics;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>
/// Debug 构建下记录播放切换各阶段耗时和 GC 变化；不包含路径、密码或媒体内容。
/// </summary>
internal sealed class PlaybackPerformanceDiagnostics : IDisposable
{
#if DEBUG
    private readonly string _operation;
    private readonly long _startedAt;
    private readonly GcSnapshot _startedGc;
    private long _phaseStartedAt;
    private GcSnapshot _phaseStartedGc;
#endif

    private PlaybackPerformanceDiagnostics(string operation)
    {
#if DEBUG
        _operation = operation;
        _startedAt = Stopwatch.GetTimestamp();
        _phaseStartedAt = _startedAt;
        _startedGc = CaptureGc();
        _phaseStartedGc = _startedGc;
#endif
    }

    public static PlaybackPerformanceDiagnostics Begin(string operation) => new(operation);

    public void Mark(string phase)
    {
#if DEBUG
        var now = Stopwatch.GetTimestamp();
        var currentGc = CaptureGc();
        Write(
            _operation,
            phase,
            Stopwatch.GetElapsedTime(_phaseStartedAt, now),
            _phaseStartedGc,
            currentGc);
        _phaseStartedAt = now;
        _phaseStartedGc = currentGc;
#endif
    }

    public void Dispose()
    {
#if DEBUG
        var now = Stopwatch.GetTimestamp();
        var currentGc = CaptureGc();
        Write(
            _operation,
            "total",
            Stopwatch.GetElapsedTime(_startedAt, now),
            _startedGc,
            currentGc);
#endif
    }

#if DEBUG
    private static GcSnapshot CaptureGc()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        var generationInfo = memoryInfo.GenerationInfo;
        var lohBytes = generationInfo.Length > 3
            ? generationInfo[3].SizeAfterBytes
            : 0;

        // WorkingSet/PrivateBytes 能观察 LibVLC、D3D 纹理和原生解码队列，
        // GC 指标则只反映托管堆。两组数据必须同时记录，否则很容易把正常的
        // 原生缓冲释放误判为 .NET 内存泄漏，或反过来漏掉托管缓存增长。
        using var process = Process.GetCurrentProcess();
        return new GcSnapshot(
            GC.CollectionCount(2),
            lohBytes,
            GC.GetTotalPauseDuration(),
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }

    private static void Write(
        string operation,
        string phase,
        TimeSpan elapsed,
        GcSnapshot before,
        GcSnapshot after)
    {
        Debug.WriteLine(
            $"[VideoSecurityPlayer.Playback] operation={operation} phase={phase} " +
            $"elapsedMs={elapsed.TotalMilliseconds:F1} " +
            $"gen2Delta={after.Gen2Collections - before.Gen2Collections} " +
            $"lohDeltaBytes={after.LohBytes - before.LohBytes} " +
            $"gcPauseDeltaMs={(after.PauseDuration - before.PauseDuration).TotalMilliseconds:F1} " +
            $"workingSetDeltaBytes={after.WorkingSetBytes - before.WorkingSetBytes} " +
            $"privateBytesDelta={after.PrivateBytes - before.PrivateBytes}");
    }

    private readonly record struct GcSnapshot(
        int Gen2Collections,
        long LohBytes,
        TimeSpan PauseDuration,
        long WorkingSetBytes,
        long PrivateBytes);
#endif
}
