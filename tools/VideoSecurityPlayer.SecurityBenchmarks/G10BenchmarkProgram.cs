using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Models.SecretVideoPlayer;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

internal static class G10BenchmarkProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (Has(args, "--g10-aggregate"))
                return G10BaselineComparer.RunAggregate(args);
            if (Has(args, "--g10-compare"))
                return G10BaselineComparer.RunCompare(args);
            if (Has(args, "--g10-child"))
                return await RunCryptoChildAsync(args);

            var options = G10Options.Parse(args);
            var root = Path.Combine(
                Path.GetTempPath(),
                "VideoSecurityPlayer-G10-Benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var smallPath = Path.Combine(root, "crypto-small.json");
                var largePath = Path.Combine(root, "crypto-large.json");
                await RunChildProcessAsync(
                    options.SmallMiB,
                    options.SmallIterations,
                    options.SeekCount,
                    smallPath);
                await RunChildProcessAsync(
                    options.LargeMiB,
                    options.LargeIterations,
                    options.SeekCount,
                    largePath);

                var small = Read<CryptoScenarioReport>(smallPath);
                var large = Read<CryptoScenarioReport>(largePath);
                var library = await RunLibrarySuiteAsync(root, options);
                var hardGate = EvaluateHardGate(small, large, library);
                var environment = EnvironmentReport.Capture();
                var report = new G10BenchmarkReport(
                    1,
                    "g10-performance",
                    DateTimeOffset.UtcNow,
                    environment,
                    environment.CreateComparableFingerprint(),
                    options,
                    small,
                    large,
                    library,
                    hardGate,
                    hardGate.Passed);

                WriteJson(options.OutputPath, report);
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return report.Success ? 0 : 1;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCryptoChildAsync(string[] args)
    {
        var sizeMiB = RequiredInt(args, "--size-mib");
        var iterations = RequiredInt(args, "--iterations");
        var seekCount = RequiredInt(args, "--seek-count");
        var output = Required(args, "--output");
        if (sizeMiB < 8 || iterations <= 0 || seekCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args));

        var root = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-G10-Crypto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            await GenerateSourceAsync(source, (long)sizeMiB * 1024 * 1024);
            await WarmUpAsync(root);
            ForceCollection();

            using var sampler = new ResourceSampler();
            sampler.Start();
            var encryptMs = new List<double>(iterations);
            var decryptMs = new List<double>(iterations);
            string? lastEncrypted = null;
            string? lastDecrypted = null;
            for (var index = 0; index < iterations; index++)
            {
                var encrypted = Path.Combine(root, $"encrypted-{index}.secvid");
                var decrypted = Path.Combine(root, $"decrypted-{index}.mp4");
                var started = Stopwatch.GetTimestamp();
                await new Secvid03Encryptor().EncryptAsync(
                    source,
                    encrypted,
                    "G10 benchmark password",
                    string.Empty,
                    string.Empty);
                encryptMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                started = Stopwatch.GetTimestamp();
                await new Secvid03Decryptor().DecryptAsync(
                    encrypted,
                    decrypted,
                    "G10 benchmark password");
                decryptMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                if (lastEncrypted is not null)
                {
                    File.Delete(lastEncrypted);
                    File.Delete(lastDecrypted!);
                }
                lastEncrypted = encrypted;
                lastDecrypted = decrypted;
            }

            var seek = MeasureRandomSeeks(
                lastEncrypted!,
                "G10 benchmark password",
                seekCount);
            var cache = MeasureCache(lastEncrypted!, "G10 benchmark password");
            sampler.Stop();

            var hashesMatch = await HashAsync(source) == await HashAsync(lastDecrypted!);
            var partialCount = Directory.EnumerateFiles(root, "*.partial-*").Count();
            var filesUnlocked = CanOpenExclusive(source) &&
                                CanOpenExclusive(lastEncrypted!) &&
                                CanOpenExclusive(lastDecrypted!);
            var encryptThroughput = encryptMs
                .Select(ms => sizeMiB / (ms / 1000d))
                .ToArray();
            var decryptThroughput = decryptMs
                .Select(ms => sizeMiB / (ms / 1000d))
                .ToArray();

            var report = new CryptoScenarioReport(
                sizeMiB,
                iterations,
                Metric.From(encryptMs, "ms"),
                Metric.From(encryptThroughput, "MiB/s"),
                Metric.From(decryptMs, "ms"),
                Metric.From(decryptThroughput, "MiB/s"),
                seek,
                cache,
                sampler.Capture(),
                hashesMatch,
                partialCount,
                filesUnlocked,
                hashesMatch && partialCount == 0 && filesUnlocked &&
                cache.MaximumActiveChunks <= 4 &&
                cache.Requests == 7 &&
                cache.Hits == 1 &&
                cache.Misses == 6 &&
                cache.Evictions == 2);
            WriteJson(output, report);
            return report.Passed ? 0 : 1;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunChildProcessAsync(
        int sizeMiB,
        int iterations,
        int seekCount,
        string reportPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.ArgumentList.Add("--g10-child");
        startInfo.ArgumentList.Add("--size-mib");
        startInfo.ArgumentList.Add(sizeMiB.ToString());
        startInfo.ArgumentList.Add("--iterations");
        startInfo.ArgumentList.Add(iterations.ToString());
        startInfo.ArgumentList.Add("--seek-count");
        startInfo.ArgumentList.Add(seekCount.ToString());
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(reportPath);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 G10 子进程。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"G10 {sizeMiB} MiB 子进程失败。");
    }

    private static async Task WarmUpAsync(string root)
    {
        var source = Path.Combine(root, "warmup.mp4");
        var encrypted = Path.Combine(root, "warmup.secvid");
        var decrypted = Path.Combine(root, "warmup-out.mp4");
        await GenerateSourceAsync(source, 8L * 1024 * 1024);
        await new Secvid03Encryptor().EncryptAsync(
            source,
            encrypted,
            "G10 warmup password",
            string.Empty,
            string.Empty);
        await new Secvid03Decryptor().DecryptAsync(
            encrypted,
            decrypted,
            "G10 warmup password");
        File.Delete(source);
        File.Delete(encrypted);
        File.Delete(decrypted);
    }

    private static Metric MeasureRandomSeeks(
        string encryptedPath,
        string password,
        int count)
    {
        using var stream = SeekableEncryptedVideoStream.Open(encryptedPath, password);
        var random = new Random(0x473130);
        var buffer = new byte[64 * 1024];
        var samples = new double[count];
        for (var index = 0; index < samples.Length; index++)
        {
            var maximum = Math.Max(1, stream.Length - buffer.Length);
            stream.Position = random.NextInt64(maximum);
            var started = Stopwatch.GetTimestamp();
            stream.ReadExactly(buffer);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return Metric.From(samples, "ms");
    }

    private static CacheScenarioReport MeasureCache(string encryptedPath, string password)
    {
        SecurePlaybackDiagnostics.ResetCacheStatistics();
        var maximumActive = 0;
        using (var stream = SeekableEncryptedVideoStream.Open(encryptedPath, password))
        {
            var oneByte = new byte[1];
            var body = stream.DiagnosticSummary.OriginalHeaderLength;
            ReadAt(stream, body, oneByte);
            ReadAt(stream, body, oneByte);
            for (var chunk = 1; chunk <= 4; chunk++)
            {
                ReadAt(stream, body + chunk * (long)Secvid03Format.ChunkSize, oneByte);
                maximumActive = Math.Max(
                    maximumActive,
                    SecurePlaybackDiagnostics.CaptureResources().CachedPlaintextChunks);
            }
            ReadAt(stream, body, oneByte);
        }
        var statistics = SecurePlaybackDiagnostics.CaptureCacheStatistics();
        return new CacheScenarioReport(
            statistics.Requests,
            statistics.Hits,
            statistics.Misses,
            statistics.Evictions,
            maximumActive);
    }

    private static async Task<LibrarySuiteReport> RunLibrarySuiteAsync(
        string root,
        G10Options options)
    {
        var fixtureSource = Path.Combine(root, "library-source.mp4");
        var fixture = Path.Combine(root, "library-fixture.secvid");
        await GenerateSourceAsync(fixtureSource, 4096);
        await new Secvid03Encryptor().EncryptAsync(
            fixtureSource,
            fixture,
            "G10 library password",
            string.Empty,
            string.Empty);

        var smallRoot = Path.Combine(root, "library-small");
        var largeRoot = Path.Combine(root, "library-large");
        CopyFixture(fixture, smallRoot, options.LibrarySmall);
        CopyFixture(fixture, largeRoot, options.LibraryLarge);

        using var sampler = new ResourceSampler();
        sampler.Start();
        var scanner = new VideoLibraryScanner();
        var smallFirst = await ScanAsync(scanner, smallRoot);
        var smallHot = await ScanAsync(scanner, smallRoot);
        var largeFirst = await ScanAsync(scanner, largeRoot);
        var largeHot = await ScanAsync(scanner, largeRoot);
        var projection = await MeasureProjectionAsync(
            largeFirst.Results,
            largeRoot,
            fixture,
            options.LibraryLarge);
        var watcher = await MeasureWatcherAsync(
            scanner,
            largeRoot,
            fixture,
            options.StormEvents,
            options.LibraryLarge);
        sampler.Stop();

        var passed = HasExactUniqueCount(smallFirst.Results, options.LibrarySmall) &&
                     HasExactUniqueCount(smallHot.Results, options.LibrarySmall) &&
                     HasExactUniqueCount(largeFirst.Results, options.LibraryLarge) &&
                     HasExactUniqueCount(largeHot.Results, options.LibraryLarge) &&
                     projection.Passed &&
                     watcher.Passed;
        return new LibrarySuiteReport(
            options.LibrarySmall,
            options.LibraryLarge,
            smallFirst.ElapsedMs,
            smallHot.ElapsedMs,
            largeFirst.ElapsedMs,
            largeHot.ElapsedMs,
            projection,
            watcher,
            sampler.Capture(),
            passed);
    }

    private static async Task<ScanResult> ScanAsync(
        VideoLibraryScanner scanner,
        string root)
    {
        var results = new List<VideoLibraryScanResult>();
        var started = Stopwatch.GetTimestamp();
        await foreach (var item in scanner.ScanAsync(
                           root,
                           VideoLibraryScanOptions.TopDirectoryOnly,
                           CancellationToken.None))
        {
            results.Add(item);
        }
        return new ScanResult(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            results);
    }

    private static bool HasExactUniqueCount(
        IReadOnlyList<VideoLibraryScanResult> results,
        int expected) =>
        results.Count == expected &&
        results.Select(result => Path.GetFullPath(result.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == expected;

    private static async Task<ProjectionReport> MeasureProjectionAsync(
        IReadOnlyList<VideoLibraryScanResult> results,
        string root,
        string fixture,
        int initialCount)
    {
        using var viewModel = new LibraryBrowserCoordinatorViewModel(
            new VideoLibraryScanner(),
            NonClosingDocumentLifetime.Instance,
            catalog: new FixedCatalog(results));
        await viewModel.LoadFolderAsync("g10-virtual-library");

        var searchStarted = Stopwatch.GetTimestamp();
        viewModel.SearchText = $"{results.Count - 1:D4}";
        await WaitUntilAsync(() => viewModel.VisibleItemCount == 1, TimeSpan.FromSeconds(5));
        var searchMs = Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds;

        viewModel.SearchText = string.Empty;
        await WaitUntilAsync(
            () => viewModel.VisibleItemCount == results.Count,
            TimeSpan.FromSeconds(5));

        var sortSamples = new List<double>();
        var sortOrderCorrect = true;
        foreach (var field in Enum.GetValues<VideoLibrarySortField>())
        {
            foreach (var direction in Enum.GetValues<VideoLibrarySortDirection>())
            {
                if (viewModel.SortField == field && viewModel.SortDirection == direction)
                {
                    sortOrderCorrect &= IsSorted(
                        viewModel.CaptureVisibleItems(),
                        field,
                        direction);
                    continue;
                }
                var changed = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                NotifyCollectionChangedEventHandler handler = (_, _) =>
                    changed.TrySetResult();
                ((INotifyCollectionChanged)viewModel.VisibleItems).CollectionChanged += handler;
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    viewModel.SortField = field;
                    viewModel.SortDirection = direction;
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    sortSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    sortOrderCorrect &= IsSorted(
                        viewModel.CaptureVisibleItems(),
                        field,
                        direction);
                }
                finally
                {
                    ((INotifyCollectionChanged)viewModel.VisibleItems).CollectionChanged -= handler;
                }
            }
        }

        var unique = viewModel.CaptureVisibleItems()
            .Select(item => item.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var incremental = await MeasureIncrementalProjectionAsync(
            root,
            fixture,
            initialCount);
        return new ProjectionReport(
            150,
            searchMs,
            Metric.From(sortSamples, "ms"),
            viewModel.VisibleItemCount,
            incremental,
            unique == viewModel.VisibleItemCount &&
            viewModel.VisibleItemCount == results.Count &&
            sortOrderCorrect &&
            incremental.Passed);
    }

    private static async Task<IncrementalProjectionReport> MeasureIncrementalProjectionAsync(
        string root,
        string fixture,
        int initialCount)
    {
        using var viewModel = new LibraryBrowserCoordinatorViewModel(
            new VideoLibraryScanner(),
            NonClosingDocumentLifetime.Instance);
        var catalogTask = viewModel.LoadFolderAsync(root);
        var added = Path.Combine(root, "incremental-added.secvid");
        var renamed = Path.Combine(root, "incremental-renamed.secvid");

        try
        {
            await WaitUntilAsync(
                () => !viewModel.IsScanning &&
                      viewModel.VisibleItemCount == initialCount,
                TimeSpan.FromSeconds(20));

            var started = Stopwatch.GetTimestamp();
            File.Copy(fixture, added, overwrite: false);
            await WaitUntilAsync(
                () => ContainsPath(viewModel, added) &&
                      viewModel.VisibleItemCount == initialCount + 1,
                TimeSpan.FromSeconds(10));
            var addMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var modified = viewModel.CaptureVisibleItems()
                .First(item => !string.Equals(
                    item.FilePath,
                    added,
                    StringComparison.OrdinalIgnoreCase));
            var previousWriteTime = modified.LastWriteTimeUtc;
            started = Stopwatch.GetTimestamp();
            File.SetLastWriteTimeUtc(
                modified.FilePath,
                DateTime.UtcNow.AddMinutes(2));
            await WaitUntilAsync(
                () => viewModel.CaptureVisibleItems().Any(item =>
                    string.Equals(
                        item.FilePath,
                        modified.FilePath,
                        StringComparison.OrdinalIgnoreCase) &&
                    item.LastWriteTimeUtc > previousWriteTime),
                TimeSpan.FromSeconds(10));
            var modifyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            started = Stopwatch.GetTimestamp();
            File.Move(added, renamed);
            await WaitUntilAsync(
                () => !ContainsPath(viewModel, added) &&
                      ContainsPath(viewModel, renamed) &&
                      viewModel.VisibleItemCount == initialCount + 1,
                TimeSpan.FromSeconds(10));
            var renameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            started = Stopwatch.GetTimestamp();
            File.Delete(renamed);
            await WaitUntilAsync(
                () => !ContainsPath(viewModel, renamed) &&
                      viewModel.VisibleItemCount == initialCount,
                TimeSpan.FromSeconds(10));
            var deleteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var uniqueCount = viewModel.CaptureVisibleItems()
                .Select(item => item.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            viewModel.Dispose();
            await catalogTask.WaitAsync(TimeSpan.FromSeconds(5));
            return new IncrementalProjectionReport(
                addMs,
                modifyMs,
                renameMs,
                deleteMs,
                viewModel.VisibleItemCount,
                uniqueCount,
                catalogTask.IsCompletedSuccessfully,
                viewModel.VisibleItemCount == initialCount &&
                uniqueCount == initialCount &&
                catalogTask.IsCompletedSuccessfully);
        }
        finally
        {
            viewModel.Dispose();
            try
            {
                await catalogTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<WatcherReport> MeasureWatcherAsync(
        VideoLibraryScanner scanner,
        string root,
        string fixture,
        int stormEvents,
        int initialCount)
    {
        var session = new VideoLibraryCatalogSession(scanner);
        using var cancellation = new CancellationTokenSource();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initial = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var storm = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replaceAllCount = 0;
        var batchCount = 0;
        var consume = Task.Run(async () =>
        {
            await foreach (var batch in session.ObserveAsync(
                               root,
                               VideoLibraryScanOptions.TopDirectoryOnly,
                               cancellation.Token))
            {
                batchCount++;
                if (batch.ReplaceAll)
                {
                    replaceAllCount++;
                    paths.Clear();
                }
                foreach (var removed in batch.RemovedPaths)
                    paths.Remove(Path.GetFullPath(removed));
                foreach (var upsert in batch.Upserts)
                    paths.Add(Path.GetFullPath(upsert.FilePath));
                if (!batch.IsScanning && paths.Count == initialCount)
                    initial.TrySetResult();
                if (!batch.IsScanning && paths.Count == initialCount + stormEvents)
                    storm.TrySetResult();
            }
        }, cancellation.Token);

        await initial.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < stormEvents; index++)
        {
            File.Copy(
                fixture,
                Path.Combine(root, $"storm-{index:D4}.secvid"),
                overwrite: false);
        }
        await storm.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        cancellation.Cancel();
        try
        {
            await consume.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }

        var diskCount = Directory.EnumerateFiles(root, "*.secvid").Count();
        // 初始扫描每 50 项输出一批，事件风暴最多再产生少量合并批次；
        // 该门禁用于防止未来退化为“每个文件一个无界输出项”。
        var maximumExpectedBatches = (int)Math.Ceiling(initialCount / 50d) + 3;
        return new WatcherReport(
            stormEvents,
            elapsed,
            replaceAllCount,
            batchCount,
            maximumExpectedBatches,
            paths.Count,
            diskCount,
            paths.Count == diskCount &&
            diskCount == initialCount + stormEvents &&
            batchCount <= maximumExpectedBatches);
    }

    private static HardGateReport EvaluateHardGate(
        CryptoScenarioReport small,
        CryptoScenarioReport large,
        LibrarySuiteReport library)
    {
        var managedLimit = small.Resources.ManagedHeapPeakDeltaBytes + 64L * 1024 * 1024;
        var privateLimit = small.Resources.PrivateBytesPeakDeltaBytes + 128L * 1024 * 1024;
        var passed = small.Passed &&
                     large.Passed &&
                     library.Passed &&
                     large.Resources.ManagedHeapPeakDeltaBytes <= managedLimit &&
                     large.Resources.PrivateBytesPeakDeltaBytes <= privateLimit;
        return new HardGateReport(
            passed,
            managedLimit,
            privateLimit,
            large.Resources.ManagedHeapPeakDeltaBytes,
            large.Resources.PrivateBytesPeakDeltaBytes);
    }

    private static async Task GenerateSourceAsync(string path, long length)
    {
        var buffer = new byte[1024 * 1024];
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] = (byte)((index * 31L + index / 997 + 17) & 0xff);
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

    private static void CopyFixture(string fixture, string root, int count)
    {
        Directory.CreateDirectory(root);
        for (var index = 0; index < count; index++)
            File.Copy(fixture, Path.Combine(root, $"{index:D4}.secvid"));
    }

    private static void ReadAt(Stream stream, long position, byte[] buffer)
    {
        stream.Position = position;
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            throw new EndOfStreamException();
    }

    private static bool ContainsPath(
        LibraryBrowserCoordinatorViewModel viewModel,
        string path) =>
        viewModel.CaptureVisibleItems().Any(item => string.Equals(
            item.FilePath,
            path,
            StringComparison.OrdinalIgnoreCase));

    private static bool IsSorted(
        IReadOnlyList<VideoLibraryItemViewModel> items,
        VideoLibrarySortField field,
        VideoLibrarySortDirection direction)
    {
        for (var index = 1; index < items.Count; index++)
        {
            var comparison = CompareLibraryItems(
                items[index - 1],
                items[index],
                field,
                direction);
            if (comparison > 0)
                return false;
        }
        return true;
    }

    private static int CompareLibraryItems(
        VideoLibraryItemViewModel left,
        VideoLibraryItemViewModel right,
        VideoLibrarySortField field,
        VideoLibrarySortDirection direction)
    {
        if (field == VideoLibrarySortField.LastPlayedTime &&
            (left.LastPlayedUtc is null || right.LastPlayedUtc is null))
        {
            var nullComparison = left.LastPlayedUtc is null
                ? right.LastPlayedUtc is null ? 0 : 1
                : -1;
            return nullComparison != 0
                ? nullComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
        }

        var comparison = field switch
        {
            VideoLibrarySortField.PublicTitle => StringComparer.OrdinalIgnoreCase.Compare(
                string.IsNullOrWhiteSpace(left.PublicTitle)
                    ? left.FileNameWithoutExtension
                    : left.PublicTitle,
                string.IsNullOrWhiteSpace(right.PublicTitle)
                    ? right.FileNameWithoutExtension
                    : right.PublicTitle),
            VideoLibrarySortField.ModifiedTime =>
                left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc),
            VideoLibrarySortField.LastPlayedTime =>
                left.LastPlayedUtc!.Value.CompareTo(right.LastPlayedUtc!.Value),
            _ => StringComparer.OrdinalIgnoreCase.Compare(
                left.FileNameWithoutExtension,
                right.FileNameWithoutExtension)
        };
        if (comparison != 0 && direction == VideoLibrarySortDirection.Descending)
            comparison = -comparison;
        return comparison != 0
            ? comparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= timeout)
                throw new TimeoutException("G10 等待产品状态超时。");
            await Task.Delay(20);
        }
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("G10 子报告无效。");

    private static void WriteJson(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n",
            new UTF8Encoding(false));
    }

    private static bool Has(string[] args, string name) =>
        args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Required(string[] args, string name)
    {
        var index = Array.FindIndex(
            args,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException($"缺少参数 {name}。");
        return args[index + 1];
    }

    private static int RequiredInt(string[] args, string name) =>
        int.Parse(Required(args, name));

    private sealed class FixedCatalog(IReadOnlyList<VideoLibraryScanResult> results)
        : IVideoLibraryCatalogSession
    {
        public async IAsyncEnumerable<VideoLibraryCatalogBatch> ObserveAsync(
            string folderPath,
            VideoLibraryScanOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VideoLibraryCatalogBatch(
                results,
                Array.Empty<string>(),
                true,
                false,
                $"扫描完成，共 {results.Count} 个");
            await Task.CompletedTask;
        }
    }

    private sealed class ResourceSampler : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private Task? _sampling;
        private long _managedBaseline;
        private long _privateBaseline;
        private long _workingBaseline;
        private long _managedPeak;
        private long _privatePeak;
        private long _workingPeak;
        private int _gen2Baseline;
        private TimeSpan _pauseBaseline;

        public void Start()
        {
            var memory = GC.GetGCMemoryInfo();
            _managedBaseline = memory.HeapSizeBytes;
            _privateBaseline = _process.PrivateMemorySize64;
            _workingBaseline = _process.WorkingSet64;
            _managedPeak = _managedBaseline;
            _privatePeak = _privateBaseline;
            _workingPeak = _workingBaseline;
            _gen2Baseline = GC.CollectionCount(2);
            _pauseBaseline = GC.GetTotalPauseDuration();
            _sampling = Task.Run(SampleAsync);
        }

        public void Stop()
        {
            if (!_cancellation.IsCancellationRequested)
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

        public ResourceTrend Capture() => new(
            Math.Max(0, _managedPeak - _managedBaseline),
            Math.Max(0, _privatePeak - _privateBaseline),
            Math.Max(0, _workingPeak - _workingBaseline),
            GC.CollectionCount(2) - _gen2Baseline,
            (GC.GetTotalPauseDuration() - _pauseBaseline).TotalMilliseconds);

        private async Task SampleAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(50, _cancellation.Token);
            }
        }

        private void Sample()
        {
            Max(ref _managedPeak, GC.GetGCMemoryInfo().HeapSizeBytes);
            _process.Refresh();
            Max(ref _privatePeak, _process.PrivateMemorySize64);
            Max(ref _workingPeak, _process.WorkingSet64);
        }

        private static void Max(ref long location, long value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref location, value, current);
                if (previous == current)
                    return;
                current = previous;
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellation.Dispose();
            _process.Dispose();
        }
    }
}

internal sealed record G10Options(
    [property: JsonIgnore] string OutputPath,
    int SmallMiB,
    int LargeMiB,
    int SmallIterations,
    int LargeIterations,
    int SeekCount,
    int LibrarySmall,
    int LibraryLarge,
    int StormEvents)
{
    public static G10Options Parse(string[] args)
    {
        string Read(string name, string fallback)
        {
            var index = Array.FindIndex(
                args,
                value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        var result = new G10Options(
            Path.GetFullPath(Read("--output", "g10-performance.json")),
            int.Parse(Read("--small-mib", "64")),
            int.Parse(Read("--large-mib", "512")),
            int.Parse(Read("--small-iterations", "5")),
            int.Parse(Read("--large-iterations", "1")),
            int.Parse(Read("--seek-count", "256")),
            int.Parse(Read("--library-small", "100")),
            int.Parse(Read("--library-large", "1000")),
            int.Parse(Read("--storm-events", "256")));
        if (result.SmallMiB < 8 ||
            result.LargeMiB <= result.SmallMiB ||
            result.SmallIterations <= 0 ||
            result.LargeIterations <= 0 ||
            result.LibrarySmall <= 0 ||
            result.LibraryLarge <= result.LibrarySmall ||
            result.StormEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "G10 参数范围无效。");
        }
        return result;
    }
}

internal sealed record Metric(string Unit, double Median, double P95, double[] Samples)
{
    public static Metric From(IEnumerable<double> values, string unit)
    {
        var samples = values.Select(value => Math.Round(value, 4)).ToArray();
        if (samples.Length == 0)
            return new Metric(unit, 0, 0, []);
        var sorted = samples.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
        var p95Index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * 0.95) - 1,
            0,
            sorted.Length - 1);
        return new Metric(unit, median, sorted[p95Index], samples);
    }
}

internal sealed record CryptoScenarioReport(
    int SizeMiB,
    int Iterations,
    Metric EncryptElapsed,
    Metric EncryptThroughput,
    Metric DecryptElapsed,
    Metric DecryptThroughput,
    Metric RandomSeek,
    CacheScenarioReport Cache,
    ResourceTrend Resources,
    bool HashesMatch,
    int PartialFileCount,
    bool FilesUnlocked,
    bool Passed);

internal sealed record CacheScenarioReport(
    long Requests,
    long Hits,
    long Misses,
    long Evictions,
    int MaximumActiveChunks);

internal sealed record ResourceTrend(
    long ManagedHeapPeakDeltaBytes,
    long PrivateBytesPeakDeltaBytes,
    long WorkingSetPeakDeltaBytes,
    int Gen2CollectionDelta,
    double GcPauseDeltaMs);

internal sealed record ProjectionReport(
    int DebounceMs,
    double SearchElapsedMs,
    Metric SortElapsed,
    int FinalVisibleCount,
    IncrementalProjectionReport Incremental,
    bool Passed);

internal sealed record IncrementalProjectionReport(
    double AddElapsedMs,
    double ModifyElapsedMs,
    double RenameElapsedMs,
    double DeleteElapsedMs,
    int FinalVisibleCount,
    int UniquePathCount,
    bool SessionStopped,
    bool Passed);

internal sealed record WatcherReport(
    int EventCount,
    double SettleElapsedMs,
    int ReplaceAllCount,
    int BatchCount,
    int MaximumExpectedBatchCount,
    int ProjectedCount,
    int DiskCount,
    bool Passed);

internal sealed record LibrarySuiteReport(
    int SmallCount,
    int LargeCount,
    double SmallFirstScanMs,
    double SmallHotScanMs,
    double LargeFirstScanMs,
    double LargeHotScanMs,
    ProjectionReport Projection,
    WatcherReport Watcher,
    ResourceTrend Resources,
    bool Passed);

internal sealed record HardGateReport(
    bool Passed,
    long ManagedHeapLimitBytes,
    long PrivateBytesLimitBytes,
    long LargeManagedHeapPeakDeltaBytes,
    long LargePrivateBytesPeakDeltaBytes);

internal sealed record G10BenchmarkReport(
    int SchemaVersion,
    string Kind,
    DateTimeOffset TimestampUtc,
    EnvironmentReport Environment,
    string ComparableFingerprint,
    G10Options Parameters,
    CryptoScenarioReport SmallCrypto,
    CryptoScenarioReport LargeCrypto,
    LibrarySuiteReport Library,
    HardGateReport HardGate,
    bool Success);

internal sealed record EnvironmentReport(
    string Platform,
    string Architecture,
    string OperatingSystem,
    string CpuModel,
    int LogicalProcessorCount,
    long AvailableMemoryBytes,
    string Runtime,
    bool IsServerGc,
    string GcLatencyMode,
    string BuildConfiguration,
    string VideoSecurityPlayerVersion,
    string AvaloniaVersion,
    string DockVersion,
    string LibVlcSharpVersion,
    string LibVlcVersion)
{
    public static EnvironmentReport Capture()
    {
        var plugin = typeof(SeekableEncryptedVideoStream).Assembly;
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        var runtimeRoot = Path.Combine(
            Path.GetDirectoryName(plugin.Location) ?? string.Empty,
            "native",
            "win-x64",
            "libvlc",
            "libvlc.dll");
        return new EnvironmentReport(
            System.OperatingSystem.IsWindows() ? "windows-x64" : "unsupported",
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.FrameworkDescription,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            configuration,
            plugin.GetName().Version?.ToString() ?? "unknown",
            typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            "not-applicable-host-v2",
            typeof(LibVLCSharp.Shared.Core).Assembly.GetName().Version?.ToString() ?? "unknown",
            File.Exists(runtimeRoot)
                ? FileVersionInfo.GetVersionInfo(runtimeRoot).FileVersion ?? "unknown"
                : "unavailable");
    }

    public string CreateComparableFingerprint()
    {
        var input = string.Join(
            "\n",
            Platform,
            Architecture,
            CpuModel,
            LogicalProcessorCount,
            Runtime,
            BuildConfiguration,
            VideoSecurityPlayerVersion,
            AvaloniaVersion,
            DockVersion,
            LibVlcSharpVersion,
            LibVlcVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }
}

internal sealed record ScanResult(
    double ElapsedMs,
    IReadOnlyList<VideoLibraryScanResult> Results);

/// <summary>基准进程没有 Host 文档，使用永不关闭的显式生命周期作为测量边界。</summary>
internal sealed class NonClosingDocumentLifetime : IDocumentLifetime
{
    public static NonClosingDocumentLifetime Instance { get; } = new();
    public CancellationToken ClosingToken => CancellationToken.None;
    public bool IsClosing => false;
}
