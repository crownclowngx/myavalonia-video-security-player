using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

[Collection(Secvid03Collection.Name)]
public sealed class G2ReliabilityTests(Secvid03Fixture fixture)
{
    [Fact]
    public async Task EncryptionPreflight_ComputesExactContainerLengthAndRejectsConflicts()
    {
        var outputPath = Path.Combine(fixture.DirectoryPath, "g2-preflight.secvid");
        var service = new VideoEncryptorService(
            new RecordingEncryptor(),
            new StoragePreflightProbe());
        var request = new VideoEncryptionRequest(
            fixture.OriginalPath,
            outputPath,
            "公开标题",
            "公开描述");

        var result = await service.PreflightAsync(request);
        using var input = File.OpenRead(fixture.OriginalPath);
        var prefixLength = Secvid03Format.DetectOriginalHeaderLength(input);
        var expected = Secvid03Format
            .CalculateLayout(input.Length, prefixLength)
            .PhysicalFileLength;

        Assert.True(result.CanProceed);
        Assert.Equal(expected, result.RequiredBytes);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".secvid-write-probe-*"));

        await File.WriteAllTextAsync(outputPath, "existing");
        var conflict = await service.PreflightAsync(request);
        Assert.False(conflict.CanProceed);
        Assert.Contains(conflict.Issues, issue => issue.Code == VideoTaskFailureCode.OutputConflict);

        var samePath = await service.PreflightAsync(request with { OutputPath = fixture.OriginalPath });
        Assert.False(samePath.CanProceed);
        Assert.Contains(samePath.Issues, issue => issue.Code == VideoTaskFailureCode.InputOutputConflict);
    }

    [Fact]
    public async Task OutputTransaction_RollsBackAndNeverOverwritesCompetingTarget()
    {
        var finalPath = Path.Combine(fixture.DirectoryPath, "g2-transaction.bin");
        var factory = new OutputFileTransactionFactory();

        var rollback = factory.Create(finalPath);
        var rollbackTemporary = rollback.TemporaryPath;
        await rollback.Stream.WriteAsync("partial"u8.ToArray());
        await rollback.DisposeAsync();
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(rollbackTemporary));

        var competing = factory.Create(finalPath);
        var competingTemporary = competing.TemporaryPath;
        await competing.Stream.WriteAsync("new-data"u8.ToArray());
        await File.WriteAllTextAsync(finalPath, "keep-me");
        var error = await Assert.ThrowsAsync<VideoTaskException>(
            () => competing.CommitAsync());
        await competing.DisposeAsync();

        Assert.Equal(VideoTaskFailureCode.OutputConflict, error.FailureCode);
        Assert.Equal("keep-me", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(competingTemporary));
    }

    [Fact]
    public async Task WrongPassword_IsRejectedBeforePlaintextTransactionExists()
    {
        var factory = new RecordingTransactionFactory();
        var outputPath = Path.Combine(fixture.DirectoryPath, "g2-wrong-password.mp4");

        var error = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Decryptor(factory).DecryptAsync(
                fixture.EncryptedPath,
                outputPath,
                "definitely-wrong"));

        Assert.Equal(VideoTaskFailureCode.AuthenticationFailed, error.FailureCode);
        Assert.Equal(0, factory.CreateCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task EncryptionWriteDiskFull_HasStableCodeAndDisposesTransaction()
    {
        var factory = new DiskFullTransactionFactory();
        var outputPath = Path.Combine(fixture.DirectoryPath, "g2-disk-full.secvid");

        var error = await Assert.ThrowsAsync<VideoTaskException>(() =>
            new Secvid03Encryptor(factory).EncryptAsync(
                new VideoEncryptionRequest(
                    fixture.OriginalPath,
                    outputPath,
                    string.Empty,
                    string.Empty),
                Secvid03Fixture.Password));

        Assert.Equal(VideoTaskFailureCode.InsufficientDiskSpace, error.FailureCode);
        Assert.True(factory.Transaction?.Disposed);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task DecryptionPreflight_BlocksOnlyItemsBeyondKnownFreeSpace()
    {
        var outputDirectory = Path.Combine(fixture.DirectoryPath, "g2-space-batch");
        Directory.CreateDirectory(outputDirectory);
        var candidates = new[]
        {
            Candidate("first.secvid", "first.mp4", 100),
            Candidate("second.secvid", "second.mp4", 100)
        };
        var service = new VideoDecryptionService(
            new NoOpDecryptor(),
            new DecryptionOutputPathResolver(),
            new FixedStorageProbe(150));

        var result = await service.PreflightAsync(candidates, outputDirectory);

        Assert.True(result.Overall.CanProceed);
        Assert.True(result.Items[0].Result.CanProceed);
        Assert.False(result.Items[1].Result.CanProceed);
        Assert.Contains(result.Items[1].Result.Issues,
            issue => issue.Code == VideoTaskFailureCode.InsufficientDiskSpace);
    }

    [Fact]
    public void OperationAndQueueModels_DoNotExposePasswordMembers()
    {
        var types = new[]
        {
            typeof(VideoEncryptionRequest),
            typeof(VideoPreflightResult),
            typeof(VideoTaskProgress),
            typeof(DecryptionCandidate),
            typeof(BatchDecryptionProgress),
            typeof(BatchDecryptionResult)
        };

        foreach (var type in types)
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                type.GetFields(),
                field => field.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task EncryptionService_IsReplaceableAndPassesPasswordOnlyAtCallTime()
    {
        var encryptor = new RecordingEncryptor();
        var service = new VideoEncryptorService(
            encryptor,
            new FixedStorageProbe(long.MaxValue));
        var outputPath = Path.Combine(fixture.DirectoryPath, "g2-replaceable.secvid");
        var request = new VideoEncryptionRequest(
            fixture.OriginalPath,
            outputPath,
            string.Empty,
            string.Empty);

        await service.EncryptAsync(request, "call-only-secret");

        Assert.Equal(request, encryptor.Request);
        Assert.Equal("call-only-secret", encryptor.ReceivedPassword);
        Assert.DoesNotContain("call-only-secret", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptionDocuments_HaveIndependentCancellationAndClearPasswordsOnDispose()
    {
        var firstService = new BlockingEncryptionService();
        var secondService = new BlockingEncryptionService();
        using var firstLifetime = new TestDocumentLifetime();
        using var secondLifetime = new TestDocumentLifetime();
        var first = CreateEncryptionDocument(firstService, firstLifetime, "first");
        using var second = CreateEncryptionDocument(secondService, secondLifetime, "second");

        var firstRun = first.StartEncryptionCommand.ExecuteAsync(null);
        var secondRun = second.StartEncryptionCommand.ExecuteAsync(null);
        await Task.WhenAll(
            firstService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            secondService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        first.Dispose();
        await firstRun;

        Assert.True(firstService.CancellationObserved);
        Assert.False(secondService.CancellationObserved);
        Assert.True(second.IsEncrypting);
        Assert.Empty(first.Password);
        Assert.Empty(first.ConfirmPassword);

        second.CancelEncryptionCommand.Execute(null);
        await secondRun;
        Assert.True(secondService.CancellationObserved);
    }

    private VideoEncryptorViewModel CreateEncryptionDocument(
        IVideoEncryptionService service,
        TestDocumentLifetime lifetime,
        string suffix)
    {
        var document = TestViewModelFactory.CreateEncryptor(service, lifetime);
        document.SelectedFilePath = fixture.OriginalPath;
        document.OutputFilePath = Path.Combine(
            fixture.DirectoryPath,
            $"g2-{suffix}.secvid");
        document.Password = "123456";
        document.ConfirmPassword = "123456";
        return document;
    }

    private static DecryptionCandidate Candidate(string inputName, string originalName, long length) =>
        new(
            Path.GetFullPath(inputName),
            inputName,
            originalName,
            ".mp4",
            string.Empty,
            length,
            true,
            string.Empty);

    private sealed class RecordingEncryptor : ISecvid03Encryptor
    {
        public VideoEncryptionRequest? Request { get; private set; }
        public string? ReceivedPassword { get; private set; }

        public Task EncryptAsync(
            VideoEncryptionRequest request,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            ReceivedPassword = password;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDecryptor : ISecvid03Decryptor
    {
        public Task DecryptAsync(
            string inputPath,
            string outputPath,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedStorageProbe(long availableBytes) : IStoragePreflightProbe
    {
        public Task<VideoPreflightResult> CheckAsync(
            string outputDirectory,
            long requiredBytes,
            bool createDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(requiredBytes, availableBytes));
    }

    private sealed class BlockingEncryptionService : IVideoEncryptionService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public Task<VideoPreflightResult> PreflightAsync(
            VideoEncryptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(1, long.MaxValue));

        public async Task EncryptAsync(
            VideoEncryptionRequest request,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
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
        }
    }

    private sealed class RecordingTransactionFactory : IOutputFileTransactionFactory
    {
        public int CreateCount { get; private set; }

        public IOutputFileTransaction Create(string finalPath)
        {
            CreateCount++;
            return new MemoryTransaction(finalPath, new MemoryStream());
        }
    }

    private sealed class DiskFullTransactionFactory : IOutputFileTransactionFactory
    {
        public MemoryTransaction? Transaction { get; private set; }

        public IOutputFileTransaction Create(string finalPath)
        {
            Transaction = new MemoryTransaction(finalPath, new DiskFullStream());
            return Transaction;
        }
    }

    private sealed class MemoryTransaction : IOutputFileTransaction
    {
        public MemoryTransaction(string finalPath, Stream stream)
        {
            FinalPath = finalPath;
            TemporaryPath = finalPath + ".partial-test";
            Stream = stream;
        }

        public Stream Stream { get; }
        public string FinalPath { get; }
        public string TemporaryPath { get; }
        public VideoTaskException? CleanupError => null;
        public bool Disposed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await Stream.DisposeAsync();
        }
    }

    private sealed class DiskFullStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw CreateDiskFull();
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(CreateDiskFull());

        private static IOException CreateDiskFull() =>
            new("disk full", unchecked((int)0x80070070));
    }
}
