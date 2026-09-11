using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Constants;

namespace VideoSecurityPlayer.Standalone;

internal sealed record PreviewContribution(DocumentDescriptor Descriptor, Type ModelType, Func<Control> CreateView)
{
    public override string ToString() => Descriptor.DisplayName;
}

/// <summary>只消费真实 Module 的声明；开发窗口不复制业务注册清单。</summary>
internal sealed class PreviewRegistration : IPluginRegistration, IPluginIconRegistration, IWorkflowActionRegistration
{
    // V6.1：预览/测试只保留本次组合的纯图标数据；不使用 Host 的全局注册表或缓存。
    // 对重复名称和非法名称直接报错，避免预览吞掉正式 Host 会拒绝的声明。
    private readonly Dictionary<string, VectorIconDefinition> _previewIcons = new(StringComparer.Ordinal);
    public string AddIcon(string localName, VectorIconDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (localName is null || !System.Text.RegularExpressions.Regex.IsMatch(localName, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"))
            throw new ArgumentException("图标名称必须使用小写字母、数字及单个连字符分段。", nameof(localName));
        var reference = $"plugin:{PluginId.Value}/{localName}";
        _previewIcons.Add(reference, definition);
        return reference;
    }


    public PluginId PluginId => VideoSecurityPlayerContributionIds.Plugin;
    public IServiceCollection Services { get; } = new ServiceCollection();
    public List<PreviewContribution> Documents { get; } = [];
    public List<WorkflowActionDescriptor> Actions { get; } = [];
    public void AddDocument<T, V>(DocumentDescriptor descriptor)
        where T : class, IPluginDocument where V : Control, new()
    {
        if (Documents.Any(item => item.Descriptor.DocumentTypeId == descriptor.DocumentTypeId))
            throw new InvalidOperationException("重复文档身份。");
        Documents.Add(new(descriptor, typeof(T), static () => new V()));
        Services.AddScoped<T>();
    }
    public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor)
        where T : class, IPersistablePluginDocument where V : Control, new() => AddDocument<T, V>(descriptor);
    public void AddWorkflowAction<T>(WorkflowActionDescriptor descriptor) where T : class, IWorkflowActionHandler
    {
        Actions.Add(descriptor);
        Services.AddScoped<T>();
    }
    public void UseWorkflowActionGateway() => throw new NotSupportedException("独立预览不提供工作流 Gateway。");
    public void UseLifecycle<T>() where T : class, IPluginLifecycle => throw new NotSupportedException("当前视频插件未声明插件生命周期。");
    public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new() =>
        throw new NotSupportedException("当前视频插件只使用 Document。");
}
