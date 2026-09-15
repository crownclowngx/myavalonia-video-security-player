using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>公开信息编辑依赖的窄文件端口，不包含密码或播放操作。</summary>
public interface IPublicVideoInfoStore
{
    EncryptedVideoPublicInfo Read(string path);
    Task UpdateAsync(string path, string title, string description, CancellationToken cancellationToken);
}

/// <summary>复用现有公开区读写；固定大小的文件更新放在后台，避免阻塞界面。</summary>
public sealed class PublicVideoInfoStore : IPublicVideoInfoStore
{
    public EncryptedVideoPublicInfo Read(string path) => EncryptedVideoContainer.ReadPublicInfo(path);

    public Task UpdateAsync(string path, string title, string description, CancellationToken cancellationToken) =>
        Task.Run(() => EncryptedVideoContainer.UpdatePublicInfo(path, title, description), cancellationToken);
}
