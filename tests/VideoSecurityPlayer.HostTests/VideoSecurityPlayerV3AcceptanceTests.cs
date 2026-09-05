using Dock.Model.Mvvm.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Constants;
using VideoSecurityPlayer.Plugin;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;

namespace MyAvaloniaManagement.PluginTests;

/// <summary>通过真实 Host V3 组合链验收 VideoSecurityPlayer 的贡献、Scope 和关闭所有权。</summary>
public sealed class VideoSecurityPlayerV3AcceptanceTests
{
    [Fact]
    public void 模块一次声明四个非持久化Document和一个非破坏性加密Action()
    {
        using var composition = VideoSecurityPlayerComposition.Create();
        var plugin = Assert.Single(composition.Registry.Plugins);

        Assert.Equal(VideoSecurityPlayerContributionIds.Plugin.Value, plugin.Manifest.PluginId.Value);
        Assert.Equal(4, plugin.DocumentTypes.Count);
        Assert.Empty(plugin.ToolTypes);
        Assert.Empty(composition.Registry.Lifecycles);
        Assert.Empty(composition.Registry.WorkflowActionConsumerIds);

        var action = Assert.Single(composition.Registry.WorkflowActions);
        Assert.Equal(VideoSecurityPlayerContributionIds.Plugin, action.OwnerId);
        Assert.Equal(
            "myavalonia.plugin.my-small-tools.workflow.encrypt-video",
            action.Descriptor.Id.Value);
        Assert.Equal("EncryptVideoWorkflowActionHandler", action.HandlerType.Name);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun,
            action.Descriptor.ConfirmationPolicy);
        Assert.Equal(["/password"], action.Descriptor.SensitiveInputPointers);

        AssertDocument<SecretVideoPlayerViewModel, SecretVideoPlayerView>(
            composition.Registry,
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
            "加密视频播放器",
            "支持 SECVID03/AES-256-GCM 认证分块和随机读取的加密视频播放器");
        AssertDocument<SecretVideoLibraryViewModel, SecretVideoLibraryView>(
            composition.Registry,
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument,
            "加密视频库播放器",
            "扫描文件夹中的 SECVID03 视频，支持公开信息搜索和公共密码播放");
        AssertDocument<VideoEncryptorViewModel, VideoEncryptorView>(
            composition.Registry,
            VideoSecurityPlayerContributionIds.VideoEncryptorDocument,
            "视频文件加密器",
            "使用 SECVID03/AES-256-GCM 分块加密视频，支持标题、描述和随机读取播放");
        AssertDocument<VideoDecryptorViewModel, VideoDecryptorView>(
            composition.Registry,
            VideoSecurityPlayerContributionIds.VideoDecryptorDocument,
            "批量视频解密器",
            "使用一个公共密码批量解密 SECVID03 视频，并安全导出原始文件");
    }

    [Fact]
    public async Task 四类Document使用普通模型并支持默认与自定义标题()
    {
        using var composition = VideoSecurityPlayerComposition.Create();
        var activator = composition.HostProvider.GetRequiredService<PluginContributionActivator>();
        var cases = new (DocumentTypeId Id, string DefaultTitle, string CustomTitle)[]
        {
            (VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument, "加密视频播放器", "播放器 A"),
            (VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument, "加密视频库播放器", "媒体库 A"),
            (VideoSecurityPlayerContributionIds.VideoEncryptorDocument, "视频文件加密器", "加密任务 A"),
            (VideoSecurityPlayerContributionIds.VideoDecryptorDocument, "批量视频解密器", "解密任务 A"),
        };

        foreach (var item in cases)
        {
            using var defaultActivation = activator.ActivateDocument(item.Id);
            using var customActivation = activator.ActivateDocument(item.Id);
            Assert.IsAssignableFrom<IPluginDocument>(defaultActivation.Model);
            Assert.False(defaultActivation.Model is Document);

            await defaultActivation.Model.InitializeAsync(
                new NewDocumentActivation(string.Empty), default);
            await customActivation.Model.InitializeAsync(
                new NewDocumentActivation(item.CustomTitle), default);

            Assert.Equal(item.DefaultTitle, defaultActivation.Model.Presentation.Title);
            Assert.Equal(item.CustomTitle, customActivation.Model.Presentation.Title);
        }
    }

    [Fact]
    public void 同类Document隔离敏感状态且关闭信号只作用于自身Scope()
    {
        using var composition = VideoSecurityPlayerComposition.Create();
        var activator = composition.HostProvider.GetRequiredService<PluginContributionActivator>();
        var firstActivation = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument);
        using var secondActivation = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument);
        var first = Assert.IsType<SecretVideoPlayerViewModel>(firstActivation.Model);
        var second = Assert.IsType<SecretVideoPlayerViewModel>(secondActivation.Model);
        first.Password = "player-a";
        second.Password = "player-b";

        Assert.NotSame(first.PlayerViewModel, second.PlayerViewModel);
        Assert.NotSame(first.Source, second.Source);
        Assert.False(firstActivation.ClosingToken.IsCancellationRequested);
        Assert.False(secondActivation.ClosingToken.IsCancellationRequested);

        firstActivation.Dispose();

        Assert.True(firstActivation.ClosingToken.IsCancellationRequested);
        Assert.False(secondActivation.ClosingToken.IsCancellationRequested);
        Assert.Empty(first.Password);
        Assert.Equal("player-b", second.Password);
    }

    [Fact]
    public void 加解密和媒体库状态均由独立DocumentScope拥有()
    {
        using var composition = VideoSecurityPlayerComposition.Create();
        var activator = composition.HostProvider.GetRequiredService<PluginContributionActivator>();
        using var encryptorA = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.VideoEncryptorDocument);
        using var encryptorB = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.VideoEncryptorDocument);
        using var decryptorA = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.VideoDecryptorDocument);
        using var decryptorB = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.VideoDecryptorDocument);
        using var libraryA = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument);
        using var libraryB = activator.ActivateDocument(
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument);

        var encA = Assert.IsType<VideoEncryptorViewModel>(encryptorA.Model);
        var encB = Assert.IsType<VideoEncryptorViewModel>(encryptorB.Model);
        var decA = Assert.IsType<VideoDecryptorViewModel>(decryptorA.Model);
        var decB = Assert.IsType<VideoDecryptorViewModel>(decryptorB.Model);
        var libA = Assert.IsType<SecretVideoLibraryViewModel>(libraryA.Model);
        var libB = Assert.IsType<SecretVideoLibraryViewModel>(libraryB.Model);

        encA.Password = "enc-a";
        encB.Password = "enc-b";
        decA.Password = "dec-a";
        decB.Password = "dec-b";
        libA.Password = "lib-a";
        libB.Password = "lib-b";

        Assert.NotSame(encA.Queue, encB.Queue);
        Assert.NotSame(decA.Queue, decB.Queue);
        Assert.NotSame(libA.Browser, libB.Browser);
        Assert.NotSame(libA.PlayerViewModel, libB.PlayerViewModel);
        Assert.Equal("enc-a", encA.Password);
        Assert.Equal("enc-b", encB.Password);
        Assert.Equal("dec-a", decA.Password);
        Assert.Equal("dec-b", decB.Password);
        Assert.Equal("lib-a", libA.Password);
        Assert.Equal("lib-b", libB.Password);
    }

    [Fact]
    public void 生产程序集不再引用LegacyDockCommonNewtonsoft或Host实现()
    {
        var references = typeof(VideoSecurityPlayerPluginModule).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MyAvaloniaManagement.PluginSdk", references);
        Assert.Contains("MyAvaloniaManagement.PluginSdk.UI", references);
        Assert.DoesNotContain("MyAvaloniaManagementCommon", references);
        Assert.DoesNotContain("Dock.Model", references);
        Assert.DoesNotContain("Dock.Model.Mvvm", references);
        Assert.DoesNotContain("Newtonsoft.Json", references);
        Assert.DoesNotContain("MyAvaloniaManagement", references);
    }

    private static void AssertDocument<TDocument, TView>(
        PluginRegistry registry,
        DocumentTypeId id,
        string displayName,
        string description)
    {
        Assert.True(registry.TryGetDocumentRegistration(id, out var registration));
        Assert.Equal(typeof(TDocument), registration.ModelType);
        Assert.Equal(typeof(TView), registration.ViewType);
        Assert.Equal(displayName, registration.Descriptor.DisplayName);
        Assert.Equal(description, registration.Descriptor.Description);
        Assert.Equal("视频工具", registration.Descriptor.MenuCategory);
        Assert.False(registration.IsPersistable);
    }

    private sealed class VideoSecurityPlayerComposition : IDisposable
    {
        private readonly string _directory;
        private readonly HostDiagnosticSession _diagnostics;
        private readonly PluginProviderOwner _pluginProviders;
        private readonly DocumentScopeRegistry _documentScopes;
        private bool _disposed;

        private VideoSecurityPlayerComposition(
            string directory,
            HostDiagnosticSession diagnostics,
            ServiceProvider hostProvider,
            PluginProviderOwner pluginProviders,
            DocumentScopeRegistry documentScopes,
            PluginRegistry registry)
        {
            _directory = directory;
            _diagnostics = diagnostics;
            HostProvider = hostProvider;
            _pluginProviders = pluginProviders;
            _documentScopes = documentScopes;
            Registry = registry;
        }

        internal ServiceProvider HostProvider { get; }
        internal PluginRegistry Registry { get; }

        internal static VideoSecurityPlayerComposition Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"videosecurityplayer-g11-{Guid.NewGuid():N}");
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
            return new VideoSecurityPlayerComposition(
                directory,
                diagnostics,
                provider,
                pluginProviders,
                documentScopes,
                provider.GetRequiredService<PluginRegistry>());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _documentScopes.CloseAll();
            _pluginProviders.Dispose();
            HostProvider.Dispose();
            _diagnostics.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
