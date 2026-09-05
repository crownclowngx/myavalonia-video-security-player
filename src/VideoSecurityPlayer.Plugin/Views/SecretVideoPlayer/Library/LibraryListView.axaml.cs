using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Library;

/// <summary>媒体库搜索和虚拟化列表视图，拥有双击与 Enter 的显式播放手势。</summary>
public partial class LibraryListView : UserControl
{
    public LibraryListView() => InitializeComponent();

    private async void OnVideoListDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        await ExecuteActivateSelectedAsync();
    }

    private async void OnVideoListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await ExecuteActivateSelectedAsync();
    }

    private async Task ExecuteActivateSelectedAsync()
    {
        if (DataContext is not LibraryPlaybackViewModel playback)
            return;
        var command = playback.Owner.ActivateSelectedCommand;
        if (command.CanExecute(null))
            await command.ExecuteAsync(null);
    }
}
