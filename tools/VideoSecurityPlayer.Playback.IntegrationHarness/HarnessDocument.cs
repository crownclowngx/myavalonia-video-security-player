using MyAvaloniaManagement.Business.Docking;

namespace VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>把 Harness 使用的 Host Dock 所有权与插件业务模型明确分开。</summary>
/// <remarks>
/// 该记录只存在于真实窗口测试中：Dockable 用于激活、关闭和标题断言，Model 用于播放器与
/// 业务断言。生产插件因此无需重新继承 Dock，也不会为了测试引入反向 Host 依赖。
/// </remarks>
internal sealed record HarnessDocument<TModel>(
    ManagedDocumentDockable Dockable,
    TModel Model)
    where TModel : class
{
    public string Title
    {
        get => Dockable.Title ?? string.Empty;
        set => Dockable.Title = value;
    }

    public static implicit operator ManagedDocumentDockable(HarnessDocument<TModel> document) =>
        document.Dockable;
}

internal sealed record HarnessPlayerDocument(
    ManagedDocumentDockable Dockable,
    VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SecretVideoPlayerViewModel Model)
{
    public VideoSecurityPlayer.ViewModels.SecretVideoPlayer.VideoPlayerControlViewModel PlayerViewModel =>
        Model.PlayerViewModel;

    public string Title
    {
        get => Dockable.Title ?? string.Empty;
        set => Dockable.Title = value;
    }

    public static implicit operator ManagedDocumentDockable(HarnessPlayerDocument document) =>
        document.Dockable;
}

internal sealed record HarnessLibraryDocument(
    ManagedDocumentDockable Dockable,
    VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SecretVideoLibraryViewModel Model)
{
    public VideoSecurityPlayer.ViewModels.SecretVideoPlayer.VideoPlayerControlViewModel PlayerViewModel =>
        Model.PlayerViewModel;
    public bool IsContinuousPlaybackEnabled => Model.IsContinuousPlaybackEnabled;

    public static implicit operator ManagedDocumentDockable(HarnessLibraryDocument document) =>
        document.Dockable;
}
