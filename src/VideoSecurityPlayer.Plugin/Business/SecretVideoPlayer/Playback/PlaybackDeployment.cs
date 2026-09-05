using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

public enum DeploymentIssueCode
{
    UnsupportedOperatingSystem,
    UnsupportedProcessArchitecture,
    PluginDirectoryMissing,
    ManagedBridgeMissing,
    ManagedBridgeInvalid,
    NativeLibraryMissing,
    NativeArchitectureMismatch,
    NativePluginSetIncomplete,
    NativeInitializationFailed
}

public sealed record DeploymentIssue(
    DeploymentIssueCode Code,
    string Summary,
    string CheckedPath,
    string SuggestedAction);

public sealed record DeploymentCheckResult(
    string PluginDirectory,
    string RuntimeDirectory,
    IReadOnlyList<DeploymentIssue> Issues)
{
    public bool IsReady => Issues.Count == 0;
}

public interface IPlaybackDeploymentProbe
{
    DeploymentCheckResult Check();
}

/// <summary>
/// 对插件私有 LibVLC 部署执行无副作用检查；不加载 DLL，也不初始化 LibVLC。
/// </summary>
public sealed class PlaybackDeploymentProbe : IPlaybackDeploymentProbe
{
    // 不用“plugins 目录非空”作为完整性判断：那会把缺少 MP4/WebM 解复用器的
    // 半残部署误判为可用。这里冻结 P0 真实资产实际依赖的最小模块集合；
    // Release Manifest 则负责对发布包中的完整原生树做逐文件哈希验证。
    private static readonly (string RelativePath, string Description)[] RequiredNativePlugins =
    [
        (Path.Combine("plugins", "demux", "libmp4_plugin.dll"), "MP4 demux"),
        (Path.Combine("plugins", "demux", "libmkv_plugin.dll"), "Matroska/WebM demux"),
        (Path.Combine("plugins", "codec", "libavcodec_plugin.dll"), "FFmpeg codec"),
        (Path.Combine("plugins", "video_output", "libdirect3d11_plugin.dll"), "Direct3D 11 video output"),
        (Path.Combine("plugins", "audio_output", "libmmdevice_plugin.dll"), "Windows audio output")
    ];

    private readonly IPlaybackRuntimeLayoutProvider _layoutProvider;
    private readonly WindowsX64PlaybackCapabilitiesProvider _capabilitiesProvider;

    public PlaybackDeploymentProbe()
        : this(
            new PluginLocalPlaybackRuntimeLayoutProvider(),
            new WindowsX64PlaybackCapabilitiesProvider())
    {
    }

    internal PlaybackDeploymentProbe(
        IPlaybackRuntimeLayoutProvider layoutProvider,
        WindowsX64PlaybackCapabilitiesProvider capabilitiesProvider)
    {
        _layoutProvider = layoutProvider ??
                          throw new ArgumentNullException(nameof(layoutProvider));
        _capabilitiesProvider = capabilitiesProvider ??
                                throw new ArgumentNullException(nameof(capabilitiesProvider));
    }

    internal PlaybackDeploymentProbe(
        string pluginDirectory,
        Func<bool>? isWindows = null,
        Func<Architecture>? processArchitecture = null)
        : this(
            new FixedPlaybackRuntimeLayoutProvider(pluginDirectory),
            new WindowsX64PlaybackCapabilitiesProvider(
                isWindows ?? OperatingSystem.IsWindows,
                processArchitecture ?? (() =>
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)))
    {
    }

    public DeploymentCheckResult Check()
    {
        // 探针一次收集所有可识别问题，用户修复部署时不必经历“补一个文件、重启、
        // 再发现下一个文件”的循环。平台问题也不提前返回，因为发布目录可能同时损坏。
        var layout = _layoutProvider.Resolve();
        var pluginDirectory = layout.PluginDirectory;
        var runtimeDirectory = layout.RuntimeDirectory;
        var issues = new List<DeploymentIssue>();

        if (!_capabilitiesProvider.IsWindows)
        {
            issues.Add(Issue(
                DeploymentIssueCode.UnsupportedOperatingSystem,
                "安全视频播放器当前只支持 Windows。",
                pluginDirectory,
                "请在 Windows x64 宿主中使用该插件。"));
        }

        if (_capabilitiesProvider.ProcessArchitecture != Architecture.X64)
        {
            issues.Add(Issue(
                DeploymentIssueCode.UnsupportedProcessArchitecture,
                "宿主进程不是 x64 架构。",
                Environment.ProcessPath ?? pluginDirectory,
                "请安装并启动 Windows x64 版本的宿主。"));
        }

        if (!Directory.Exists(pluginDirectory))
        {
            issues.Add(Issue(
                DeploymentIssueCode.PluginDirectoryMissing,
                "VideoSecurityPlayer 插件目录不存在。",
                pluginDirectory,
                "请重新解压完整的 VideoSecurityPlayer Windows x64 发布包。"));
            return new DeploymentCheckResult(pluginDirectory, runtimeDirectory, issues);
        }

        ValidateManagedBridge(
            pluginDirectory,
            "LibVLCSharp.dll",
            "LibVLCSharp",
            issues);
        ValidateManagedBridge(
            pluginDirectory,
            "LibVLCSharp.Avalonia.dll",
            "LibVLCSharp.Avalonia",
            issues);
        ValidateNativeLibrary(Path.Combine(runtimeDirectory, "libvlc.dll"), issues);
        ValidateNativeLibrary(Path.Combine(runtimeDirectory, "libvlccore.dll"), issues);

        var pluginsDirectory = Path.Combine(runtimeDirectory, "plugins");
        if (!Directory.Exists(pluginsDirectory))
        {
            issues.Add(Issue(
                DeploymentIssueCode.NativePluginSetIncomplete,
                "LibVLC 插件目录缺失。",
                pluginsDirectory,
                "请重新部署完整的 native/win-x64/libvlc 目录。"));
        }
        else
        {
            foreach (var (relativePath, description) in RequiredNativePlugins)
            {
                var path = Path.Combine(runtimeDirectory, relativePath);
                if (!File.Exists(path))
                {
                    issues.Add(Issue(
                        DeploymentIssueCode.NativePluginSetIncomplete,
                        $"LibVLC 缺少必要模块：{description}。",
                        path,
                        "请重新部署完整的 VideoSecurityPlayer Windows x64 发布包。"));
                }
            }
        }

        return new DeploymentCheckResult(pluginDirectory, runtimeDirectory, issues);
    }

    internal static DeploymentCheckResult WithInitializationFailure(
        DeploymentCheckResult result)
    {
        var issues = result.Issues.ToList();
        issues.Add(Issue(
            DeploymentIssueCode.NativeInitializationFailed,
            "LibVLC 原生运行库初始化失败。",
            result.RuntimeDirectory,
            "请重新部署插件并重启宿主；不要改用系统 VLC 或 PATH 中的 DLL。"));
        return result with { Issues = issues };
    }

    private void ValidateManagedBridge(
        string pluginDirectory,
        string fileName,
        string expectedAssemblyName,
        ICollection<DeploymentIssue> issues)
    {
        var path = Path.Combine(pluginDirectory, fileName);
        if (!File.Exists(path))
        {
            issues.Add(Issue(
                DeploymentIssueCode.ManagedBridgeMissing,
                $"托管桥接程序集 {fileName} 缺失。",
                path,
                "请重新解压完整发布包，保持三个托管程序集位于同一插件目录。"));
            return;
        }

        try
        {
            // GetAssemblyName 只读取 PE/CLR 元数据，不会把程序集加载进当前上下文，
            // 因此自检不会污染宿主的插件加载顺序，也不会触发 LibVLC 原生解析。
            var actualName = AssemblyName.GetAssemblyName(path).Name;
            if (!string.Equals(actualName, expectedAssemblyName, StringComparison.Ordinal))
            {
                throw new BadImageFormatException();
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            issues.Add(Issue(
                DeploymentIssueCode.ManagedBridgeInvalid,
                $"托管桥接程序集 {fileName} 无效或名称不匹配。",
                path,
                "请删除现有插件目录后重新部署，避免混用旧版本文件。"));
        }
    }

    private static void ValidateNativeLibrary(
        string path,
        ICollection<DeploymentIssue> issues)
    {
        if (!File.Exists(path))
        {
            issues.Add(Issue(
                DeploymentIssueCode.NativeLibraryMissing,
                $"LibVLC 核心文件 {Path.GetFileName(path)} 缺失。",
                path,
                "请重新部署完整的 native/win-x64/libvlc 目录。"));
            return;
        }

        try
        {
            // PEReader 直接检查 COFF Machine。仅凭文件名或目录中的 win-x64 字样不可靠，
            // 混入 x86 DLL 时应在 Core.Initialize 前给出可恢复诊断，而不是让进程崩溃。
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            {
                issues.Add(Issue(
                    DeploymentIssueCode.NativeArchitectureMismatch,
                    $"{Path.GetFileName(path)} 不是 AMD64 原生库。",
                    path,
                    "请部署 Windows x64 发布包，不要混用 x86、ARM64 或系统 VLC 文件。"));
            }
        }
        catch (BadImageFormatException)
        {
            issues.Add(Issue(
                DeploymentIssueCode.NativeArchitectureMismatch,
                $"{Path.GetFileName(path)} 不是有效的 Windows x64 PE 文件。",
                path,
                "请删除损坏文件并重新部署完整发布包。"));
        }
        catch (IOException)
        {
            issues.Add(Issue(
                DeploymentIssueCode.NativeLibraryMissing,
                $"{Path.GetFileName(path)} 当前无法读取。",
                path,
                "请检查文件权限、杀毒软件隔离状态并重新部署。"));
        }
    }

    private static DeploymentIssue Issue(
        DeploymentIssueCode code,
        string summary,
        string checkedPath,
        string suggestedAction) =>
        new(code, summary, Path.GetFullPath(checkedPath), suggestedAction);

    /// <summary>
    /// 测试部署夹具使用的固定布局；生产代码始终使用程序集位置布局。
    /// </summary>
    private sealed class FixedPlaybackRuntimeLayoutProvider : IPlaybackRuntimeLayoutProvider
    {
        private readonly PlaybackRuntimeLayout _layout;

        public FixedPlaybackRuntimeLayoutProvider(string pluginDirectory)
        {
            var fullPath = Path.GetFullPath(
                pluginDirectory ?? throw new ArgumentNullException(nameof(pluginDirectory)));
            _layout = new PlaybackRuntimeLayout(
                fullPath,
                Path.Combine(
                    fullPath,
                    PluginLocalPlaybackRuntimeLayoutProvider.RuntimeRelativePath));
        }

        public PlaybackRuntimeLayout Resolve() => _layout;
    }
}

internal sealed class PlaybackDeploymentException(
    DeploymentCheckResult result,
    Exception? innerException = null)
    : Exception("VideoSecurityPlayer playback deployment is unavailable.", innerException)
{
    public DeploymentCheckResult Result { get; } =
        result ?? throw new ArgumentNullException(nameof(result));
}
