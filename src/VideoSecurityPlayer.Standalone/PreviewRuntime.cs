using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Plugin;

namespace VideoSecurityPlayer.Standalone;

internal sealed class PreviewDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    private bool _disposed;
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    public void Close() { if (!_closing.IsCancellationRequested) _closing.Cancel(); }
    public void Dispose() { if (_disposed) return; Close(); _disposed = true; _closing.Dispose(); }
}

internal sealed class PreviewDocument(AsyncServiceScope scope, PreviewDocumentLifetime lifetime, IPluginDocument model) : IAsyncDisposable
{
    private Task? _closing;
    public IPluginDocument Model { get; } = model;
    public Control? View { get; internal set; }
    public CancellationToken ClosingToken { get; } = lifetime.ClosingToken;
    public ValueTask DisposeAsync() => new(_closing ??= CloseAsync());
    private async Task CloseAsync()
    {
        lifetime.Close();
        if (View is not null) View.DataContext = null;
        View = null;
        await scope.DisposeAsync();
    }
}

/// <summary>窗口拥有一个插件 Provider，每次打开拥有独立 Scope。只替换开发期存储位置。</summary>
internal sealed class PreviewRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<PreviewDocument> _documents = [];
    private bool _disposed;
    public PreviewRegistration Registration { get; } = new();
    public PreviewRuntime(string? dataRoot = null, Action<IServiceCollection>? configure = null, IPluginModule? module = null)
    {
        (module ?? new VideoSecurityPlayerPluginModule()).Configure(Registration);
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoSecurityPlayer", "Standalone");
        Registration.Services.Replace(ServiceDescriptor.Singleton<SecretVideoUserDataStore>(_ => new SecretVideoUserDataStore(Path.Combine(root, "user-data-v1.json"))));
        Registration.Services.AddScoped<PreviewDocumentLifetime>();
        Registration.Services.AddScoped<IDocumentLifetime>(p => p.GetRequiredService<PreviewDocumentLifetime>());
        configure?.Invoke(Registration.Services);
        _provider = Registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    public async Task<PreviewDocument> OpenAsync(PreviewContribution contribution, bool createView = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scope = _provider.CreateAsyncScope();
        PreviewDocument? document = null;
        try
        {
            var lifetime = scope.ServiceProvider.GetRequiredService<PreviewDocumentLifetime>();
            var model = (IPluginDocument)scope.ServiceProvider.GetRequiredService(contribution.ModelType);
            document = new PreviewDocument(scope, lifetime, model);
            await model.InitializeAsync(new NewDocumentActivation(contribution.Descriptor.DisplayName), lifetime.ClosingToken);
            if (createView)
            {
                document.View = contribution.CreateView();
                document.View.DataContext = model;
            }
            _documents.Add(document);
            return document;
        }
        catch
        {
            if (document is not null) await document.DisposeAsync();
            else await scope.DisposeAsync();
            throw;
        }
    }
    public async Task CloseAsync(PreviewDocument document)
    {
        _documents.Remove(document);
        await document.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            List<Exception> failures = [];
            foreach (var document in _documents.ToArray())
                try { await CloseAsync(document); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { await _provider.DisposeAsync(); }
    }
}
