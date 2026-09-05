using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[Collection(Secvid03Collection.Name)]
public sealed class VideoDecryptionTests(Secvid03Fixture fixture)
{
    [Fact]
    public async Task Decryptor_RoundTripsOriginalBytesAndReportsCompletion()
    {
        var outputPath = Path.Combine(fixture.DirectoryPath, "roundtrip.mp4");
        var reported = new List<VideoTaskProgress>();

        await new Secvid03Decryptor().DecryptAsync(
            fixture.EncryptedPath,
            outputPath,
            Secvid03Fixture.Password,
            new InlineProgress<VideoTaskProgress>(reported.Add));

        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(outputPath));
        Assert.NotEmpty(reported);
        Assert.Equal(100, reported[^1].Percentage);
        Assert.Equal("解密完成", reported[^1].Message);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.partial-*"));
    }

    [Fact]
    public async Task Decryptor_WrongPasswordTamperingAndCancellationLeaveNoOutput()
    {
        var wrongPasswordOutput = Path.Combine(fixture.DirectoryPath, "wrong-password.mp4");
        var wrongPassword = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Decryptor().DecryptAsync(
                fixture.EncryptedPath,
                wrongPasswordOutput,
                "wrong-password"));
        Assert.Equal(VideoTaskFailureCode.AuthenticationFailed, wrongPassword.FailureCode);
        Assert.False(File.Exists(wrongPasswordOutput));

        var tamperedPath = fixture.CopyEncrypted("decrypt-tampered.secvid");
        var cipherOffset = Secvid03Fixture.FixedHeaderSize +
                           Secvid03Fixture.PublicRegionSize +
                           Secvid03Fixture.OriginalPrefixSize;
        FlipByte(tamperedPath, cipherOffset + 19);
        var tamperedOutput = Path.Combine(fixture.DirectoryPath, "tampered.mp4");
        var tampered = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Decryptor().DecryptAsync(
                tamperedPath,
                tamperedOutput,
                Secvid03Fixture.Password));
        Assert.Equal(VideoTaskFailureCode.CorruptedContent, tampered.FailureCode);
        Assert.False(File.Exists(tamperedOutput));

        var cancelledOutput = Path.Combine(fixture.DirectoryPath, "cancelled.mp4");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Secvid03Decryptor().DecryptAsync(
                fixture.EncryptedPath,
                cancelledOutput,
                Secvid03Fixture.Password,
                new InlineProgress<VideoTaskProgress>(_ => cancellation.Cancel()),
                cancellation.Token));
        Assert.False(File.Exists(cancelledOutput));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.partial-*"));
    }

    [Fact]
    public async Task Decryptor_NeverOverwritesExistingOutput()
    {
        var outputPath = Path.Combine(fixture.DirectoryPath, "existing.mp4");
        await File.WriteAllTextAsync(outputPath, "keep-me");

        var error = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Decryptor().DecryptAsync(
                fixture.EncryptedPath,
                outputPath,
                Secvid03Fixture.Password));

        Assert.Equal(VideoTaskFailureCode.OutputConflict, error.FailureCode);
        Assert.Equal("keep-me", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task Inspection_DeduplicatesAndIsolatesInvalidFiles()
    {
        var invalidPath = Path.Combine(fixture.DirectoryPath, "broken.secvid");
        await File.WriteAllTextAsync(invalidPath, "not-secvid");
        var service = CreateService(new RecordingDecryptor());

        var candidates = await service.InspectAsync(
            [fixture.EncryptedPath, fixture.EncryptedPath.ToUpperInvariant(), invalidPath]);

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].IsValid);
        Assert.False(candidates[1].IsValid);
        Assert.NotEmpty(candidates[1].ValidationMessage);
    }

    [Fact]
    public async Task Batch_ContinuesAfterFailureAndAllocatesUniqueNames()
    {
        var outputDirectory = Path.Combine(fixture.DirectoryPath, "batch-output");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "same.mp4"), "existing");

        var candidates = new[]
        {
            Candidate("first.secvid", "same.mp4", 100),
            Candidate("fail.secvid", "same.mp4", 200),
            Candidate("third.secvid", "same.mp4", 300)
        };
        var progress = new List<BatchDecryptionProgress>();
        var service = CreateService(new RecordingDecryptor("fail.secvid"));

        var result = await service.DecryptBatchAsync(
            candidates,
            outputDirectory,
            "password",
            new InlineProgress<BatchDecryptionProgress>(progress.Add));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(progress, item => item.InputPath.EndsWith("fail.secvid") && item.State == VideoTaskState.Failed);
        Assert.True(progress.Zip(progress.Skip(1), (left, right) => right.OverallPercentage >= left.OverallPercentage)
            .All(isMonotonic => isMonotonic));
        Assert.Equal(["same (1).mp4", "same (3).mp4"],
            result.OutputPaths.Select(path => Path.GetFileName(path)!).ToArray());
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "same.mp4")));
    }

    [Fact]
    public void OutputResolver_SanitizesTraversalAndReservedNames()
    {
        var outputDirectory = Path.Combine(fixture.DirectoryPath, "safe-output");
        Directory.CreateDirectory(outputDirectory);
        var candidate = Candidate("fallback.secvid", "..\\CON.mp4", 1);

        var result = new DecryptionOutputPathResolver().GetAvailablePath(
            outputDirectory,
            candidate,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(result));
        Assert.Equal("fallback.mp4", Path.GetFileName(result));
    }

    [Fact]
    public async Task ViewModel_AddsCandidatesAndKeepsPasswordOutOfItems()
    {
        var service = new StubDecryptionService();
        using var lifetime = new TestDocumentLifetime();
        using var viewModel = TestViewModelFactory.CreateDecryptor(service, lifetime);

        Assert.False(viewModel.HasItems);
        await viewModel.AddFilesAsync(["one.secvid", "one.secvid", "bad.secvid"]);
        Assert.True(viewModel.HasItems);
        viewModel.Password = "top-secret";

        Assert.Equal(2, viewModel.ItemCount);
        Assert.Equal(1, viewModel.FailedCount);
        Assert.DoesNotContain(viewModel.Items, item =>
            item.Message.Contains("top-secret", StringComparison.Ordinal) ||
            item.OutputPath.Contains("top-secret", StringComparison.Ordinal));

        viewModel.Dispose();
        Assert.Empty(viewModel.Password);
    }

    [Fact]
    public async Task ViewModel_DisposeCancelsRunningBatch()
    {
        var service = new CancellationRecordingService();
        using var lifetime = new TestDocumentLifetime();
        var viewModel = TestViewModelFactory.CreateDecryptor(service, lifetime);
        await viewModel.AddFilesAsync(["one.secvid"]);
        viewModel.SetOutputDirectory(fixture.DirectoryPath);
        viewModel.Password = "password";

        var running = viewModel.StartDecryptionCommand.ExecuteAsync(null);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.Dispose();
        await running;

        Assert.True(service.CancellationObserved);
        Assert.Empty(viewModel.Password);
    }

    [Fact]
    public async Task Document_ExposesDefaultPresentation()
    {
        using var lifetime = new TestDocumentLifetime();
        using var document = TestViewModelFactory.CreateDecryptor(
            new StubDecryptionService(), lifetime);
        await document.InitializeAsync(new NewDocumentActivation(string.Empty), default);
        Assert.Equal("批量视频解密器", document.Presentation.Title);
    }

    [Fact]
    public async Task Document_PreservesCustomTitle()
    {
        using var lifetime = new TestDocumentLifetime();
        using var document = TestViewModelFactory.CreateDecryptor(
            new StubDecryptionService(), lifetime);
        await document.InitializeAsync(
            new NewDocumentActivation("自定义解密任务"), default);
        Assert.Equal("自定义解密任务", document.Presentation.Title);
    }

    private static DecryptionCandidate Candidate(string inputName, string originalName, long length) =>
        new(
            Path.GetFullPath(inputName),
            inputName,
            originalName,
            ".mp4",
            "公开标题",
            length,
            true,
            string.Empty);

    private static VideoDecryptionService CreateService(ISecvid03Decryptor decryptor) =>
        new(decryptor, new DecryptionOutputPathResolver(), new StoragePreflightProbe());

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x80));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingDecryptor(string? failingFileName = null) : ISecvid03Decryptor
    {
        public async Task DecryptAsync(
            string inputPath,
            string outputPath,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(inputPath).Equals(failingFileName, StringComparison.OrdinalIgnoreCase))
                throw new VideoTaskException(
                    VideoTaskFailureCode.AuthenticationFailed,
                    "密码错误或固定头已损坏。");

            progress?.Report(new VideoTaskProgress(VideoTaskState.Running, 1, 1, 100, "解密完成"));
            await File.WriteAllTextAsync(outputPath, Path.GetFileName(inputPath), cancellationToken);
        }
    }

    private sealed class StubDecryptionService : IVideoDecryptionService
    {
        public Task<IReadOnlyList<DecryptionCandidate>> InspectAsync(
            IReadOnlyList<string> inputPaths,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DecryptionCandidate> result = inputPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetFileName(path).StartsWith("bad", StringComparison.OrdinalIgnoreCase)
                    ? new DecryptionCandidate(path, path, string.Empty, string.Empty, string.Empty, 0, false, "无效文件")
                    : new DecryptionCandidate(path, path, "one.mp4", ".mp4", "标题", 10, true, string.Empty))
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<BatchDecryptionResult> DecryptBatchAsync(
            IReadOnlyList<DecryptionCandidate> candidates,
            string outputDirectory,
            string password,
            IProgress<BatchDecryptionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
             Task.FromResult(new BatchDecryptionResult(0, 0, 0, 0, []));

        public Task<BatchDecryptionPreflightResult> PreflightAsync(
            IReadOnlyList<DecryptionCandidate> candidates,
            string outputDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadyPreflight(candidates, outputDirectory));
    }

    private sealed class CancellationRecordingService : IVideoDecryptionService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public Task<IReadOnlyList<DecryptionCandidate>> InspectAsync(
            IReadOnlyList<string> inputPaths,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DecryptionCandidate> candidates =
            [new(inputPaths[0], "one.secvid", "one.mp4", ".mp4", string.Empty, 10, true, string.Empty)];
            return Task.FromResult(candidates);
        }

        public async Task<BatchDecryptionResult> DecryptBatchAsync(
            IReadOnlyList<DecryptionCandidate> candidates,
            string outputDirectory,
            string password,
            IProgress<BatchDecryptionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        }

        public Task<BatchDecryptionPreflightResult> PreflightAsync(
            IReadOnlyList<DecryptionCandidate> candidates,
            string outputDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadyPreflight(candidates, outputDirectory));
    }

    private static BatchDecryptionPreflightResult ReadyPreflight(
        IReadOnlyList<DecryptionCandidate> candidates,
        string outputDirectory) =>
        new(
            VideoPreflightResult.Ready(candidates.Sum(candidate => candidate.OriginalFileLength)),
            candidates.Select(candidate => new CandidateDecryptionPreflight(
                candidate,
                Path.Combine(outputDirectory, candidate.OriginalFileName),
                VideoPreflightResult.Ready(candidate.OriginalFileLength))).ToArray());

}
