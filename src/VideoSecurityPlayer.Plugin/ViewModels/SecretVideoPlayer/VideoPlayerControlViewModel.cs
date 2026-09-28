using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.Features.Settings;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>
/// 播放控件的兼容外壳；播放状态、命令和呈现协调由 Playback 功能包统一实现。
/// </summary>
public sealed class VideoPlayerControlViewModel : PlaybackCoordinatorViewModel
{
    public VideoPlayerControlViewModel(
        ISecureVideoPlaybackSession session,
        IPlaybackSurfaceSession surfaceSession,
        IPlaybackPlatformStatus platformStatus,
        IPlaybackBackendInitializer backendInitializer,
        IPlaybackPreferenceStore? preferenceStore = null,
        IVideoSettingsReader? settingsReader = null,
        VideoSettingsTool? settings = null)
        : base(
            session,
            surfaceSession,
            platformStatus,
            backendInitializer,
            preferenceStore,
            settingsReader,
            settings)
    {
    }
}
