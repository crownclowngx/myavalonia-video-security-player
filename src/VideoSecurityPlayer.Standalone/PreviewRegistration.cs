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
internal sealed class PreviewRegistration : IPluginRegistration, IWorkflowActionRegistration
{
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
