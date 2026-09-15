using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Library;

/// <summary>媒体库低频设置视图；文件夹选择器结果只写回发起请求的 Document。</summary>
public partial class LibrarySettingsView : UserControl
{
    private bool _isFolderPickerOpen;

    public LibrarySettingsView() => InitializeComponent();

    private void OnRecentFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not LibraryLayoutViewModel layout) return;
        var menu = new MenuFlyout();
        foreach (var path in layout.Owner.Browser.RecentFolders)
            menu.Items.Add(new MenuItem { Header = path, Command = layout.Owner.OpenRecentFolderCommand, CommandParameter = path });
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无最近目录", IsEnabled = false });
        if (layout.Owner.Browser.RecentFolders.Count > 0)
        {
            var remove = new MenuItem { Header = "移除最近记录" };
            foreach (var path in layout.Owner.Browser.RecentFolders)
                remove.Items.Add(new MenuItem { Header = path, Command = layout.Owner.Browser.RemoveRecentFolderCommand, CommandParameter = path });
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
        }
        menu.ShowAt(button);
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_isFolderPickerOpen || DataContext is not LibraryLayoutViewModel layout || layout.Owner.IsOpening)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isFolderPickerOpen = true;
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择加密视频文件夹",
                AllowMultiple = false
            });
            if (folders.Count > 0 && ReferenceEquals(DataContext, layout))
                await layout.Owner.OpenFolderAsync(folders[0].Path.LocalPath);
        }
        catch
        {
            if (ReferenceEquals(DataContext, layout))
                layout.Owner.StatusMessage = "选择文件夹失败，请重新打开目录选择器后重试";
        }
        finally
        {
            _isFolderPickerOpen = false;
        }
    }
}
