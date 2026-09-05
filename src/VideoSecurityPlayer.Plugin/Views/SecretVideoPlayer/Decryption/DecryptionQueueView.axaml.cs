using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Decryption;

/// <summary>解密候选队列视图；文件选择结果只回写发起请求的 Document。</summary>
public partial class DecryptionQueueView : UserControl
{
    private bool _isFilePickerOpen;

    public DecryptionQueueView() => InitializeComponent();

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (_isFilePickerOpen || DataContext is not DecryptionQueueViewModel queue || queue.Owner.IsBusy)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isFilePickerOpen = true;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要解密的 SECVID03 视频",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("SECVID03 加密视频")
                    {
                        Patterns = ["*.secvid"],
                        MimeTypes = ["application/octet-stream"]
                    }
                ]
            });
            if (files.Count > 0 && ReferenceEquals(DataContext, queue))
                await queue.Owner.AddFilesAsync(files.Select(file => file.Path.LocalPath).ToArray());
        }
        catch
        {
            if (ReferenceEquals(DataContext, queue))
                queue.Owner.StatusMessage = "选择视频失败，请重新打开文件选择器后重试。";
        }
        finally
        {
            _isFilePickerOpen = false;
        }
    }
}
