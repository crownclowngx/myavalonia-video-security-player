using MyAvaloniaManagement.PluginSdk.UI;

namespace VideoSecurityPlayer.Plugin;

/// <summary>本插件专属矢量。保持纯不可变数据，不保存 Host 服务、控件或主题画刷。</summary>
internal static class PluginIcons
{
    /// <summary>叠放的视频条目。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition VideoLibrary { get; } = new(
        "M2,2h12v2h-12Z M4,5h12v2h-12Z M6,8h13v10h-13Z M7.5,9.5h10.0v7.0h-10.0Z M11,11v4l4,-2Z", 20, 20);

    /// <summary>闭锁加密。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition VideoLock { get; } = new(
        "M5,8V6a5,5 0 0 1 10,0v2h2v10H3V8Z M7,8h6V6a3,3 0 0 0 -6,0Z M9,11v4h2v-4Z", 20, 20);

    /// <summary>开锁解密。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition VideoUnlock { get; } = new(
        "M5,8V6a5,5 0 0 1 10,0h-2a3,3 0 0 0 -6,0v2h10v10H3V8Z M9,11v4h2v-4Z", 20, 20);

}
