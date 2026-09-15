using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Encryption;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer.Encryption;

/// <summary>加密队列视图；只在 View 边界访问窗口级文件选择器。</summary>
public partial class EncryptionQueueView : UserControl
{
    private bool _isFilePickerOpen;

    public EncryptionQueueView()
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
        e.DragEffects = DataContext is EncryptionQueueViewModel queue && !queue.Owner.IsBusy &&
            e.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not EncryptionQueueViewModel queue) return;
        var paths = e.DataTransfer.TryGetFiles()?.Where(file => file.Path.IsFile)
            .Select(file => file.Path.LocalPath).ToArray() ?? [];
        await queue.Owner.Input.ImportAsync(paths);
    }

    private async void OnAddDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFilePickerOpen || DataContext is not EncryptionQueueViewModel queue || queue.Owner.IsBusy) return;
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
        if (sender is Button { Tag: string path } && DataContext is EncryptionQueueViewModel queue)
            queue.Owner.StatusMessage = await QueueOutputActions.RevealAsync(this, path);
    }

    private async void OnCopyOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && DataContext is EncryptionQueueViewModel queue)
            queue.Owner.StatusMessage = await QueueOutputActions.CopyPathAsync(this, path);
    }

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
                await queue.Owner.Input.ImportAsync(files.Where(file => file.Path.IsFile)
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
    private void OnApplySelectedOutputClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EncryptionQueueViewModel queue)
            queue.Owner.ApplyOutput(QueueList.SelectedItems?.OfType<VideoSecurityPlayer.ViewModels.SecretVideoPlayer.EncryptionQueueItemViewModel>() ?? []);
    }
    private void OnApplyAllOutputClick(object? sender, RoutedEventArgs e)
    { if (DataContext is EncryptionQueueViewModel queue) queue.Owner.ApplyOutput(queue.Owner.Items); }
    private void OnApplyDescriptionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EncryptionQueueViewModel queue)
            queue.Owner.ApplyDescription(QueueList.SelectedItems?.OfType<VideoSecurityPlayer.ViewModels.SecretVideoPlayer.EncryptionQueueItemViewModel>() ?? []);
    }
    private async void OnChooseOutputDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFilePickerOpen || DataContext is not EncryptionQueueViewModel queue || queue.Owner.IsBusy) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _isFilePickerOpen = true;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择统一输出目录" });
            if (ReferenceEquals(DataContext, queue) && !queue.Owner.IsBusy && !queue.Owner.IsClosing && folders.Count > 0)
                queue.Owner.UnifiedOutputDirectory = folders[0].Path.LocalPath;
        }
        catch { if (ReferenceEquals(DataContext, queue)) queue.Owner.StatusMessage = "选择目录失败，请重试"; }
        finally { _isFilePickerOpen = false; }
    }
    private async void OnOpenOutputDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EncryptionQueueViewModel queue)
            queue.Owner.StatusMessage = await QueueOutputActions.OpenDirectoryAsync(this, queue.Owner.UnifiedOutputDirectory);
    }
}
