using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Playback;

/// <summary>
/// 把播放器作用域按键映射为现有命令，并主动避开文本编辑、选择器和按钮操作。
/// </summary>
public static class PlaybackShortcutRouter
{
    public static bool TryHandle(KeyEventArgs e, VideoPlayerControlViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (IsEditingOrSelectingControl(e.Source))
            return false;

        var action = PlaybackShortcutPolicy.Map(
            e.Key,
            e.KeyModifiers,
            viewModel.IsFullscreen);
        var command = action switch
        {
            PlaybackShortcutAction.TogglePlayPause => viewModel.TogglePlayPauseCommand,
            PlaybackShortcutAction.SeekBackward => viewModel.SeekBackwardCommand,
            PlaybackShortcutAction.SeekForward => viewModel.SeekForwardCommand,
            PlaybackShortcutAction.IncreaseVolume => viewModel.IncreaseVolumeCommand,
            PlaybackShortcutAction.DecreaseVolume => viewModel.DecreaseVolumeCommand,
            PlaybackShortcutAction.ExitFullscreen => viewModel.ToggleFullscreenCommand,
            _ => null
        };

        if (command?.CanExecute(null) != true)
            return false;
        command.Execute(null);
        return true;
    }

    private static bool IsEditingOrSelectingControl(object? source)
    {
        if (source is not Visual visual)
            return false;
        return visual.GetSelfAndVisualAncestors().Any(ancestor =>
            ancestor is TextBox or ComboBox or Slider or ListBox or Button);
    }
}
