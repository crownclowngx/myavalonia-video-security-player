using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Workspace;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;


namespace MyAvaloniaManagement.PluginTests;

public sealed class VideoSecurityPlayerPackageTests
{
    [Fact]
    public void G11最终测试Zip通过真实V3发现组合并进入Workspace目录()
    {
        var packageRoot = Environment.GetEnvironmentVariable(
            "MYAVALONIA_G11_V3_PACKAGE_ROOT");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            // 普通回归不重复构建大型 LibVLC 包；G11 专项脚本负责设置目录并执行本测试。
            return;
        }

        var snapshot = AssemblyLoaderHelper.Discover(Path.GetFullPath(packageRoot));
        Assert.Empty(snapshot.Diagnostics);
        var assembly = Assert.Single(snapshot.Assemblies);
        Assert.Equal("VideoSecurityPlayer.Plugin", assembly.GetName().Name);
        var catalog = PluginModuleCatalog.Discover(snapshot);
        var diagnosticsRoot = Path.Combine(
            Path.GetTempPath(), $"videosecurityplayer-g11-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(diagnosticsRoot);
        using var diagnostics = HostDiagnosticSession.Start(diagnosticsRoot);
        var registryBuilder = new PluginRegistryBuilder();
        using var pluginProviders = new PluginProviderOwner();
        var documentScopes = new DocumentScopeRegistry();
        var services = new ServiceCollection();
        services.AddApplicationServices(registryBuilder, pluginProviders, documentScopes);
        services.AddViewModels();
        services.AddSingleton(diagnostics);
        services.AddSingleton<IHostDiagnosticSink>(diagnostics);
        services.AddSingleton(catalog);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        try
        {
            pluginProviders.Compose(
                catalog, provider, registryBuilder, documentScopes, diagnostics);
            var registry = provider.GetRequiredService<PluginRegistry>();
            var plugin = Assert.Single(registry.Plugins);
            Assert.Equal("myavalonia.plugin.my-small-tools", plugin.Manifest.PluginId.Value);
            Assert.Equal(4, plugin.DocumentTypes.Count);
            Assert.Empty(plugin.ToolTypes);
            Assert.All(plugin.DocumentTypes, modelType =>
                Assert.Equal("VideoSecurityPlayer.Plugin", modelType.Assembly.GetName().Name));
            var workspace = provider.GetRequiredService<WorkspaceSession>();
            Assert.Equal(4, workspace.GetAllDocumentCreationEntries().Count(entry =>
                entry.DocumentTypeId.Value.StartsWith(
                    "myavalonia.plugin.my-small-tools.document.",
                    StringComparison.Ordinal)));
        }
        finally
        {
            documentScopes.CloseAll();
            diagnostics.Dispose();
            Directory.Delete(diagnosticsRoot, recursive: true);
        }
    }

}
