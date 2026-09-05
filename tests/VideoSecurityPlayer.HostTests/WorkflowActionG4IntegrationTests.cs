using System.Collections;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Plugins.Discovery;
using MyAvaloniaManagement.Business.WorkflowActions;
using MyAvaloniaManagement.PluginSdk;

namespace MyAvaloniaManagement.PluginTests;

/// <summary>
/// 使用 VideoSecurityPlayer 与外部 Workflow Studio 的真实 ZIP 验证 G4 手工工作流闭环。
/// </summary>
/// <remarks>
/// 普通测试运行没有实体 ZIP 时直接返回，G4 聚合门禁会设置两个环境变量并要求完整执行。
/// 反射只存在于测试适配器，用来驱动外部插件已经公开给 UI 的属性和命令；生产 Host 不会据此
/// 暴露任意插件方法，也不会引用 Studio 或 VideoSecurityPlayer 的私有类型。
/// </remarks>
public sealed class WorkflowActionG4IntegrationTests
{
    private const string PackageRootVariable = "MYAVALONIA_WORKFLOW_G4_PLUGIN_ROOT";
    private const string MediaPathVariable = "MYAVALONIA_WORKFLOW_G4_MEDIA_PATH";
    private const string ActionId = "myavalonia.plugin.my-small-tools.workflow.encrypt-video";
    private const string StudioDocumentId = "myavalonia.plugin.workflow-studio.document.studio";
    private const string SecretCanary = "G4-INTEGRATION-SECRET-MUST-NOT-LEAK";

    [Fact]
    public async Task 两个真实Zip通过Studio手工路径加密且重复运行安全失败()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(PackageRootVariable);
        var configuredMedia = Environment.GetEnvironmentVariable(MediaPathVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot) || string.IsNullOrWhiteSpace(configuredMedia))
        {
            // 实体 ZIP 由专项脚本在隔离目录中生成；普通回归不复制外部仓库或伪造包内容。
            return;
        }

        var pluginRoot = Path.GetFullPath(configuredRoot);
        var mediaPath = Path.GetFullPath(configuredMedia);
        Assert.True(File.Exists(mediaPath), $"G4 测试媒体不存在：{mediaPath}");
        var snapshot = AssemblyLoaderHelper.Discover(pluginRoot);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(2, snapshot.Assemblies.Count);

        var providerAssembly = snapshot.Assemblies.Single(assembly =>
            assembly.GetName().Name == "VideoSecurityPlayer.Plugin");
        var studioAssembly = snapshot.Assemblies.Single(assembly =>
            assembly.GetName().Name == "WorkflowStudio.Plugin");
        Assert.DoesNotContain(studioAssembly.GetReferencedAssemblies(), reference =>
            reference.Name == providerAssembly.GetName().Name);
        Assert.NotSame(
            AssemblyLoadContext.GetLoadContext(providerAssembly),
            AssemblyLoadContext.GetLoadContext(studioAssembly));
        Assert.NotSame(
            AssemblyLoadContext.Default,
            AssemblyLoadContext.GetLoadContext(providerAssembly));
        Assert.Same(
            AssemblyLoadContext.Default,
            AssemblyLoadContext.GetLoadContext(typeof(IWorkflowActionGateway).Assembly));

        var workRoot = Path.Combine(Path.GetTempPath(), $"workflow-action-g4-e2e-{Guid.NewGuid():N}");
        var diagnosticsRoot = Path.Combine(workRoot, "diagnostics");
        Directory.CreateDirectory(diagnosticsRoot);
        var sourcePath = Path.Combine(workRoot, "source.mp4");
        var outputPath = Path.Combine(workRoot, "output.secvid");
        File.Copy(mediaPath, sourcePath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));

        using var diagnostics = HostDiagnosticSession.Start(diagnosticsRoot);
        var registryBuilder = new PluginRegistryBuilder();
        using var pluginProviders = new PluginProviderOwner();
        var documentScopes = new DocumentScopeRegistry();
        var services = new ServiceCollection();
        services.AddApplicationServices(registryBuilder, pluginProviders, documentScopes);
        // 测试只替换 Host internal 授权端口，避免无窗口环境弹出模态窗口；生产注册不变。
        services.AddSingleton<IWorkflowActionAuthorizer, AllowingAuthorizer>();
        services.AddSingleton(diagnostics);
        services.AddSingleton<IHostDiagnosticSink>(diagnostics);
        services.AddSingleton(PluginModuleCatalog.Discover(snapshot));
        using var hostProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        try
        {
            pluginProviders.Compose(
                hostProvider.GetRequiredService<PluginModuleCatalog>(),
                hostProvider,
                registryBuilder,
                documentScopes,
                diagnostics);
            var registry = hostProvider.GetRequiredService<PluginRegistry>();
            hostProvider.GetRequiredService<WorkflowActionCatalogStore>().Commit(
                registry,
                hostProvider.GetRequiredService<PluginAvailabilityReadModel>());

            Assert.Equal(2, registry.Plugins.Count);
            var provider = registry.Plugins.Single(item =>
                item.Manifest.PluginId.Value == "myavalonia.plugin.my-small-tools");
            var studio = registry.Plugins.Single(item =>
                item.Manifest.PluginId.Value == "myavalonia.plugin.workflow-studio");
            Assert.Equal("3.1.0.0", PluginVersionText.Format(provider.Manifest.PluginVersion));
            Assert.Equal("1.2.0.0", PluginVersionText.Format(studio.Manifest.PluginVersion));
            Assert.Equal("3.3.0.0", PluginVersionText.Format(provider.Manifest.Sdk.MinInclusive));
            Assert.Single(registry.WorkflowActions);
            Assert.Equal(
                ActionId,
                Assert.Single(registry.WorkflowActions).Descriptor.Id.Value);
            Assert.Equal(
                ["myavalonia.plugin.workflow-studio"],
                registry.WorkflowActionConsumerIds.Select(item => item.Value).ToArray());

            var activator = hostProvider.GetRequiredService<PluginContributionActivator>();
            using var activation = activator.ActivateDocument(new DocumentTypeId(StudioDocumentId));
            await activation.Model.InitializeAsync(
                new NewDocumentActivation("G4 真实加密验收"),
                CancellationToken.None);
            var driver = new StudioDocumentDriver(activation.Model);
            driver.AddEncryptVideoStep(sourcePath, outputPath, SecretCanary);
            Assert.True(driver.CanExecute);

            var first = await driver.RunAsync();
            Assert.True(first.Succeeded);
            Assert.Equal(WorkflowActionInvocationStatus.Succeeded, Assert.Single(first.Statuses));
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(outputPath));
            Assert.Equal(sourceHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
            var firstOutputHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(outputPath)));

            var second = await driver.RunAsync();
            Assert.False(second.Succeeded);
            Assert.Equal(WorkflowActionInvocationStatus.Failed, Assert.Single(second.Statuses));
            Assert.Equal(sourceHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
            Assert.Equal(firstOutputHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outputPath))));
            Assert.Empty(Directory.GetFiles(workRoot, "*.partial-*", SearchOption.AllDirectories));
        }
        finally
        {
            documentScopes.CloseAll();
            diagnostics.Dispose();
        }

        try
        {
            var diagnosticText = string.Join(
                Environment.NewLine,
                Directory.GetFiles(diagnosticsRoot, "*", SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(SecretCanary, diagnosticText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    /// <summary>测试环境确定性批准经过 Host 脱敏后的风险确认请求。</summary>
    private sealed class AllowingAuthorizer : IWorkflowActionAuthorizer
    {
        public Task<bool> AuthorizeAsync(
            WorkflowActionAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ActionId, request.Descriptor.Id.Value);
            Assert.DoesNotContain(SecretCanary, request.RedactedArgumentSummary, StringComparison.Ordinal);
            Assert.Contains("***", request.RedactedArgumentSummary, StringComparison.Ordinal);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// 通过 Studio 已公开给绑定层的集合、属性和命令模拟一次真实手工编辑。
    /// </summary>
    private sealed class StudioDocumentDriver
    {
        private readonly object _document;
        private readonly Type _documentType;

        internal StudioDocumentDriver(IPluginDocument document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _documentType = document.GetType();
        }

        internal bool CanExecute => Get<bool>(_document, "CanExecute");

        internal void AddEncryptVideoStep(string sourcePath, string outputPath, string password)
        {
            var choice = Items(Get<object>(_document, "AvailableActions")).Single(item =>
            {
                var descriptor = Get<WorkflowActionDescriptor>(item, "Descriptor");
                return descriptor.Id.Value == ActionId;
            });
            Set(_document, "SelectedAction", choice);
            Get<ICommand>(_document, "AddStepCommand").Execute(null);

            var step = Assert.Single(Items(Get<object>(_document, "Steps")));
            foreach (var argument in Items(Get<object>(step, "Arguments")))
            {
                var name = Get<string>(argument, "Name");
                switch (name)
                {
                    case "inputPath": SetConstant(argument, sourcePath); break;
                    case "outputPath": SetConstant(argument, outputPath); break;
                    case "password":
                        SetEnum(argument, "Mode", "Secret");
                        Set(argument, "Value", "${secret.video-password}");
                        break;
                    case "publicTitle": SetConstant(argument, "G4 真实标题"); break;
                    case "publicDescription": SetConstant(argument, "G4 真实描述"); break;
                    default: throw new InvalidOperationException($"G4 发现未知参数：{name}。");
                }
            }

            Set(_document, "SecretName", "video-password");
            Set(_document, "SecretValue", password);
            Get<ICommand>(_document, "StoreSecretCommand").Execute(null);
            Assert.Empty(Get<string>(_document, "SecretValue"));
            Get<ICommand>(_document, "ValidateCommand").Execute(null);
        }

        internal async Task<StudioRunObservation> RunAsync()
        {
            var method = _documentType.GetMethod("RunCurrentAsync", [typeof(CancellationToken)]) ??
                         throw new MissingMethodException(_documentType.FullName, "RunCurrentAsync");
            var task = Assert.IsAssignableFrom<Task>(method.Invoke(
                _document,
                [CancellationToken.None]));
            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task) ??
                         throw new InvalidOperationException("Studio Run 没有返回结果。");
            var entries = Items(Get<object>(result, "Entries"));
            return new StudioRunObservation(
                Get<bool>(result, "Succeeded"),
                entries.Select(item => Get<WorkflowActionInvocationStatus>(item, "Status")).ToArray());
        }

        private static void SetConstant(object argument, string value)
        {
            SetEnum(argument, "Mode", "Constant");
            Set(argument, "Value", JsonSerializer.Serialize(value));
        }

        private static IEnumerable<object> Items(object collection) =>
            ((IEnumerable)collection).Cast<object>();

        private static T Get<T>(object target, string propertyName) =>
            Assert.IsAssignableFrom<T>(target.GetType().GetProperty(propertyName)?.GetValue(target));

        private static void Set(object target, string propertyName, object? value)
        {
            var property = target.GetType().GetProperty(propertyName) ??
                           throw new MissingMemberException(target.GetType().FullName, propertyName);
            property.SetValue(target, value);
        }

        private static void SetEnum(object target, string propertyName, string value)
        {
            var property = target.GetType().GetProperty(propertyName) ??
                           throw new MissingMemberException(target.GetType().FullName, propertyName);
            property.SetValue(target, Enum.Parse(property.PropertyType, value));
        }
    }

    private sealed record StudioRunObservation(
        bool Succeeded,
        IReadOnlyList<WorkflowActionInvocationStatus> Statuses);
}
