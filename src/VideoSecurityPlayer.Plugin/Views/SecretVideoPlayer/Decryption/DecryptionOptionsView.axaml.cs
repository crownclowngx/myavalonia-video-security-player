using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Decryption;

/// <summary>解密输出目录、密码和非覆盖策略视图。</summary>
public partial class DecryptionOptionsView : UserControl
{
    private bool _isFolderPickerOpen;

    public DecryptionOptionsView() => InitializeComponent();

    private async void OnChooseOutputDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFolderPickerOpen || DataContext is not DecryptionBatchViewModel batch || batch.Owner.IsBusy)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isFolderPickerOpen = true;
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择解密视频输出目录",
                AllowMultiple = false
            });
            if (folders.Count > 0 && ReferenceEquals(DataContext, batch))
                batch.Owner.SetOutputDirectory(folders[0].Path.LocalPath);
        }
        catch
        {
            if (ReferenceEquals(DataContext, batch))
                batch.Owner.StatusMessage = "选择输出目录失败，请重新打开目录选择器后重试。";
        }
        finally
        {
            _isFolderPickerOpen = false;
        }
    }
}
