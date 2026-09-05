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
