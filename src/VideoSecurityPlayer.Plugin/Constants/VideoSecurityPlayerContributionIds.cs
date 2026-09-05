using MyAvaloniaManagement.PluginSdk;

namespace VideoSecurityPlayer.Constants;

/// <summary>集中定义 VideoSecurityPlayer 对 Host V3 暴露的稳定贡献身份。</summary>
/// <remarks>
/// 这些 ID 已经进入用户配置和插件清单，迁移只改变承载模型，不能改变业务身份。
/// Legacy GUID 不属于当前 V3 输入，因此不在生产程序集保留双写或别名兼容路径。
/// </remarks>
public static class VideoSecurityPlayerContributionIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.my-small-tools");
    /// <summary>
    /// 获取“加密视频并保留源文件”工作流动作的稳定身份。
    /// </summary>
    /// <remarks>
    /// 该身份永久表达非破坏性语义。后续即使增加“验证后删除源文件”的独立能力，
    /// 也不得给本动作追加删除开关或改变此 ID 的含义。
    /// </remarks>
    internal static readonly WorkflowActionId EncryptVideoAction =
        new("myavalonia.plugin.my-small-tools.workflow.encrypt-video");
    public static readonly DocumentTypeId SecretVideoPlayerDocument =
        new("myavalonia.plugin.my-small-tools.document.secret-video-player");
    public static readonly DocumentTypeId VideoEncryptorDocument =
        new("myavalonia.plugin.my-small-tools.document.video-encryptor");
    public static readonly DocumentTypeId SecretVideoLibraryDocument =
        new("myavalonia.plugin.my-small-tools.document.secret-video-library");
    public static readonly DocumentTypeId VideoDecryptorDocument =
        new("myavalonia.plugin.my-small-tools.document.video-decryptor");
}
