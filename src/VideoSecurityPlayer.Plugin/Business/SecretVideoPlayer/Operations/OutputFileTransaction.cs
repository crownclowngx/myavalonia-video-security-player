namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

public interface IOutputFileTransactionFactory
{
    IOutputFileTransaction Create(string finalPath);
}

public interface IOutputFileTransaction : IAsyncDisposable
{
    Stream Stream { get; }
    string FinalPath { get; }
    string TemporaryPath { get; }
    VideoTaskException? CleanupError { get; }
    Task CommitAsync(CancellationToken cancellationToken = default);
}

public sealed class OutputFileTransactionFactory : IOutputFileTransactionFactory
{
    public IOutputFileTransaction Create(string finalPath) => new OutputFileTransaction(finalPath);
}

internal sealed class OutputFileTransaction : IOutputFileTransaction
{
    private readonly FileStream _stream;
    private bool _streamClosed;
    private bool _committed;
    private bool _disposed;

    public OutputFileTransaction(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        FinalPath = Path.GetFullPath(finalPath);
        TemporaryPath = FinalPath + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            _stream = new FileStream(
                TemporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            throw VideoTaskFailureClassifier.Map(ex, readingInput: false);
        }
    }

    public Stream Stream => !_streamClosed
        ? _stream
        : throw new ObjectDisposedException(nameof(OutputFileTransaction));

    public string FinalPath { get; }
    public string TemporaryPath { get; }
    public VideoTaskException? CleanupError { get; private set; }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            throw new InvalidOperationException("输出事务已经提交。");

        cancellationToken.ThrowIfCancellationRequested();
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _streamClosed = true;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(TemporaryPath, FinalPath, overwrite: false);
            _committed = true;
        }
        catch (IOException ex) when (File.Exists(FinalPath))
        {
            throw new VideoTaskException(
                VideoTaskFailureCode.OutputConflict,
                "输出文件已被其他程序创建，请更换名称后重试。",
                ex);
        }
        catch (Exception ex)
        {
            throw VideoTaskFailureClassifier.Map(ex, readingInput: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_streamClosed)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _streamClosed = true;
            }
            catch (Exception ex)
            {
                CleanupError = new VideoTaskException(
                    VideoTaskFailureCode.CleanupFailed,
                    "未能关闭当前任务的临时输出流。",
                    ex);
            }
        }

        if (_committed || !File.Exists(TemporaryPath))
            return;

        try
        {
            File.Delete(TemporaryPath);
        }
        catch (Exception ex)
        {
            CleanupError ??= new VideoTaskException(
                VideoTaskFailureCode.CleanupFailed,
                "未能清理当前任务的临时文件。",
                ex);
        }
    }
}
