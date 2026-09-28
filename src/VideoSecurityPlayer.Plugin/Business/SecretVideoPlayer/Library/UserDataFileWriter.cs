namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>将写入分为准备与提交，使存储能在最终替换前复核修订号。</summary>
internal interface IUserDataFileWriter
{
    Task<IPreparedUserDataWrite> PrepareAsync(string path, byte[] bytes, CancellationToken token);
}

/// <summary>只拥有本次创建的临时文件；未提交时释放即丢弃，不形成历史备份。</summary>
internal interface IPreparedUserDataWrite : IDisposable
{
    void Commit();
}

/// <summary>同目录临时文件与原子替换的文件适配器，不决定用户数据的业务版本。</summary>
internal sealed class UserDataFileWriter : IUserDataFileWriter
{
    public async Task<IPreparedUserDataWrite> PrepareAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = new PreparedWrite(path, path + ".partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(pending.TemporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            return pending;
        }
        catch { pending.Dispose(); throw; }
    }

    private sealed class PreparedWrite(string path, string temporaryPath) : IPreparedUserDataWrite
    {
        public string TemporaryPath { get; } = temporaryPath;
        public void Commit() => File.Move(TemporaryPath, path, overwrite: true);
        public void Dispose()
        {
            try { File.Delete(TemporaryPath); }
            catch (IOException) { /* 临时文件不会被读取为正式配置，清理失败不恢复旧历史。 */ }
            catch (UnauthorizedAccessException) { /* 正式提交结果不受临时文件清理影响。 */ }
        }
    }
}
