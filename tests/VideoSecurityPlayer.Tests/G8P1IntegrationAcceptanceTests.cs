using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G8 P1 的确定性规模、事务和脱敏门禁。
/// </summary>
/// <remarks>
/// 文件闭环使用真实 SECVID03；取消和失败时序继续由 G2/G5 的可控替身覆盖。这样既能证明
/// 百文件真实正确性，也不把依赖机器速度的竞态写入普通测试运行器。
/// </remarks>
[Collection(Secvid03Collection.Name)]
public sealed class G8P1IntegrationAcceptanceTests(Secvid03Fixture fixture)
{
    [Fact]
    public async Task 百文件加密解密闭环保持顺序哈希和无半成品()
    {
        var root = CreateDirectory("g8-roundtrip");
        var encryptedDirectory = Directory.CreateDirectory(
            Path.Combine(root, "encrypted")).FullName;
        var decryptedDirectory = Directory.CreateDirectory(
            Path.Combine(root, "decrypted")).FullName;
        try
        {
            var encryption = new VideoEncryptorService(new Secvid03Encryptor());
            var batchEncryption = new VideoBatchEncryptionService(
                encryption,
                new OutputPathConflictResolver());
            using var encryptionRunner =
                new SequentialVideoQueueRunner<PreparedEncryptionItem>();
            var requests = Enumerable.Range(0, 100)
                .Select(index => new BatchEncryptionItemRequest(
                    Guid.NewGuid(),
                    fixture.OriginalPath,
                    Path.Combine(encryptedDirectory, $"{index:D3}.secvid"),
                    $"G8 {index:D3}",
                    string.Empty))
                .ToArray();
            var encryptionPlan = await batchEncryption.PrepareAsync(
                requests,
                OutputConflictPolicy.Block,
                skippedSucceededCount: 0);

            var maximumActive = 0;
            var active = 0;
            var encryptionResult = await encryptionRunner.RunAsync(
                Guid.NewGuid(),
                encryptionPlan.Items,
                _ => true,
                async (item, progress, cancellationToken) =>
                {
                    var current = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumActive, current);
                    try
                    {
                        await encryption.EncryptAsync(
                            item.Request,
                            Secvid03Fixture.Password,
                            progress,
                            cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                });

            Assert.Equal(100, encryptionResult.SucceededCount);
            Assert.Equal(1, maximumActive);

            var decryption = new VideoDecryptionService(
                new Secvid03Decryptor(),
                new DecryptionOutputPathResolver(),
                new StoragePreflightProbe());
            var encryptedPaths = requests
                .Select(request => request.RequestedOutputPath)
                .ToArray();
            var candidates = await decryption.InspectAsync(encryptedPaths);
            var decryptionResult = await decryption.DecryptBatchAsync(
                candidates,
                decryptedDirectory,
                Secvid03Fixture.Password);

            Assert.Equal(100, decryptionResult.SucceededCount);
            Assert.Equal(0, decryptionResult.FailedCount);
            Assert.Equal(100, decryptionResult.OutputPaths.Count);

            var expectedHash = Convert.ToHexString(
                SHA256.HashData(fixture.OriginalBytes));
            foreach (var path in decryptionResult.OutputPaths)
            {
                await using var output = File.OpenRead(path);
                Assert.Equal(
                    expectedHash,
                    Convert.ToHexString(
                        await SHA256.HashDataAsync(output)));
            }
            Assert.Empty(Directory.GetFiles(root, "*.partial-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 预检后的竞争文件仍被事务拒绝且安全改名不覆盖哨兵()
    {
        var root = CreateDirectory("g8-conflict");
        try
        {
            var finalPath = Path.Combine(root, "competing.secvid");
            var request = new VideoEncryptionRequest(
                fixture.OriginalPath,
                finalPath,
                string.Empty,
                string.Empty);
            var encryption = new VideoEncryptorService(new Secvid03Encryptor());
            Assert.True((await encryption.PreflightAsync(request)).CanProceed);

            await File.WriteAllTextAsync(finalPath, "G8-SENTINEL");
            var encryptionError = await Assert.ThrowsAsync<VideoTaskException>(() =>
                encryption.EncryptAsync(request, Secvid03Fixture.Password));
            Assert.Equal(VideoTaskFailureCode.OutputConflict, encryptionError.FailureCode);
            Assert.Equal("G8-SENTINEL", await File.ReadAllTextAsync(finalPath));

            var decryption = new VideoDecryptionService(
                new Secvid03Decryptor(),
                new DecryptionOutputPathResolver(),
                new StoragePreflightProbe());
            var candidate = Assert.Single(await decryption.InspectAsync([fixture.EncryptedPath]));
            var strictTarget = Path.Combine(root, candidate.OriginalFileName);
            await File.WriteAllTextAsync(strictTarget, "G8-STRICT-SENTINEL");
            var strict = await decryption.PreflightAsync(
                [new DecryptionQueueRequest(Guid.NewGuid(), candidate)],
                root,
                OutputConflictPolicy.Block);
            Assert.False(Assert.Single(strict.Items).CanRun);

            var renamed = await decryption.PreflightAsync(
                [new DecryptionQueueRequest(Guid.NewGuid(), candidate)],
                root,
                OutputConflictPolicy.GenerateUniqueName);
            var prepared = Assert.Single(renamed.Items);
            Assert.True(prepared.CanRun);
            Assert.NotEqual(strictTarget, prepared.OutputPath);

            await File.WriteAllTextAsync(prepared.OutputPath, "G8-RENAME-SENTINEL");
            var decryptionError = await Assert.ThrowsAsync<VideoTaskException>(() =>
                decryption.DecryptAsync(prepared, Secvid03Fixture.Password));
            Assert.Equal(VideoTaskFailureCode.OutputConflict, decryptionError.FailureCode);
            Assert.Equal(
                "G8-RENAME-SENTINEL",
                await File.ReadAllTextAsync(prepared.OutputPath));
            Assert.Empty(Directory.GetFiles(root, "*.partial-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 千文件真实递归扫描没有重复且取消会停止枚举()
    {
        var root = CreateDirectory("g8-library");
        try
        {
            for (var index = 0; index < 1000; index++)
            {
                var level = (index % 3) switch
                {
                    0 => "a",
                    1 => "a/b",
                    _ => "a/b/c"
                };
                var directory = Directory.CreateDirectory(Path.Combine(root, level)).FullName;
                File.Copy(
                    fixture.EncryptedPath,
                    Path.Combine(directory, $"{index:D4}.secvid"));
            }

            var scanner = new VideoLibraryScanner();
            var items = new List<VideoLibraryScanResult>();
            await foreach (var item in scanner.ScanAsync(
                               root,
                               new VideoLibraryScanOptions(true),
                               CancellationToken.None))
            {
                items.Add(item);
            }

            Assert.Equal(1000, items.Count);
            Assert.Equal(
                1000,
                items.Select(item => item.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            Assert.All(items, item => Assert.Equal(VideoLibraryMetadataState.Ready, item.State));

            using var cancellation = new CancellationTokenSource();
            var observed = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in scanner.ScanAsync(
                                   root,
                                   new VideoLibraryScanOptions(true),
                                   cancellation.Token))
                {
                    if (++observed == 10)
                        cancellation.Cancel();
                }
            });
            Assert.InRange(observed, 10, 20);

            var stormDirectory =
                Directory.CreateDirectory(Path.Combine(root, "storm")).FullName;
            // 覆盖率插桩与全套件并行负载会显著放大 1256 个真实文件的读取成本；60 秒只是
            // 失败上限，不改变数量、去重、收敛或覆盖率门槛。原 30 秒在单测独跑时足够，
            // 但会把机器负载误报成目录协议失败，尤其现在还要验证完整静默窗口重试。
            using var catalogCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var coordinatedScanner = new RescanCoordinatingScanner(scanner);
            await using var updates = new VideoLibraryCatalogSession(coordinatedScanner)
                .ObserveAsync(
                    root,
                    new VideoLibraryScanOptions(true),
                    catalogCancellation.Token)
                .GetAsyncEnumerator(catalogCancellation.Token);

            // 初扫完成后再集中制造超过阈值的不同路径。目录会话必须放弃逐项猜测，
            // 以 ReplaceAll 完整快照恢复文件系统事实，避免 watcher 丢事件后留下幽灵项。
            while (await updates.MoveNextAsync())
            {
                if (!updates.Current.IsScanning)
                    break;
            }

            // 使用 G10 正式规模的 256 个唯一路径，为 FileSystemWatcher 在高负载下的
            // 合并/丢弃保留余量；即便少量原始通知没有到达，也必须跨过 128 路径阈值。
            for (var index = 0; index < 256; index++)
            {
                File.Copy(
                    fixture.EncryptedPath,
                    Path.Combine(stormDirectory, $"{index:D3}.secvid"));
            }

            // 只制造一瞬间的文件风暴时，操作系统可能把全部通知都赶在 500ms 静默窗口
            // 之前交付，测试便无法稳定覆盖“静默等待期间又收到通知”的关键分支。这里用同一
            // 个临时候选文件持续制造短暂变化：它不改变最终 1256 项事实，也不靠扩大唯一
            // 路径数跨门槛；连续两秒则保证会话至少经历一次“发现新通知 -> 重新等待完整
            // 静默窗口”。这样验证的是收敛协议本身，而不是某次 FileSystemWatcher 调度运气。
            var lateChangePath = Path.Combine(stormDirectory, "quiet-window.secvid");
            var rescanActivity = Task.Run(async () =>
            {
                await Task.Delay(300, catalogCancellation.Token);
                for (var index = 0; index < 20; index++)
                {
                    File.Copy(fixture.EncryptedPath, lateChangePath, overwrite: true);
                    File.Delete(lateChangePath);
                    await Task.Delay(100, catalogCancellation.Token);
                }

                // 第二次 ScanAsync 就是风暴触发的完整扫描。测试扫描器在真正枚举前暂停，
                // 此时再写入同一临时文件，能确定性证明“扫描期间又有通知”的中间快照会
                // 被丢弃。生产代码仍使用真实扫描器；这个窄装饰器只提供测试同步点，不替换
                // 文件解析、Watcher 或目录事实，也不把计时器/服务定位器引入生产边界。
                await coordinatedScanner.RescanStarted.WaitAsync(catalogCancellation.Token);
                for (var index = 0; index < 10; index++)
                {
                    File.Copy(fixture.EncryptedPath, lateChangePath, overwrite: true);
                    File.Delete(lateChangePath);
                    await Task.Delay(50, catalogCancellation.Token);
                }
                coordinatedScanner.AllowRescan();
            }, catalogCancellation.Token);

            VideoLibraryCatalogBatch? stormSnapshot = null;
            while (await updates.MoveNextAsync())
            {
                if (!updates.Current.ReplaceAll)
                    continue;

                // 生产会话会丢弃扫描期间仍有变化的中间快照；这里仍防御性地只接受
                // 精确最终事实，确保未来实现调整也不能把半成品 ReplaceAll 当成成功。
                // 上面的 60 秒令牌仅是负载保护，1256 与去重门槛都没有放宽。
                if (updates.Current.Upserts.Count != 1256)
                    continue;

                stormSnapshot = updates.Current;
                break;
            }

            Assert.NotNull(stormSnapshot);
            Assert.Equal(1256, stormSnapshot.Upserts.Count);
            Assert.Equal(
                1256,
                stormSnapshot.Upserts
                    .Select(item => item.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            await rescanActivity;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 持久化模型诊断快照和用户数据均不包含敏感Canary()
    {
        const string password = "G8-PASSWORD-CANARY";
        const string derivedKey = "G8-DERIVED-KEY-CANARY";
        const string plaintext = "G8-PLAINTEXT-CANARY";
        const string description = "G8-PUBLIC-DESCRIPTION-CANARY";
        var root = CreateDirectory("g8-sensitive");
        var userDataPath = Path.Combine(root, "user-data-v1.json");
        try
        {
            using (var store = new SecretVideoUserDataStore(userDataPath))
            {
                store.UpdatePreferences(new PlaybackPreferences(61, 1.25f));
                store.UpdateSettings(VideoLibrarySettings.Default with
                {
                    RecentFolder = root,
                    IncludeSubdirectories = true
                });
                store.Upsert(new VideoPlaybackHistoryEntry(
                    Path.Combine(root, "item.secvid"),
                    "00112233445566778899AABBCCDDEEFF",
                    100,
                    25,
                    100,
                    DateTimeOffset.UnixEpoch,
                    false));
            }

            var persisted = File.ReadAllText(userDataPath);
            var diagnostics = JsonSerializer.Serialize(
                SecurePlaybackDiagnostics.CaptureResources());
            var combined = persisted + diagnostics;
            foreach (var canary in new[] { password, derivedKey, plaintext, description })
                Assert.DoesNotContain(canary, combined, StringComparison.Ordinal);

            var persistentAndDiagnosticTypes = new[]
            {
                typeof(VideoPlaybackHistoryEntry),
                typeof(VideoLibrarySettings),
                typeof(PlaybackPreferences),
                typeof(VideoQueueProgress),
                typeof(VideoQueueRunResult),
                typeof(BatchDecryptionResult),
                typeof(PlaybackResourceSnapshot)
            };
            foreach (var type in persistentAndDiagnosticTypes)
            {
                Assert.DoesNotContain(
                    type.GetProperties(),
                    property => IsSensitiveMember(property.Name));
                Assert.DoesNotContain(
                    type.GetFields(),
                    field => IsSensitiveMember(field.Name));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string CreateDirectory(string prefix)
    {
        var path = Path.Combine(
            fixture.DirectoryPath,
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsSensitiveMember(string name) =>
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DerivedKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("CachedPlaintextChunks", StringComparison.Ordinal) ||
        name.Contains("AuthenticationContext", StringComparison.OrdinalIgnoreCase);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 仅为目录风暴测试暴露“第二次完整扫描已经开始”的同步点。
    /// </summary>
    /// <remarks>
    /// 装饰器把所有扫描和单文件读取原样委托给真实扫描器；它只暂停第二次 ScanAsync，
    /// 让测试能够在扫描窗口内可靠制造文件通知。相比固定睡眠，这个窄 Adapter 能证明
    /// 被测协议的先后关系，也避免因机器快慢造成覆盖率与门禁摘要漂移。
    /// </remarks>
    private sealed class RescanCoordinatingScanner(IVideoLibraryScanner inner)
        : IVideoLibraryScanner
    {
        private readonly TaskCompletionSource _rescanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowRescan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scanCount;

        public Task RescanStarted => _rescanStarted.Task;

        public void AllowRescan() => _allowRescan.TrySetResult();

        public IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            CancellationToken cancellationToken) =>
            ScanAsync(folderPath, VideoLibraryScanOptions.TopDirectoryOnly, cancellationToken);

        public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            VideoLibraryScanOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _scanCount) == 2)
            {
                _rescanStarted.TrySetResult();
                await _allowRescan.Task.WaitAsync(cancellationToken);
            }

            await foreach (var item in inner
                               .ScanAsync(folderPath, options, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }

        public Task<VideoLibraryScanResult?> ReadFileAsync(
            string filePath,
            CancellationToken cancellationToken) =>
            inner.ReadFileAsync(filePath, cancellationToken);
    }
}
