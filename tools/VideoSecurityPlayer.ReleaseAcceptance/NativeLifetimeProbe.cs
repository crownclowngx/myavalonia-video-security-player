using System.Runtime.InteropServices;
using System.Text.Json;
using LibVLCSharp.Shared;
using VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>
/// 在独立验收进程中复现原生引擎生命周期的句柄变化，不创建 UI、MediaPlayer 或媒体。
/// </summary>
/// <remarks>
/// isolated 每轮完整创建/释放引擎；overlap 让一个守护引擎存活，再创建/释放其他实例。
/// 两组对照用于区分“每个实例都泄漏”和“全局运行时从零重新初始化时增长”。
/// 诊断只统计当前进程，不枚举或关闭别的进程句柄，也不改变生产生命周期与资源阈值。
/// </remarks>
internal static class NativeLifetimeProbe
{
    public static int Run(string mode, int rounds, string reportPath)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("原生生命周期诊断仅支持 Windows x64。");
        if (mode is not ("isolated" or "overlap"))
            throw new ArgumentException("--native-lifetime 仅支持 isolated 或 overlap。", nameof(mode));
        if (rounds is < 2 or > 100)
            throw new ArgumentOutOfRangeException(nameof(rounds), "轮数必须在 2 到 100 之间。");

        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "native", "win-x64", "libvlc");
        Core.Initialize(runtimeDirectory);
        var module = NativeLibrary.Load(Path.Combine(runtimeDirectory, "libvlc.dll"));
        var samples = new List<object>();
        IReadOnlyDictionary<string, int> final;
        try
        {
            // 直接调用已加载 DLL 的 C API，绕过托管 LibVLC 包装对象，以隔离业务和绑定层。
            var create = Marshal.GetDelegateForFunctionPointer<CreateInstance>(
                NativeLibrary.GetExport(module, "libvlc_new"));
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseInstance>(
                NativeLibrary.GetExport(module, "libvlc_release"));
            var guard = mode == "overlap" ? CreateChecked(create) : IntPtr.Zero;
            try
            {
                for (var round = 0; round < rounds; round++)
                {
                    var before = WindowsHandleDiagnostics.CaptureCurrentProcessByType();
                    var instance = CreateChecked(create);
                    release(instance);
                    var after = WindowsHandleDiagnostics.CaptureCurrentProcessByType();
                    samples.Add(new
                    {
                        round,
                        semaphoresBefore = before.GetValueOrDefault("Semaphore"),
                        semaphoresAfter = after.GetValueOrDefault("Semaphore"),
                        delta = WindowsHandleDiagnostics.CreateDelta(before, after)
                    });
                }
            }
            finally
            {
                if (guard != IntPtr.Zero) release(guard);
            }
            final = WindowsHandleDiagnostics.CaptureCurrentProcessByType();
        }
        finally
        {
            // 只平衡本方法 NativeLibrary.Load 的引用，Core.Initialize 的加载责任仍归其自身。
            NativeLibrary.Free(module);
        }

        var absoluteReport = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteReport)!);
        File.WriteAllText(absoluteReport, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "libvlc-native-lifetime-diagnostic",
            mode,
            rounds,
            completed = true,
            samples,
            final
        }, new JsonSerializerOptions { WriteIndented = true }));
        // 退出码只表示诊断是否执行完成，故意不使用 passed/success 冒充资源门禁。
        Console.WriteLine($"原生生命周期诊断完成：{absoluteReport}");
        return 0;
    }

    private static IntPtr CreateChecked(CreateInstance create)
    {
        var instance = create(0, IntPtr.Zero);
        if (instance == IntPtr.Zero)
            throw new InvalidOperationException("原生 libvlc_new 创建失败。");
        return instance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInstance(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseInstance(IntPtr instance);
}
