using Avalonia.Input;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

/// <summary>播放器作用域快捷键映射结果。</summary>
internal enum PlaybackShortcutAction
{
    None,
    TogglePlayPause,
    SeekBackward,
    SeekForward,
    IncreaseVolume,
    DecreaseVolume,
    ExitFullscreen
}

/// <summary>
/// 不依赖播放会话的纯快捷键策略。
/// </summary>
/// <remarks>
/// 焦点是否属于输入控件由 View 判断，本类型只负责“按键 + 修饰键 + 全屏状态”
/// 到用户意图的稳定映射，因此无需创建 LibVLC 或真实窗口即可单元测试。
/// </remarks>
internal static class PlaybackShortcutPolicy
{
    public static PlaybackShortcutAction Map(
        Key key,
        KeyModifiers modifiers,
        bool isFullscreen)
    {
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return PlaybackShortcutAction.None;
        }

        return key switch
        {
            Key.Space => PlaybackShortcutAction.TogglePlayPause,
            Key.Left => PlaybackShortcutAction.SeekBackward,
            Key.Right => PlaybackShortcutAction.SeekForward,
            Key.Up => PlaybackShortcutAction.IncreaseVolume,
            Key.Down => PlaybackShortcutAction.DecreaseVolume,
            Key.Escape when isFullscreen => PlaybackShortcutAction.ExitFullscreen,
            _ => PlaybackShortcutAction.None
        };
    }
}
