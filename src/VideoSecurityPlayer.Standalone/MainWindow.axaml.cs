using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using MyAvaloniaManagement.PluginSdk.UI;

namespace VideoSecurityPlayer.Standalone;

public sealed partial class MainWindow : Window, IWindowContentFullscreenHost
{
    private readonly PreviewRuntime _runtime;
    private readonly ObservableCollection<TabItem> _tabs = [];
    private FullscreenLease? _fullscreen;
    private bool _closing;
    private bool _closed;
    public MainWindow() : this(new PreviewRuntime()) { }
    internal MainWindow(PreviewRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;
        FeatureSelector.ItemsSource = runtime.Registration.Documents;
        FeatureSelector.SelectedIndex = 0;
        Documents.ItemsSource = _tabs;
    }
    private async void OpenFeature(object? sender, RoutedEventArgs args)
    {
        if (_closing || FeatureSelector.SelectedItem is not PreviewContribution contribution) return;
        OpenButton.IsEnabled = false;
        try { await OpenDocumentAsync(contribution); }
        catch (Exception error) { Status.Text = $"打开失败：{error.Message}"; }
        finally { OpenButton.IsEnabled = !_closing; }
    }
    internal async Task<TabItem> OpenDocumentAsync(PreviewContribution contribution)
    {
        var document = await _runtime.OpenAsync(contribution);
        var tab = new TabItem { Content = document.View, Tag = document };
        var close = new Button { Content = "×", Padding = new Avalonia.Thickness(7, 1) };
        close.Click += async (_, _) =>
        {
            try { await CloseDocumentAsync(tab); }
            catch (Exception error) { Status.Text = $"关闭失败：{error.Message}"; }
        };
        tab.Header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = document.Model.Presentation.Title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, close } };
        _tabs.Add(tab);
        Documents.SelectedItem = tab;
        return tab;
    }
    internal async Task CloseDocumentAsync(TabItem tab)
    {
        if (tab.Tag is not PreviewDocument document) return;
        _fullscreen?.Dispose();
        tab.Content = null;
        tab.Tag = null;
        _tabs.Remove(tab);
        await _runtime.CloseAsync(document);
    }
    protected override async void OnClosing(WindowClosingEventArgs args)
    {
        if (_closed) { base.OnClosing(args); return; }
        args.Cancel = true;
        base.OnClosing(args);
        if (_closing) return;
        _closing = true;
        OpenButton.IsEnabled = false;
        try
        {
            _fullscreen?.Dispose();
            foreach (var tab in _tabs) tab.Content = null;
            _tabs.Clear();
            await _runtime.DisposeAsync();
            _closed = true;
            Close();
        }
        catch (Exception error)
        {
            Status.Text = $"退出清理失败：{error.Message}";
            _closed = true;
            // 保留错误供调试；再次关闭可以退出。
        }
    }
    public IDisposable? TryPresent(Control content)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(content);
        if (_fullscreen is not null || _closing || FullscreenContent.Content is not null) return null;
        try
        {
            FullscreenContent.Content = content;
            FullscreenLayer.IsVisible = true;
            return _fullscreen = new FullscreenLease(this);
        }
        catch
        {
            FullscreenContent.Content = null;
            FullscreenLayer.IsVisible = false;
            throw;
        }
    }
    private sealed class FullscreenLease(MainWindow owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            Dispatcher.UIThread.VerifyAccess();
            _disposed = true;
            if (!ReferenceEquals(owner._fullscreen, this)) return;
            owner._fullscreen = null;
            try { owner.FullscreenContent.Content = null; }
            finally { owner.FullscreenLayer.IsVisible = false; }
        }
    }
}
