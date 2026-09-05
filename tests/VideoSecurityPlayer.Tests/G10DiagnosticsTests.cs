using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G10 脱敏诊断和四块 LRU 统计门禁。
/// </summary>
public sealed class G10DiagnosticsTests
{
    [Fact]
    public async Task DiagnosticJson_UsesAllowListAndRemovesSensitiveCanaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "g10-plugin", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "native", "win-x64", "libvlc");
        var state = new FixedDiagnosticState(
            new PlaybackDiagnosticState(
                PlaybackSnapshot.Empty with
                {
                    State = PlaybackState.Faulted,
                    Activity = PlaybackActivity.Idle
                },
                new Secvid03DiagnosticSummary(
                    "SECVID03",
                    3,
                    32,
                    12_345_678,
                    1024 * 1024,
                    12,
                    16,
                    "PBKDF2-SHA256",
                    600_000)));
        var platform = new FixedPlatformStatus(root, runtime, ready: true);
        var exporter = new PlaybackDiagnosticExporter(
            state,
            platform,
            new FixedLayoutProvider(root, runtime));
        var canary = "G10-PASSWORD-用户目录-SECRET";
        var derivedKeyCanary =
            "A1B2C3D4E5F60718293A4B5C6D7E8F90112233445566778899AABBCCDDEEFF00";
        var plaintextCanary = "G10-PLAINTEXT-VIDEO-ASCII-CANARY";
        var failure = new PlaybackFailure(
            PlaybackFailureCode.AuthenticationFailed,
            $"密码 {canary}，路径 C:\\Users\\secret\\movie.secvid，密钥 {derivedKeyCanary}",
            $"公开标题和公开描述 {canary}，明文 {plaintextCanary}",
            "AUTH_FAILED");

        var json = Encoding.UTF8.GetString(
            (await exporter.CreateJsonAsync(failure)).Span);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "videosecurityplayer-playback-diagnostics",
            document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            "authentication",
            document.RootElement.GetProperty("playback")
                .GetProperty("failureDomain")
                .GetString());
        Assert.Equal(
            "$PLUGIN/native/win-x64/libvlc",
            document.RootElement.GetProperty("deployment")
                .GetProperty("runtimeLocation")
                .GetString());
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(derivedKeyCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintextCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("movie.secvid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileId", json, StringComparison.OrdinalIgnoreCase);
        foreach (var forbiddenKey in new[]
                 {
                     "\"password\":",
                     "\"derivedKey\":",
                     "\"authenticationContext\":",
                     "\"publicDescription\":",
                     "\"filePath\":"
                 })
        {
            Assert.DoesNotContain(forbiddenKey, json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(Encoding.UTF8.GetByteCount(json) <= PlaybackDiagnosticExporter.MaximumJsonBytes);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticJson_HidesRuntimeOutsidePluginAndRejectsUnsafeDiagnosticCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "g10-plugin-root");
        var outside = Path.Combine(Path.GetTempPath(), "private-user", "runtime");
        var exporter = new PlaybackDiagnosticExporter(
            new FixedDiagnosticState(
                new PlaybackDiagnosticState(PlaybackSnapshot.Empty, null)),
            new FixedPlatformStatus(root, outside, ready: false),
            new FixedLayoutProvider(root, outside));

        var json = Encoding.UTF8.GetString(
            (await exporter.CreateJsonAsync(
                new PlaybackFailure(
                    PlaybackFailureCode.DecodeFailed,
                    "ignored",
                    DiagnosticCode: "BAD/CODE/Users/secret"))).Span);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "outside-plugin",
            document.RootElement.GetProperty("deployment")
                .GetProperty("runtimeLocation")
                .GetString());
        Assert.Equal(
            "RUNTIME_OUTSIDE_PLUGIN",
            document.RootElement.GetProperty("deployment")
                .GetProperty("issues")[0]
                .GetProperty("code")
                .GetString());
        Assert.Equal(
            "unavailable",
            document.RootElement.GetProperty("versions")
                .GetProperty("libVlc")
                .GetString());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("playback")
                .GetProperty("diagnosticCode")
                .ValueKind);
        Assert.DoesNotContain("private-user", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticJson_PrioritizesUnsupportedPlatformOverDeploymentFailure()
    {
        var root = Path.GetFullPath("g10-unsupported-platform");
        var exporter = new PlaybackDiagnosticExporter(
            new FixedDiagnosticState(
                new PlaybackDiagnosticState(PlaybackSnapshot.Empty, null)),
            new FixedPlatformStatus(
                root,
                Path.Combine(root, "native"),
                ready: false,
                supported: false),
            new FixedLayoutProvider(root, Path.Combine(root, "native")));

        var json = Encoding.UTF8.GetString(
            (await exporter.CreateJsonAsync(null)).Span);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "platform",
            document.RootElement.GetProperty("playback")
                .GetProperty("failureDomain")
                .GetString());
        Assert.False(
            document.RootElement.GetProperty("platform")
                .GetProperty("isSupported")
                .GetBoolean());
    }

    [Theory]
    [InlineData(PlaybackFailureCode.InvalidFormat, "format")]
    [InlineData(PlaybackFailureCode.CorruptedContent, "format")]
    [InlineData(PlaybackFailureCode.AuthenticationFailed, "authentication")]
    [InlineData(PlaybackFailureCode.InputUnavailable, "io")]
    [InlineData(PlaybackFailureCode.ParseFailed, "decode")]
    [InlineData(PlaybackFailureCode.DecodeFailed, "decode")]
    [InlineData(PlaybackFailureCode.SurfaceRestoreFailed, "decode")]
    [InlineData(PlaybackFailureCode.InvalidRequest, "operation")]
    public async Task DiagnosticJson_MapsStableFailureDomain(
        PlaybackFailureCode code,
        string expectedDomain)
    {
        var root = Path.GetFullPath("g10-diagnostic-test");
        var runtime = Path.Combine(root, "native");
        var exporter = new PlaybackDiagnosticExporter(
            new FixedDiagnosticState(
                new PlaybackDiagnosticState(PlaybackSnapshot.Empty, null)),
            new FixedPlatformStatus(root, runtime, ready: true),
            new FixedLayoutProvider(root, runtime));

        var json = Encoding.UTF8.GetString(
            (await exporter.CreateJsonAsync(new PlaybackFailure(code, "ignored"))).Span);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            expectedDomain,
            document.RootElement.GetProperty("playback")
                .GetProperty("failureDomain")
                .GetString());
    }

    [Fact]
    public async Task FourChunkLru_ReportsHitsMissesAndEvictions()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "VideoSecurityPlayer-G10-Cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            var encrypted = Path.Combine(root, "sample.secvid");
            await File.WriteAllBytesAsync(source, new byte[6 * 1024 * 1024]);
            await new Secvid03Encryptor().EncryptAsync(
                source,
                encrypted,
                "G10 cache test",
                string.Empty,
                string.Empty);

            SecurePlaybackDiagnostics.ResetCacheStatistics();
            using (var stream = SeekableEncryptedVideoStream.Open(
                       encrypted,
                       "G10 cache test"))
            {
                var buffer = new byte[1];
                var body = stream.DiagnosticSummary.OriginalHeaderLength;
                ReadAt(stream, body, buffer);
                ReadAt(stream, body, buffer);
                for (var chunk = 1; chunk <= 4; chunk++)
                    ReadAt(stream, body + chunk * 1024L * 1024, buffer);
                ReadAt(stream, body, buffer);

                var resources = SecurePlaybackDiagnostics.CaptureResources();
                Assert.Equal(4, resources.CachedPlaintextChunks);
            }

            var statistics = SecurePlaybackDiagnostics.CaptureCacheStatistics();
            Assert.Equal(7, statistics.Requests);
            Assert.Equal(1, statistics.Hits);
            Assert.Equal(6, statistics.Misses);
            Assert.Equal(2, statistics.Evictions);
            Assert.Equal(0, SecurePlaybackDiagnostics.CaptureResources().CachedPlaintextChunks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PerformanceComparator_AppliesLatencyAndThroughputThresholds()
    {
        var latencyBaseline = new ComparableMetric(
            "seek",
            MetricDirection.LowerIsBetter,
            "ms",
            10,
            20);
        var latencyCandidate = latencyBaseline with { Median = 12, P95 = 29 };
        var throughputBaseline = new ComparableMetric(
            "encrypt",
            MetricDirection.HigherIsBetter,
            "MiB/s",
            100,
            100);

        Assert.True(
            G10BaselineComparer.CompareMetric(latencyBaseline, latencyCandidate).Passed);
        Assert.False(
            G10BaselineComparer.CompareMetric(
                latencyBaseline,
                latencyCandidate with { P95 = 31 }).Passed);
        Assert.True(
            G10BaselineComparer.CompareMetric(
                throughputBaseline,
                throughputBaseline with { Median = 75 }).Passed);
        Assert.False(
            G10BaselineComparer.CompareMetric(
                throughputBaseline,
                throughputBaseline with { Median = 74.9 }).Passed);

        var lowLatency = new ComparableMetric(
            "cached-read",
            MetricDirection.LowerIsBetter,
            "ms",
            0.5,
            0.5);
        Assert.True(
            G10BaselineComparer.CompareMetric(
                lowLatency,
                lowLatency with { Median = 2.5, P95 = 5.5 }).Passed);
        Assert.False(
            G10BaselineComparer.CompareMetric(
                lowLatency,
                lowLatency with { Median = 2.51, P95 = 5.5 }).Passed);
    }

    [Fact]
    public void PerformanceComparator_SeparatesFingerprintAndHardGateFailures()
    {
        var environment = EnvironmentReport.Capture();
        var metric = new ComparableMetric(
            "seek",
            MetricDirection.LowerIsBetter,
            "ms",
            10,
            20);
        var baseline = new G10AggregateReport(
            1,
            "g10-performance-baseline",
            DateTimeOffset.UtcNow,
            environment,
            "fingerprint-a",
            "scenario-a",
            true,
            [metric]);

        var notComparable = G10BaselineComparer.Compare(
            baseline,
            baseline with { ComparableFingerprint = "fingerprint-b" });
        var hardFailure = G10BaselineComparer.Compare(
            baseline,
            baseline with { HardGatePassed = false });
        var scenarioMismatch = G10BaselineComparer.Compare(
            baseline,
            baseline with { ScenarioSignature = "scenario-b" });

        Assert.False(notComparable.Comparable);
        Assert.Equal("environment-fingerprint-mismatch", notComparable.Reason);
        Assert.True(hardFailure.Comparable);
        Assert.False(hardFailure.Passed);
        Assert.Equal("candidate-hard-gate-failed", hardFailure.Reason);
        Assert.False(scenarioMismatch.Comparable);
        Assert.Equal("scenario-parameters-mismatch", scenarioMismatch.Reason);
    }

    [Fact]
    public async Task DiagnosticExport_AllowsOnlyOneTaskPerDocument()
    {
        var root = Path.GetFullPath("g10-diagnostic-concurrency");
        using var session = new EmptyPlaybackSession();
        using var viewModel = new PlaybackCoordinatorViewModel(
            session,
            session,
            new FixedPlatformStatus(root, Path.Combine(root, "native"), ready: true),
            new NoopBackendInitializer());
        var exporter = new BlockingExporter();
        viewModel.ConfigureDiagnosticExporter(exporter);

        var first = viewModel.CreateDiagnosticJsonAsync();
        await exporter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.CreateDiagnosticJsonAsync());
        Assert.True(viewModel.IsExportingDiagnostics);

        exporter.Complete.TrySetResult();
        Assert.Equal("{}", Encoding.UTF8.GetString((await first).Span));
        Assert.False(viewModel.IsExportingDiagnostics);
        Assert.True(viewModel.CanExportDiagnostics);
    }

    private static void ReadAt(Stream stream, long position, byte[] buffer)
    {
        stream.Position = position;
        Assert.Equal(1, stream.Read(buffer, 0, 1));
    }

    private sealed class FixedDiagnosticState(PlaybackDiagnosticState state)
        : IPlaybackDiagnosticState
    {
        public PlaybackDiagnosticState CaptureDiagnosticState() => state;
    }

    private sealed class BlockingExporter : IPlaybackDiagnosticExporter
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReadOnlyMemory<byte>> CreateJsonAsync(
            PlaybackFailure? lastFailure,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return Encoding.UTF8.GetBytes("{}");
        }
    }

    private sealed class NoopBackendInitializer : IPlaybackBackendInitializer
    {
        public void Initialize()
        {
        }
    }

    private sealed class EmptyPlaybackSession :
        ISecureVideoPlaybackSession,
        IPlaybackSurfaceSession,
        IPlaybackVideoOutput
    {
        public event EventHandler<PlaybackChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler? OutputChanged
        {
            add { }
            remove { }
        }

        public PlaybackSnapshot Snapshot => PlaybackSnapshot.Empty;
        public IPlaybackVideoOutput VideoOutput => this;
        public long Generation => 0;

        public Task<PlaybackOperationResult> LoadAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> LoadAndPlayAsync(
            string filePath,
            string password,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> PlayAsync(
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> PauseAsync(
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> SeekAsync(
            long positionMs,
            bool waitForFrame = false,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> SeekRelativeAsync(
            long deltaMs,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> SetRateAsync(
            float rate,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> SelectAudioTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> SelectSubtitleTrackAsync(
            int trackId,
            CancellationToken cancellationToken = default) =>
            Success();

        public Task<PlaybackOperationResult> ReleaseAsync(
            CancellationToken cancellationToken = default) =>
            Success();

        public bool SetVolume(int volume) => true;

        public void DetachSurface(VideoSurfaceIdentity surface)
        {
        }

        public Task<PlaybackOperationResult> AttachAndRestoreSurfaceAsync(
            VideoSurfaceIdentity surface,
            CancellationToken cancellationToken = default) =>
            Success();

        public void Dispose()
        {
        }

        private static Task<PlaybackOperationResult> Success() =>
            Task.FromResult(PlaybackOperationResult.Succeeded());
    }

    private sealed class FixedLayoutProvider(string plugin, string runtime)
        : IPlaybackRuntimeLayoutProvider
    {
        public PlaybackRuntimeLayout Resolve() => new(plugin, runtime);
    }

    private sealed class FixedPlatformStatus(
        string plugin,
        string runtime,
        bool ready,
        bool supported = true) : IPlaybackPlatformStatus
    {
        public PlaybackPlatformCapabilities Capabilities { get; } = new(
            supported ? "windows-x64" : "unsupported",
            supported,
            supported,
            supported,
            supported,
            supported,
            supported,
            supported ? null : "unsupported");

        public DeploymentCheckResult Check() => new(
            plugin,
            runtime,
            ready
                ? []
                :
                [
                    new DeploymentIssue(
                        DeploymentIssueCode.NativeLibraryMissing,
                        "ignored",
                        runtime,
                        "ignored")
                ]);
    }
}
