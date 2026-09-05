using Avalonia.Input;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>验证播放器快捷键映射不依赖窗口或 LibVLC。</summary>
public sealed class G6PresentationPolicyTests
{
    [Theory]
    [InlineData(Key.Space, (int)PlaybackShortcutAction.TogglePlayPause)]
    [InlineData(Key.Left, (int)PlaybackShortcutAction.SeekBackward)]
    [InlineData(Key.Right, (int)PlaybackShortcutAction.SeekForward)]
    [InlineData(Key.Up, (int)PlaybackShortcutAction.IncreaseVolume)]
    [InlineData(Key.Down, (int)PlaybackShortcutAction.DecreaseVolume)]
    public void 播放器日常快捷键映射到稳定用户意图(
        Key key,
        int expected)
    {
        Assert.Equal(
            (PlaybackShortcutAction)expected,
            PlaybackShortcutPolicy.Map(key, KeyModifiers.None, isFullscreen: false));
    }

    [Fact]
    public void Esc只在全屏时映射为退出()
    {
        Assert.Equal(
            PlaybackShortcutAction.None,
            PlaybackShortcutPolicy.Map(Key.Escape, KeyModifiers.None, isFullscreen: false));
        Assert.Equal(
            PlaybackShortcutAction.ExitFullscreen,
            PlaybackShortcutPolicy.Map(Key.Escape, KeyModifiers.None, isFullscreen: true));
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    public void 宿主级修饰键组合不会被播放器截获(KeyModifiers modifiers)
    {
        Assert.Equal(
            PlaybackShortcutAction.None,
            PlaybackShortcutPolicy.Map(Key.Space, modifiers, isFullscreen: true));
    }
}
