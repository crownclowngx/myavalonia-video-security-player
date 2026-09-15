using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Decryption;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Decryption;

/// <summary>解密候选队列视图；文件选择结果只回写发起请求的 Document。</summary>
public partial class DecryptionQueueView : UserControl
{
    private bool _isFilePickerOpen;

    public DecryptionQueueView()
    {
        InitializeComponent();
        // 常规状态把剩余高度留给列表；展开配置后最少保留 140 像素，并由外层滚动保证表单可达。
        // 尺寸只归 View 管理，不把窗口像素引入批次业务模型。
        LayoutUpdated += (_, _) =>
        {
            var available = Math.Max(140, Bounds.Height - QueueLayout.RowDefinitions.Take(4).Sum(row => row.ActualHeight) - 8);
            if (Math.Abs(QueueArea.Height - available) > 1) QueueArea.Height = available;
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DataContext is DecryptionQueueViewModel queue && !queue.Owner.IsBusy &&
            e.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not DecryptionQueueViewModel queue) return;
        var paths = e.DataTransfer.TryGetFiles()?.Where(file => file.Path.IsFile)
            .Select(file => file.Path.LocalPath).ToArray() ?? [];
        await queue.Owner.Input.ImportAsync(paths);
    }

    private async void OnAddDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFilePickerOpen || DataContext is not DecryptionQueueViewModel queue || queue.Owner.IsBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        _isFilePickerOpen = true;
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "添加视频目录", AllowMultiple = true });
            if (ReferenceEquals(DataContext, queue))
                await queue.Owner.Input.ImportAsync(folders.Where(folder => folder.Path.IsFile)
                    .Select(folder => folder.Path.LocalPath).ToArray());
        }
        catch { if (ReferenceEquals(DataContext, queue)) queue.Owner.StatusMessage = "选择目录失败，请重试"; }
        finally { _isFilePickerOpen = false; }
    }

    private async void OnLocateOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && DataContext is DecryptionQueueViewModel queue)
            queue.Owner.StatusMessage = await QueueOutputActions.RevealAsync(this, path);
    }

    private async void OnCopyOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && DataContext is DecryptionQueueViewModel queue)
            queue.Owner.StatusMessage = await QueueOutputActions.CopyPathAsync(this, path);
    }

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
                await queue.Owner.Input.ImportAsync(files.Select(file => file.Path.LocalPath).ToArray());
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
