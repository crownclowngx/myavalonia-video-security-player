using Avalonia.Headless.XUnit;
using System.Text.Json;
using Dock.Model.Mvvm.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Docking;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Constants;
using VideoSecurityPlayer.Plugin;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;
using Xunit;

namespace MyAvaloniaManagement.UiTests;

/// <summary>在 Headless Avalonia 中验收 VideoSecurityPlayer 四类 V3 Document 的真实 View 组合。</summary>
public sealed class VideoSecurityPlayerV3UiTests
{
    [AvaloniaFact]
    public async Task 四类Document通过HostAdapter创建View并绑定普通模型()
    {
        using var composition = VideoSecurityPlayerUiComposition.Create();
        var factory = composition.Provider.GetRequiredService<IHostDockableFactory>();

        await AssertDocumentAsync<SecretVideoPlayerViewModel, SecretVideoPlayerView>(
            factory, VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument, "播放器 UI");
        await AssertDocumentAsync<SecretVideoLibraryViewModel, SecretVideoLibraryView>(
            factory, VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument, "媒体库 UI");
        await AssertDocumentAsync<VideoEncryptorViewModel, VideoEncryptorView>(
            factory, VideoSecurityPlayerContributionIds.VideoEncryptorDocument, "加密 UI");
        await AssertDocumentAsync<VideoDecryptorViewModel, VideoDecryptorView>(
            factory, VideoSecurityPlayerContributionIds.VideoDecryptorDocument, "解密 UI");
    }

    [AvaloniaFact]
    public async Task 关闭Adapter会释放View并取消对应DocumentScope()
    {
        using var composition = VideoSecurityPlayerUiComposition.Create();
        var factory = composition.Provider.GetRequiredService<IHostDockableFactory>();
        var adapter = Assert.IsType<ManagedDocumentDockable>(
            await factory.CreateDocumentAsync(
                VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
                new NewDocumentActivation("关闭测试")));
        var view = Assert.IsType<SecretVideoPlayerView>(adapter.PreparedView);
        var model = Assert.IsType<SecretVideoPlayerViewModel>(adapter.Model);
        var closingToken = adapter.ClosingToken;

        adapter.Dispose();

        Assert.True(closingToken.IsCancellationRequested);
        Assert.Null(adapter.PreparedView);
        Assert.Empty(model.Password);
        Assert.Null(view.DataContext);
    }

    [AvaloniaFact]
    public async Task 四个非持久化Document全部显式拒绝Restore激活()
    {
        using var composition = VideoSecurityPlayerUiComposition.Create();
        var factory = composition.Provider.GetRequiredService<IHostDockableFactory>();
        using var json = JsonDocument.Parse("{}");
        var content = new DocumentContent(1, json.RootElement);

        foreach (var documentTypeId in new[]
                 {
                     VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
                     VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument,
                     VideoSecurityPlayerContributionIds.VideoEncryptorDocument,
                     VideoSecurityPlayerContributionIds.VideoDecryptorDocument,
                 })
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateDocumentAsync(
                documentTypeId,
                new RestoreDocumentActivation("错误恢复", content)).AsTask());
        }
    }

    private static async Task AssertDocumentAsync<TModel, TView>(
        IHostDockableFactory factory,
        DocumentTypeId id,
        string title)
        where TModel : class, IPluginDocument
        where TView : Avalonia.Controls.Control
    {
        using var adapter = Assert.IsType<ManagedDocumentDockable>(
            await factory.CreateDocumentAsync(id, new NewDocumentActivation(title)));
        var model = Assert.IsType<TModel>(adapter.Model);
        var view = Assert.IsType<TView>(adapter.PreparedView);

        Assert.Same(model, view.DataContext);
        Assert.False(model is Document);
        Assert.Equal(title, adapter.Title);
        Assert.False(adapter.CanFloat);
    }

    private sealed class VideoSecurityPlayerUiComposition : IDisposable
    {
        private readonly string _directory;
        private readonly HostDiagnosticSession _diagnostics;
        private readonly PluginProviderOwner _pluginProviders;
        private readonly DocumentScopeRegistry _documentScopes;
        private bool _disposed;

        private VideoSecurityPlayerUiComposition(
            string directory,
            HostDiagnosticSession diagnostics,
            ServiceProvider provider,
            PluginProviderOwner pluginProviders,
            DocumentScopeRegistry documentScopes)
        {
            _directory = directory;
            _diagnostics = diagnostics;
            Provider = provider;
            _pluginProviders = pluginProviders;
            _documentScopes = documentScopes;
        }

        internal ServiceProvider Provider { get; }

        internal static VideoSecurityPlayerUiComposition Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"videosecurityplayer-g11-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var diagnostics = HostDiagnosticSession.Start(directory);
            var registryBuilder = new PluginRegistryBuilder();
            var pluginProviders = new PluginProviderOwner();
            var documentScopes = new DocumentScopeRegistry();
            var services = new ServiceCollection();
            services.AddApplicationServices(registryBuilder, pluginProviders, documentScopes);
            services.AddViewModels();
            services.AddSingleton(diagnostics);
            services.AddSingleton<IHostDiagnosticSink>(diagnostics);
            services.AddSingleton(PluginModuleCatalog.CreateForTests(
            [
                (VideoSecurityPlayerContributionIds.Plugin,
                    (IPluginModule)new VideoSecurityPlayerPluginModule()),
            ]));
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            pluginProviders.Compose(
                provider.GetRequiredService<PluginModuleCatalog>(),
                provider,
                registryBuilder,
                documentScopes,
                diagnostics);
            _ = provider.GetRequiredService<PluginRegistry>();
            return new VideoSecurityPlayerUiComposition(
                directory, diagnostics, provider, pluginProviders, documentScopes);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _documentScopes.CloseAll();
            _pluginProviders.Dispose();
            Provider.Dispose();
            _diagnostics.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
