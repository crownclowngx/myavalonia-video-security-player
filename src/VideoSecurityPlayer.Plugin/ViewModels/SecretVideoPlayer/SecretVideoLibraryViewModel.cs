using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;
using MyAvaloniaManagement.PluginSdk;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 媒体库 Document 的兼容外壳；播放、历史、布局和浏览协调位于 Library 功能包。
/// </summary>
public sealed class SecretVideoLibraryViewModel : LibraryDocumentCoordinatorViewModel, IPluginDocument
{
    private string _title = "加密视频库播放器";

    public SecretVideoLibraryViewModel(
        VideoLibraryBrowserViewModel browser,
        VideoPlayerControlViewModel playerViewModel,
        IDocumentLifetime documentLifetime,
        IPlaybackHistoryStore? historyStore = null,
        IVideoLibrarySettingsStore? settingsStore = null,
        PlaybackHistoryCoordinator? historyCoordinator = null,
        ISecretVideoUserDataDiagnostics? userDataDiagnostics = null)
        : base(
            browser,
            playerViewModel,
            documentLifetime,
            historyStore,
            settingsStore,
            historyCoordinator,
            userDataDiagnostics)
    {
    }

    public DocumentPresentationState Presentation => new(_title);

    public event EventHandler? PresentationChanged;

    public ValueTask InitializeAsync(
        DocumentActivation activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation)
        {
            // 媒体库的历史和设置由插件私有存储拥有，不是 Document envelope 内容。
            throw new NotSupportedException("加密视频库播放器只支持新建激活。");
        }

        var title = string.IsNullOrWhiteSpace(activation.Title) ? "加密视频库播放器" : activation.Title;
        if (!string.Equals(_title, title, StringComparison.Ordinal))
        {
            _title = title;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }
}
