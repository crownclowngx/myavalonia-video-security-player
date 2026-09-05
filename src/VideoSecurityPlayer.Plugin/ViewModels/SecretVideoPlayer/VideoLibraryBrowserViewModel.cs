using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;
using MyAvaloniaManagement.PluginSdk;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 媒体库浏览器的兼容外壳；目录会话和可见投影由 Library 功能包统一实现。
/// </summary>
public sealed class VideoLibraryBrowserViewModel : LibraryBrowserCoordinatorViewModel
{
    public VideoLibraryBrowserViewModel(
        IVideoLibraryScanner scanner,
        IDocumentLifetime documentLifetime,
        IVideoLibrarySettingsStore? settingsStore = null,
        IPlaybackHistoryStore? historyStore = null,
        IVideoLibraryCatalogSession? catalog = null)
        : base(scanner, documentLifetime, settingsStore, historyStore, catalog)
    {
    }
}
