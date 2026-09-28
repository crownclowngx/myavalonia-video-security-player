using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

public partial class VideoPlayerControl
{
    /// <summary>复用单例草稿，但 View 属于当前页面；通过网格分配空间，不覆盖或重新挂载 HWND。</summary>
    internal void ShowSettings(bool runtime)
    {
        if (DataContext is not VideoPlayerControlViewModel { Settings: { } settings }) return;
        if (runtime) settings.ShowRuntime();
        SettingsHost.IsVisible = true;
        ApplySettingsLayout();
    }
    private void OnOpenSettings(object? sender, RoutedEventArgs e) => ShowSettings(false);
    private void OnCloseSettings(object? sender, RoutedEventArgs e)
    {
        SettingsHost.IsVisible = false;
        ApplySettingsLayout();
        OpenSettingsButton.Focus();
    }
    private void OnSettingsSizeChanged(object? sender, SizeChangedEventArgs e) => ApplySettingsLayout();
    private void ApplySettingsLayout()
    {
        if (SettingsLayout is null || SettingsHost is null) return;
        var wide = Bounds.Width >= 1040;
        SettingsLayout.ColumnDefinitions[1].Width = SettingsHost.IsVisible && wide ? new GridLength(400) : new GridLength(0);
        SettingsLayout.RowDefinitions[2].Height = SettingsHost.IsVisible && !wide ? new GridLength(Math.Min(300, Math.Max(160, Bounds.Height * 0.45))) : new GridLength(0);
        Grid.SetColumn(SettingsHost, wide ? 1 : 0);
        Grid.SetRow(SettingsHost, wide ? 1 : 2);
    }
}
