using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Views.SecretVideoPlayer;

namespace VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>
/// Avalonia 12 与 LibVLCSharp 的运行时兼容闸门；组合 G3 与 G8，不复制播放器业务流程。
/// </summary>
internal sealed class Phase4AcceptanceSuite(
    IServiceProvider services,
    HarnessOptions options) : IAcceptanceSuite
{
    private const long PrivateMemoryAllowanceBytes = 64L * 1024 * 1024;
    private readonly List<string> _failures = [];
    private int _unhandledExceptionCount;
    private long _peakPrivateBytes;
    private int _peakHandleCount;

    public async Task<int> RunAsync()
    {
        var reportPath = Path.GetFullPath(options.ReportPath);
        var reportDirectory = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(reportDirectory);
        var g3ReportPath = Path.Combine(reportDirectory, "phase4-g3.json");
        var g8ReportPath = Path.Combine(reportDirectory, "phase4-g8.json");
        // 源码状态必须在生成任何证据文件前绑定，避免报告本身把 clean 快照误判为脏。
        var source = ReadSourceState();
        var threadPoolWarmupWorkerCount = await WarmUpThreadPoolAsync();
        // 采样器在预热前启动，使线程池与计时器句柄同时存在于资源起点和终点。
        using var sampler = new DispatcherResourceSampler(CapturePeak);
        var warmupPassed = await WarmUpRuntimeAsync();

        // Avalonia、D3D11 和 LibVLC 会按真实负载扩展进程级句柄池与缓存。资源闸门从
        // 一次同规模 G3/G8 预热后的稳定点起算，测量重复生命周期增长而非池扩容成本。
        await StabilizeProcessAsync();
        var surfaceStart = EmbeddedVideoSurface.CaptureDiagnostics();
        var processStart = ProcessResourceSnapshot.Capture();
        var handleTypesStart = WindowsHandleDiagnostics.CaptureCurrentProcessByType();
        _peakPrivateBytes = processStart.PrivateBytes;
        _peakHandleCount = processStart.HandleCount;

        UnhandledExceptionEventHandler unhandledHandler = (_, _) =>
            Interlocked.Increment(ref _unhandledExceptionCount);
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, _) =>
            Interlocked.Increment(ref _unhandledExceptionCount);
        AppDomain.CurrentDomain.UnhandledException += unhandledHandler;
        TaskScheduler.UnobservedTaskException += unobservedHandler;

        var g3ExitCode = 1;
        var g8ExitCode = 1;
        IReadOnlyDictionary<string, int> handleTypesAfterG3 = handleTypesStart;
        try
        {
            g3ExitCode = await new G3PlaybackHarnessRunner(
                services,
                options with
                {
                    Suite = HarnessSuite.G3,
                    ReportPath = g3ReportPath
                }).RunAsync();
            await StabilizeProcessAsync();
            handleTypesAfterG3 =
                WindowsHandleDiagnostics.CaptureCurrentProcessByType();

            if (g3ExitCode == 0)
            {
                g8ExitCode = await new G8P1AcceptanceSuite(
                    services,
                    options with
                    {
                        Suite = HarnessSuite.G8,
                        ReportPath = g8ReportPath
                    }).RunAsync();
            }
            else
            {
                _failures.Add("PHASE4_G3_FAILED");
            }
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= unhandledHandler;
            TaskScheduler.UnobservedTaskException -= unobservedHandler;
        }

        if (g8ExitCode != 0)
        {
            _failures.Add("PHASE4_G8_FAILED");
        }

        await StabilizeProcessAsync();
        CapturePeak();
        var processFinal = ProcessResourceSnapshot.Capture();
        var handleTypesFinal = WindowsHandleDiagnostics.CaptureCurrentProcessByType();
        var surfaceFinal = EmbeddedVideoSurface.CaptureDiagnostics();
        var finalResources = SecurePlaybackDiagnostics.CaptureResources();
        var childFailures = ReadChildFailures(g3ReportPath)
            .Concat(ReadChildFailures(g8ReportPath))
            .ToArray();
        var timeoutCount = childFailures.Count(failure =>
            failure.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("超时", StringComparison.Ordinal));
        var voutErrorCount = childFailures.Count(failure =>
            failure.Contains("vout", StringComparison.OrdinalIgnoreCase));
        var blackScreenCount = childFailures.Count(failure =>
            failure.Contains("black", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("黑屏", StringComparison.Ordinal));

        Require(finalResources == default, "PHASE4_PLAYBACK_RESOURCES_NOT_ZERO");
        Require(surfaceFinal.ActiveCount == 0, "PHASE4_SURFACE_ACTIVE_NOT_ZERO");
        Require(
            surfaceFinal.CreatedCount - surfaceStart.CreatedCount ==
            surfaceFinal.DestroyedCount - surfaceStart.DestroyedCount,
            "PHASE4_SURFACE_CREATE_DESTROY_MISMATCH");
        Require(
            processFinal.HandleCount <= processStart.HandleCount + 10,
            "PHASE4_HANDLE_LIMIT_EXCEEDED");
        Require(
            processFinal.PrivateBytes <= processStart.PrivateBytes + PrivateMemoryAllowanceBytes,
            "PHASE4_PRIVATE_MEMORY_LIMIT_EXCEEDED");
        Require(
            Volatile.Read(ref _unhandledExceptionCount) == 0,
            "PHASE4_UNHANDLED_EXCEPTION");
        Require(timeoutCount == 0, "PHASE4_TIMEOUT");
        Require(voutErrorCount == 0, "PHASE4_VOUT_ERROR");
        Require(blackScreenCount == 0, "PHASE4_BLACK_SCREEN");

        var report = new Phase4Report(
            1,
            "avalonia12-libvlcsharp-runtime-gate",
            _failures.Count == 0 && g3ExitCode == 0 && g8ExitCode == 0,
            DateTimeOffset.UtcNow,
            source.Revision,
            source.WorktreeClean,
            "pending",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(Dock.Model.Core.IDockable).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(LibVLCSharp.Shared.LibVLC).Assembly.GetName().Version?.ToString() ?? "unknown",
            GetNativeLibVlcVersion(),
            warmupPassed,
            threadPoolWarmupWorkerCount,
            new HwndGateReport(
                surfaceFinal.HandleDescriptor,
                surfaceFinal.LastHandleWasNonZero),
            surfaceFinal.CreatedCount - surfaceStart.CreatedCount,
            surfaceFinal.DestroyedCount - surfaceStart.DestroyedCount,
            processStart,
            new ProcessPeakSnapshot(_peakPrivateBytes, _peakHandleCount),
            processFinal,
            handleTypesStart,
            handleTypesAfterG3,
            handleTypesFinal,
            WindowsHandleDiagnostics.CreateDelta(
                handleTypesStart,
                handleTypesAfterG3),
            WindowsHandleDiagnostics.CreateDelta(
                handleTypesAfterG3,
                handleTypesFinal),
            WindowsHandleDiagnostics.CreateDelta(handleTypesStart, handleTypesFinal),
            finalResources,
            Volatile.Read(ref _unhandledExceptionCount),
            blackScreenCount,
            voutErrorCount,
            timeoutCount,
            Path.GetFileName(g3ReportPath),
            Path.GetFileName(g8ReportPath),
            _failures.Concat(childFailures).Distinct(StringComparer.Ordinal).ToArray());
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

        Console.WriteLine($"Phase 4 report: {reportPath}");
        return report.Success ? 0 : 1;
    }

    private async Task<bool> WarmUpRuntimeAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"videosecurityplayer-phase4-warmup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var g3ExitCode = await new G3PlaybackHarnessRunner(
                services,
                options with
                {
                    Suite = HarnessSuite.G3,
                    ReportPath = Path.Combine(directory, "warmup-g3.json")
                }).RunAsync();
            // G3 与 G8 会初始化不同的 Avalonia、Dock 和线程池路径。即使 G3 已失败，
            // 仍须完成 G8 预热；最终结果继续保留 NO-GO，但正式资源基线不会混入 G8
            // 首次初始化成本，便于准确区分兼容性失败与重复生命周期泄漏。
            var g8ExitCode = await new G8P1AcceptanceSuite(
                services,
                options with
                {
                    Suite = HarnessSuite.G8,
                    ReportPath = Path.Combine(directory, "warmup-g8.json")
                }).RunAsync();
            Require(g3ExitCode == 0, "PHASE4_WARMUP_G3_FAILED");
            Require(g8ExitCode == 0, "PHASE4_WARMUP_G8_FAILED");
            return g3ExitCode == 0 && g8ExitCode == 0;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<int> WarmUpThreadPoolAsync()
    {
        ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out _);
        var targetWorkers = Math.Min(
            maximumWorkers,
            Math.Max(
                minimumWorkers,
                Math.Clamp(Environment.ProcessorCount * 2, 16, 64)));
        if (!ThreadPool.SetMinThreads(targetWorkers, minimumIo))
        {
            throw new InvalidOperationException("无法设置阶段 4 ThreadPool 预热下限。");
        }

        // 长生命周期原生调度任务会触发 ThreadPool 渐进扩容。基线前用一次受控并发
        // 建立工作线程基础设施，避免把运行时池扩容误报为播放器句柄泄漏。
        using var ready = new CountdownEvent(targetWorkers);
        using var release = new ManualResetEventSlim(initialState: false);
        var workers = Enumerable.Range(0, targetWorkers)
            .Select(_ => Task.Run(() =>
            {
                ready.Signal();
                release.Wait();
            }))
            .ToArray();
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("阶段 4 ThreadPool 预热未在限时内就绪。");
            }
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(workers);
        return targetWorkers;
    }

    private void CapturePeak()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            InterlockedExtensions.Max(ref _peakPrivateBytes, process.PrivateMemorySize64);
            InterlockedExtensions.Max(ref _peakHandleCount, process.HandleCount);
        }
        catch (InvalidOperationException)
        {
            // 采样与进程退出竞争时不影响最终的起点/终点硬闸门。
        }
    }

    private void Require(bool condition, string code)
    {
        if (!condition)
        {
            _failures.Add(code);
        }
    }

    private static IEnumerable<string> ReadChildFailures(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (!document.RootElement.TryGetProperty("Failures", out var failures) &&
            !document.RootElement.TryGetProperty("FailedScenarioCodes", out failures))
        {
            return [];
        }

        return failures.ValueKind == JsonValueKind.Array
            ? failures.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : [];
    }

    private static async Task StabilizeProcessAsync()
    {
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ThreadPool 会为原生播放回调临时扩容。必须留出退坡时间，并要求句柄和线程
        // 连续稳定后再采样，避免把仍在运行的清理线程误判为最终资源。
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Stopwatch.StartNew();
        var stableSamples = 0;
        var lastHandleCount = -1;
        var lastThreadCount = -1;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += OnTick;
        timer.Start();
        try
        {
            await completion.Task;
        }
        finally
        {
            timer.Stop();
            timer.Tick -= OnTick;
        }

        void OnTick(object? sender, EventArgs args)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                if (process.HandleCount == lastHandleCount &&
                    process.Threads.Count == lastThreadCount)
                {
                    stableSamples++;
                }
                else
                {
                    stableSamples = 0;
                    lastHandleCount = process.HandleCount;
                    lastThreadCount = process.Threads.Count;
                }

                if (started.Elapsed >= TimeSpan.FromSeconds(30) ||
                    started.Elapsed >= TimeSpan.FromSeconds(15) &&
                    stableSamples >= 3)
                {
                    completion.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static SourceState ReadSourceState()
    {
        var directory = FindRepositoryRoot();
        if (directory is null)
        {
            return new("unknown", false);
        }

        var revision = RunGit(directory, "rev-parse HEAD");
        var status = RunGit(directory, "status --porcelain");
        return new(
            string.IsNullOrWhiteSpace(revision) ? "unknown" : revision.Trim(),
            status is not null && string.IsNullOrWhiteSpace(status));
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5_000) && process.ExitCode == 0
                ? output
                : null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string GetNativeLibVlcVersion()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "native",
            "win-x64",
            "libvlc",
            "libvlc.dll");
        return File.Exists(path)
            ? FileVersionInfo.GetVersionInfo(path).ProductVersion ??
              FileVersionInfo.GetVersionInfo(path).FileVersion ??
              "unknown"
            : "unavailable";
    }

    private readonly record struct SourceState(string Revision, bool WorktreeClean);

    private sealed class DispatcherResourceSampler : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _handler;

        public DispatcherResourceSampler(Action sample)
        {
            _handler = (_, _) => sample();
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += _handler;
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= _handler;
        }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public static void Max(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

internal sealed record Phase4Report(
    int SchemaVersion,
    string Kind,
    bool Success,
    DateTimeOffset ExecutedAtUtc,
    string SourceRevision,
    bool WorktreeClean,
    string ManualSignoff,
    string OperatingSystem,
    string Architecture,
    string DotNetVersion,
    string AvaloniaVersion,
    string DockVersion,
    string LibVlcSharpVersion,
    string NativeLibVlcVersion,
    bool WarmupPassed,
    int ThreadPoolWarmupWorkerCount,
    HwndGateReport Hwnd,
    long SurfaceCreatedCount,
    long SurfaceDestroyedCount,
    ProcessResourceSnapshot ProcessStart,
    ProcessPeakSnapshot ProcessPeak,
    ProcessResourceSnapshot ProcessFinal,
    IReadOnlyDictionary<string, int> HandleTypesStart,
    IReadOnlyDictionary<string, int> HandleTypesAfterG3,
    IReadOnlyDictionary<string, int> HandleTypesFinal,
    IReadOnlyDictionary<string, int> G3HandleTypeDeltas,
    IReadOnlyDictionary<string, int> G8HandleTypeDeltas,
    IReadOnlyDictionary<string, int> HandleTypeDeltas,
    PlaybackResourceSnapshot FinalPlaybackResources,
    int UnhandledExceptionCount,
    int BlackScreenCount,
    int VoutErrorCount,
    int TimeoutCount,
    string G3Report,
    string G8Report,
    IReadOnlyList<string> Failures);

internal sealed record HwndGateReport(
    string Descriptor,
    bool NonZero);

internal sealed record ProcessPeakSnapshot(
    long PrivateBytes,
    int HandleCount);
