using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>目录/导出选择器属于视图边界，业务检测和持久化由模型端口完成。</summary>
public sealed partial class VideoSettingsView : UserControl
{
    private CancellationTokenSource _viewLifetime = new();
    public VideoSettingsView() => InitializeComponent();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewLifetime.IsCancellationRequested) _viewLifetime = new();
        base.OnAttachedToVisualTree(e);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewLifetime.Cancel();
        // 隐藏/换 View 仅取消本视图的文件操作，不释放单例设置草稿。
        base.OnDetachedFromVisualTree(e);
    }
    private async void BrowseDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoSettingsTool model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var token = _viewLifetime.Token;
        try
        {
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 LibVLC 目录", AllowMultiple = false });
            if (!token.IsCancellationRequested && ReferenceEquals(DataContext, model) && result.FirstOrDefault()?.TryGetLocalPath() is { } path)
            { model.Runtime.Automatic = false; model.Runtime.DirectoryPath = path; }
        }
        catch { if (!token.IsCancellationRequested && ReferenceEquals(DataContext, model)) model.Runtime.CheckMessage = "无法打开目录选择器，请直接输入绝对目录。"; }
    }
    private async void ExportEnvironment(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoSettingsTool model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var token = _viewLifetime.Token;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = "导出脱敏环境诊断", SuggestedFileName = "video-environment.json", DefaultExtension = "json" });
            if (file is null || token.IsCancellationRequested || !ReferenceEquals(DataContext, model)) return;
            var bytes = await model.Runtime.CreateDiagnosticAsync(token);
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            if (ReferenceEquals(DataContext, model)) model.Runtime.ReportExport(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!token.IsCancellationRequested) model.Runtime.ReportExport(false); }
    }
}
