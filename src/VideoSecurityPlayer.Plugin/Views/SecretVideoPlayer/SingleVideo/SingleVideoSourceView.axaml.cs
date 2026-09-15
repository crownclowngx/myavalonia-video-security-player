using Avalonia.Input;
using Avalonia.Input.Platform;
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
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DataContext is SingleVideoSourceViewModel source && !source.IsBusy &&
                e.DataTransfer.TryGetFiles()?.Count() == 1 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
        if (files.Length == 1 && files[0].Path.IsFile) await SelectPathAsync(files[0].Path.LocalPath);
    }

    private async void OnPastePathClick(object? sender, RoutedEventArgs e)
    {
        var source = DataContext as SingleVideoSourceViewModel;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            var path = (await clipboard.TryGetTextAsync())?.Trim().Trim('"') ?? "";
            if (ReferenceEquals(DataContext, source)) await SelectPathAsync(path);
        }
        catch { if (ReferenceEquals(DataContext, source) && source is not null) source.StatusMessage = "读取剪贴板失败，请使用浏览选择文件"; }
    }

    private async Task SelectPathAsync(string path)
    {
        if (DataContext is not SingleVideoSourceViewModel source || source.IsBusy || source.IsClosing) return;
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".secvid", StringComparison.OrdinalIgnoreCase))
        { source.StatusMessage = "请选择一个存在的 .secvid 文件"; return; }
        try { await source.SelectFileAsync(Path.GetFullPath(path)); }
        catch { if (ReferenceEquals(DataContext, source)) source.StatusMessage = "打开文件失败，请检查路径后重试"; }
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_isPickerOpen || DataContext is not SingleVideoSourceViewModel source || source.IsSavingPublicInfo)
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
        catch { if (ReferenceEquals(DataContext, source)) source.StatusMessage = "选择文件失败，请检查路径后重试"; }
        finally
        {
            _isPickerOpen = false;
        }
    }
}
