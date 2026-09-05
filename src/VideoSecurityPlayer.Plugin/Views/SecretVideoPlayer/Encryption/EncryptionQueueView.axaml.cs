using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Encryption;

/// <summary>加密队列视图；只在 View 边界访问窗口级文件选择器。</summary>
public partial class EncryptionQueueView : UserControl
{
    private bool _isFilePickerOpen;

    public EncryptionQueueView() => InitializeComponent();

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_isFilePickerOpen || DataContext is not EncryptionQueueViewModel queue || queue.Owner.IsBusy)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isFilePickerOpen = true;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要加入加密队列的视频文件",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("视频文件")
                    {
                        Patterns = ["*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv", "*.flv", "*.webm", "*.m4v"]
                    },
                    new FilePickerFileType("所有文件") { Patterns = ["*.*"] }
                ]
            });

            if (ReferenceEquals(DataContext, queue) && files.Count > 0)
                await queue.Owner.AddFilesAsync(files.Where(file => file.Path.IsFile)
                    .Select(file => file.Path.LocalPath).ToArray());
        }
        catch
        {
            if (ReferenceEquals(DataContext, queue))
                queue.Owner.StatusMessage = "选择文件失败，请重新打开文件选择器后重试。";
        }
        finally
        {
            _isFilePickerOpen = false;
        }
    }
}
