using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Workspace;
using MyAvaloniaManagement.PluginSdk;

namespace MyAvaloniaManagement.UiTests;

/// <summary>为 Headless UI 测试建立不含伪 Host 插件的最小只读目录。</summary>
internal static class UiWorkspaceCatalogFactory
{
    internal static readonly PluginId PluginOwner =
        new("myavalonia.plugin.g7-ui-tests");

    internal static WorkspaceCatalog Create(
        PluginRegistry registry,
        HostWorkspaceCatalog? hostCatalog = null)
    {
        var availability = new PluginAvailabilityReadModel(
            new PluginLifecycleStateStore(registry));
        return new WorkspaceCatalog(
            hostCatalog ?? new HostWorkspaceCatalog([], []),
            registry,
            availability);
    }
}
