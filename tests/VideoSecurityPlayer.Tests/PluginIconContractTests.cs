using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using Xunit;

namespace PluginIconMigration.Tests;

/// <summary>执行真实 Module，验证全部入口有可解析图标且模块确实使用注册返回值。</summary>
public sealed class PluginIconContractTests
{
    [Fact]
    public void 所有文档工具和创建意图均可解析图标且保持入口数量()
    {
        var registration = new Capture("first");
        new VideoSecurityPlayer.Plugin.VideoSecurityPlayerPluginModule().Configure(registration);
        Assert.Equal(4, registration.Documents.Count);
        Assert.Empty(registration.Tools);
        Assert.Equal(0, registration.Documents.Sum(d => d.CreationIntents.Count));
        Assert.Equal(3, registration.Icons.Count);
        Assert.All(registration.Documents, document =>
        {
            registration.AssertResolvable(document.IconPath);
            foreach (var intent in document.CreationIntents)
                registration.AssertResolvable(string.IsNullOrWhiteSpace(intent.IconPath) ? document.IconPath : intent.IconPath);
        });
        Assert.All(registration.Tools, tool => registration.AssertResolvable(tool.IconPath));
        Assert.All(registration.Icons.Values, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.PathData));
            Assert.InRange(definition.ViewBoxWidth, 1, 64);
            Assert.InRange(definition.ViewBoxHeight, 1, 64);
            Assert.Equal(IconFillRule.EvenOdd, definition.FillRule);
        });
    }

    [Fact]
    public void 重复组合使用当前注册入口返回的引用且不持有上次的注册状态()
    {
        var module = new VideoSecurityPlayer.Plugin.VideoSecurityPlayerPluginModule();
        var first = new Capture("first");
        var second = new Capture("second");
        module.Configure(first);
        module.Configure(second);
        Assert.Equal(first.Icons.Count, second.Icons.Count);
        Assert.Equal(first.Documents.Select(d => d.DocumentTypeId), second.Documents.Select(d => d.DocumentTypeId));
        foreach (var document in second.Documents)
        {
            second.AssertResolvable(document.IconPath);
            foreach (var intent in document.CreationIntents)
                second.AssertResolvable(string.IsNullOrWhiteSpace(intent.IconPath) ? document.IconPath : intent.IconPath);
        }
        Assert.All(second.Tools, tool => second.AssertResolvable(tool.IconPath));
    }

    // 故意返回不符合生产协议的哨兵引用；如果模块手写 plugin: 或缓存上次返回值，断言会失败。
    private sealed class Capture(string run) : IPluginRegistration, IPluginIconRegistration,
        IWorkflowActionRegistration, IWorkbenchCommandRegistration
    {
        private static readonly HashSet<string> PublicKeys = new(["builtin:module", "builtin:folder", "builtin:table", "builtin:chart", "builtin:text-check", "builtin:image", "builtin:video", "builtin:download"], StringComparer.Ordinal);
        private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
        public PluginId PluginId => new("myavalonia.plugin.my-small-tools");
        public IServiceCollection Services { get; } = new ServiceCollection();
        public List<DocumentDescriptor> Documents { get; } = [];
        public List<ToolDescriptor> Tools { get; } = [];
        public Dictionary<string, VectorIconDefinition> Icons { get; } = new(StringComparer.Ordinal);
        public string AddIcon(string localName, VectorIconDefinition definition)
        {
            Assert.Matches(@"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z", localName);
            Assert.True(_localNames.Add(localName), $"重复图标声明：{localName}");
            var reference = $"capture:{run}/{localName}";
            Icons.Add(reference, definition);
            return reference;
        }
        public void AssertResolvable(string reference) => Assert.True(
            PublicKeys.Contains(reference) || Icons.ContainsKey(reference), $"图标引用无法解析：{reference}");
        public void AddDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPluginDocument where V : Control, new() => Documents.Add(descriptor);
        public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPersistablePluginDocument where V : Control, new() => Documents.Add(descriptor);
        public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new() => Tools.Add(descriptor);
        public void UseLifecycle<T>() where T : class, IPluginLifecycle { }
        public void AddWorkflowAction<T>(WorkflowActionDescriptor descriptor) where T : class, IWorkflowActionHandler { }
        public void UseWorkflowActionGateway() { }
        public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) { }
        public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) { }
        public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) { }
    }
}
