using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.SingleVideo;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.SingleVideo;

/// <summary>
/// 单文件来源视图。文件选择器依赖当前 TopLevel，因此刻意保留在 View 边界。
/// </summary>
public partial class SingleVideoSourceView : UserControl
{
    private bool _isPickerOpen;

    public SingleVideoSourceView()
    {
        InitializeComponent();
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_isPickerOpen || DataContext is not SingleVideoSourceViewModel source)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _isPickerOpen = true;
        try
        {
            // 保存请求发起时的组件，防止 Dock 重建后把结果写入另一个 Document。
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择加密视频文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("加密视频文件") { Patterns = ["*.secvid"] },
                    new FilePickerFileType("所有文件") { Patterns = ["*.*"] }
                ]
            });

            if (files.Count > 0 && ReferenceEquals(DataContext, source))
                await source.SelectFileAsync(files[0].Path.LocalPath);
        }
        finally
        {
            _isPickerOpen = false;
        }
    }
}
