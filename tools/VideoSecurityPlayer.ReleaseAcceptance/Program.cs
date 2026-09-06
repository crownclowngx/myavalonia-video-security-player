using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

return await ReleaseAcceptanceProgram.RunAsync(args);

/// <summary>
/// G4 独立验收进程。它不依赖测试运行器，也不启动 Avalonia：
/// 部署探针验证“交付目录是否完整”，内存门禁验证“数据量增长时峰值内存是否保持有界”。
/// 真实 LibVLC、HWND 和 Dock 行为继续由现有 Playback.IntegrationHarness 负责，
/// 从而让每个门禁只承担一种失败语义，报告也更容易定位问题。
/// </summary>
internal static class ReleaseAcceptanceProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (Has(args, "--native-lifetime"))
            {
                return NativeLifetimeProbe.Run(
                    Required(args, "--native-lifetime"),
                    OptionalInt(args, "--rounds", 12),
                    Required(args, "--report"));
            }

            if (Has(args, "--probe"))
            {
                return RunProbe(Required(args, "--probe"), Required(args, "--report"));
            }

            if (Has(args, "--memory-child"))
            {
                var sizeMiB = int.Parse(Required(args, "--memory-child"));
                return await RunMemoryChildAsync(sizeMiB, Required(args, "--report"));
            }

            if (Has(args, "--memory"))
            {
                var smallMiB = OptionalInt(args, "--small-mib", 64);
                var largeMiB = OptionalInt(args, "--large-mib", 512);
                return await RunMemoryGateAsync(
                    smallMiB,
                    largeMiB,
                    Required(args, "--report"));
            }

            Console.Error.WriteLine(
                "Usage: --probe <plugin-root> --report <json> | " +
                "--memory [--small-mib 64 --large-mib 512] --report <json> | " +
                "--native-lifetime <isolated|overlap> [--rounds 12] --report <json>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int RunProbe(string pluginRoot, string reportPath)
    {
        var result = new PlaybackDeploymentProbe(
                pluginRoot,
                () => OperatingSystem.IsWindows(),
                () => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
            .Check();
        WriteJson(
            reportPath,
            new
            {
                schemaVersion = 1,
                kind = "deployment-probe",
                result.IsReady,
                issues = result.Issues.Select(issue => new
                {
                    code = issue.Code.ToString(),
                    issue.Summary,
                    checkedPath = Path.GetRelativePath(
                        result.PluginDirectory,
                        issue.CheckedPath).Replace('\\', '/'),
                    issue.SuggestedAction
                })
            });
        return result.IsReady ? 0 : 1;
    }

    private static async Task<int> RunMemoryGateAsync(
        int smallMiB,
        int largeMiB,
        string reportPath)
    {
        if (smallMiB <= 0 || largeMiB <= smallMiB)
        {
            throw new ArgumentOutOfRangeException(
                nameof(largeMiB),
                "Large input must be greater than the positive small input.");
        }

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var smallReport = Path.Combine(reportDirectory, $".memory-{smallMiB}.json");
        var largeReport = Path.Combine(reportDirectory, $".memory-{largeMiB}.json");
        try
        {
            await RunChildAsync(smallMiB, smallReport);
            await RunChildAsync(largeMiB, largeReport);

            var small = ReadMemoryResult(smallReport);
            var large = ReadMemoryResult(largeReport);
            // 比较的是“场景开始后的增量”，而不是机器的绝对工作集。512 MiB 输入是
            // 64 MiB 的八倍，但允许的额外 managed/private 峰值只有固定 64/128 MiB；
            // 这样既能拒绝把整个文件读入内存的回归，又不会把运行时、杀毒软件和
            // 文件系统缓存造成的小幅跨机器差异误判为产品故障。
            var managedLimit = small.ManagedHeapPeakDeltaBytes + 64L * 1024 * 1024;
            var privateLimit = small.PrivateBytesPeakDeltaBytes + 128L * 1024 * 1024;
            var passed = small.Passed &&
                         large.Passed &&
                         large.ManagedHeapPeakDeltaBytes <= managedLimit &&
                         large.PrivateBytesPeakDeltaBytes <= privateLimit;

            WriteJson(
                reportPath,
                new MemoryGateReport(
                    1,
                    "p0-large-file-memory",
                    passed,
                    small,
                    large,
                    managedLimit,
                    privateLimit));
            return passed ? 0 : 1;
        }
        finally
        {
            TryDelete(smallReport);
            TryDelete(largeReport);
        }
    }

    private static async Task RunChildAsync(int sizeMiB, string report)
    {
        // 小/大场景必须使用不同进程。若在同一进程顺序执行，前一轮扩张后的 GC segment
        // 和文件缓存会被后一轮继承，峰值差值将无法代表该输入规模自身的内存需求。
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Cannot locate acceptance executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--memory-child");
        startInfo.ArgumentList.Add(sizeMiB.ToString());
        startInfo.ArgumentList.Add("--report");
        startInfo.ArgumentList.Add(report);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Cannot start memory child.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Memory child for {sizeMiB} MiB exited with {process.ExitCode}.");
        }
    }

    private static async Task<int> RunMemoryChildAsync(int sizeMiB, string reportPath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-G4-Memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.mp4");
        var encrypted = Path.Combine(root, "source.secvid");
        var decrypted = Path.Combine(root, "decrypted.mp4");
        const string password = "G4 acceptance password";
        MemorySampler? sampler = null;
        try
        {
            // 先流式生成输入，再强制完成一次 GC，把输入文件生成成本排除出产品门禁。
            // 随后用同一个采样器覆盖加密、解密和随机读取三条 P0 主链路。
            await GenerateSourceAsync(source, (long)sizeMiB * 1024 * 1024);
            ForceCollection();
            sampler = new MemorySampler();
            sampler.Start();

            await new Secvid03Encryptor().EncryptAsync(
                source,
                encrypted,
                password,
                "G4 memory sample",
                string.Empty);
            await new Secvid03Decryptor().DecryptAsync(
                encrypted,
                decrypted,
                password);
            await VerifyRandomReadsAsync(source, encrypted, password);
            sampler.Stop();

            var sourceHash = await HashAsync(source);
            var decryptedHash = await HashAsync(decrypted);
            var partials = Directory.EnumerateFiles(root, "*.partial-*").Count();
            var cache = SecurePlaybackDiagnostics.CaptureResources().CachedPlaintextChunks;
            var filesUnlocked = CanOpenExclusive(source) &&
                                CanOpenExclusive(encrypted) &&
                                CanOpenExclusive(decrypted);
            var passed = sourceHash == decryptedHash &&
                         partials == 0 &&
                         cache <= 4 &&
                         filesUnlocked;
            var result = new MemoryScenarioResult(
                sizeMiB,
                passed,
                sampler.ManagedHeapPeakDeltaBytes,
                sampler.PrivateBytesPeakDeltaBytes,
                cache,
                partials,
                filesUnlocked,
                sourceHash == decryptedHash);
            WriteJson(reportPath, result);
            return passed ? 0 : 1;
        }
        finally
        {
            sampler?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task GenerateSourceAsync(string path, long length)
    {
        // 数据缓冲区固定为 1 MiB并循环写入，不会因为目标文件变大而扩张。
        // 前 8 字节伪装为最小 MP4 ftyp 签名，只用于走与真实视频一致的前缀识别路径；
        // 此门禁验证容器 I/O 和内存，不把“能否解码”与内存结论耦合。
        var buffer = new byte[1024 * 1024];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = ExpectedByte(index);
        }
        buffer[0] = 0;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 32;
        "ftyp"u8.CopyTo(buffer.AsSpan(4));

        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(buffer.Length, length - written);
            await output.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
        await output.FlushAsync();
        output.Flush(flushToDisk: true);
    }

    private static async Task VerifyRandomReadsAsync(
        string sourcePath,
        string encryptedPath,
        string password)
    {
        var length = new FileInfo(sourcePath).Length;
        // 固定位置覆盖文件首尾、SECVID03 块边界两侧；固定随机种子再补足 128 次，
        // 使失败可以复现，同时迫使四块 LRU 在远距离 Seek 中持续淘汰旧明文块。
        var positions = new List<long>
        {
            0,
            31,
            Secvid03Format.ChunkSize - 16,
            Secvid03Format.ChunkSize,
            Secvid03Format.ChunkSize + 17,
            Math.Max(0, length - 65536)
        };
        var random = new Random(0x534543);
        while (positions.Count < 128)
        {
            positions.Add(random.NextInt64(Math.Max(1, length - 65536)));
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var encrypted = SeekableEncryptedVideoStream.Open(encryptedPath, password);
        var expected = new byte[65536];
        var actual = new byte[65536];
        foreach (var position in positions)
        {
            source.Position = position;
            encrypted.Position = position;
            var count = (int)Math.Min(expected.Length, length - position);
            await source.ReadExactlyAsync(expected.AsMemory(0, count));
            await encrypted.ReadExactlyAsync(actual.AsMemory(0, count));
            if (!expected.AsSpan(0, count).SequenceEqual(actual.AsSpan(0, count)))
            {
                throw new InvalidDataException($"Random read mismatch at {position}.");
            }
        }
    }

    private static byte ExpectedByte(long position) =>
        (byte)((position * 31 + position / 997 + 17) & 0xff);

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static MemoryScenarioResult ReadMemoryResult(string path) =>
        JsonSerializer.Deserialize<MemoryScenarioResult>(
            File.ReadAllText(path),
            JsonOptions) ?? throw new InvalidDataException("Invalid memory report.");

    private static void WriteJson(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool Has(string[] args, string name) =>
        Array.IndexOf(args, name) >= 0;

    private static string Required(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing {name}.");
        }
        return args[index + 1];
    }

    private static int OptionalInt(string[] args, string name, int fallback) =>
        Has(args, name) ? int.Parse(Required(args, name)) : fallback;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class MemorySampler : IDisposable
    {
        // HeapSizeBytes 反映托管堆实际扩张，PrivateMemorySize64 同时覆盖非托管缓冲区。
        // 两者每 100 ms 采样并记录峰值；这里只保存数字，不保存临时路径或媒体内容。
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private Task? _sampling;
        private long _managedBaseline;
        private long _privateBaseline;
        private long _managedPeak;
        private long _privatePeak;

        public long ManagedHeapPeakDeltaBytes =>
            Math.Max(0, _managedPeak - _managedBaseline);
        public long PrivateBytesPeakDeltaBytes =>
            Math.Max(0, _privatePeak - _privateBaseline);

        public void Start()
        {
            _managedBaseline = GC.GetGCMemoryInfo().HeapSizeBytes;
            _process.Refresh();
            _privateBaseline = _process.PrivateMemorySize64;
            _managedPeak = _managedBaseline;
            _privatePeak = _privateBaseline;
            _sampling = Task.Run(SampleAsync);
        }

        public void Stop()
        {
            _cancellation.Cancel();
            try
            {
                _sampling?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            Sample();
        }

        private async Task SampleAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(100, _cancellation.Token);
            }
        }

        private void Sample()
        {
            InterlockedExtensions.Max(
                ref _managedPeak,
                GC.GetGCMemoryInfo().HeapSizeBytes);
            _process.Refresh();
            InterlockedExtensions.Max(ref _privatePeak, _process.PrivateMemorySize64);
        }

        public void Dispose()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                Stop();
            }
            _cancellation.Dispose();
            _process.Dispose();
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref location, value, current);
                if (previous == current)
                {
                    return;
                }
                current = previous;
            }
        }
    }

    private sealed record MemoryScenarioResult(
        int SizeMiB,
        bool Passed,
        long ManagedHeapPeakDeltaBytes,
        long PrivateBytesPeakDeltaBytes,
        int CachedPlaintextChunks,
        int PartialFileCount,
        bool FilesUnlocked,
        bool HashMatched);

    private sealed record MemoryGateReport(
        int SchemaVersion,
        string Kind,
        bool Passed,
        MemoryScenarioResult Small,
        MemoryScenarioResult Large,
        long ManagedHeapLimitBytes,
        long PrivateBytesLimitBytes);
}
