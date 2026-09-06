using Xunit;

namespace MyAvaloniaManagement.PluginTests;

/// <summary>
/// 真实双插件测试必须由专项脚本提供隔离 ZIP 与媒体。发现阶段明确标记未配置场景，
/// 避免测试方法直接返回后被 xUnit 记为通过；Full 门禁另外要求该用例实际执行。
/// </summary>
internal sealed class WorkflowIntegrationFactAttribute : FactAttribute
{
    public WorkflowIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYAVALONIA_WORKFLOW_G4_PLUGIN_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYAVALONIA_WORKFLOW_G4_MEDIA_PATH")))
        {
            Skip = "尚未提供真实双插件包或媒体，请通过 Test-HostIntegration.ps1 执行完整场景。";
        }
    }
}
