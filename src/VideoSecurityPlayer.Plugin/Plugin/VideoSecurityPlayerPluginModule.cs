using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Workflow;
using VideoSecurityPlayer.Constants;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using VideoSecurityPlayer.Views.SecretVideoPlayer;

namespace VideoSecurityPlayer.Plugin;

/// <summary>VideoSecurityPlayer 接入当前 V3 私有 Provider 的唯一组合入口。</summary>
/// <remarks>
/// 模块只描述对象关系和四个 UI 贡献，不创建 View、Document 或 LibVLC 实例。Host 为每次
/// 文档创建建立独立 Scope，因此播放器、队列、密码和媒体库状态不会跨标签页共享；无状态的
/// 格式服务仍按其真实职责使用 singleton 或 transient，避免用统一生命周期掩盖所有权。
/// </remarks>
public sealed class VideoSecurityPlayerPluginModule : IPluginModule
{
    /// <inheritdoc />
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var services = registration.Services;

        // 平台事实和运行时布局可跨文档复用。LibVLC 初始化仍保持惰性：仅解析模块元数据或
        // 构建 Registry 时不会加载原生库，这也是最终 ZIP 可以安全预检的必要条件。
        services.AddSingleton<IPlaybackRuntimeLayoutProvider,
            PluginLocalPlaybackRuntimeLayoutProvider>();
        services.AddSingleton<WindowsX64PlaybackCapabilitiesProvider>();
        services.AddSingleton<PlaybackDeploymentProbe>();
        services.AddSingleton<IPlaybackDeploymentProbe>(provider =>
            provider.GetRequiredService<PlaybackDeploymentProbe>());
        services.AddSingleton<IPlaybackPlatformStatus, PlaybackPlatformStatus>();
        services.AddSingleton<LibVlcRuntime>();
        services.AddSingleton<IPlaybackRuntimeInitializer>(provider =>
            provider.GetRequiredService<LibVlcRuntime>());

        // 原生 PlayerHost、串行调度器和释放队列由单个 Document Scope 独占。媒体切换只替换
        // MediaSource，不重建播放器或把 HWND 所有权扩散到业务模型。
        services.AddScoped<IPlaybackBackendFactory, LibVlcPlaybackBackendFactory>();
        services.AddScoped<LazyPlaybackBackend>();
        services.AddScoped<IPlaybackPlayerHost>(provider =>
            provider.GetRequiredService<LazyPlaybackBackend>());
        services.AddScoped<IPlaybackMediaSourceFactory>(provider =>
            provider.GetRequiredService<LazyPlaybackBackend>());
        services.AddScoped<IPlaybackBackendInitializer>(provider =>
            provider.GetRequiredService<LazyPlaybackBackend>());
        services.AddScoped<IPlaybackNativeDispatcher, PlaybackNativeDispatcher>();
        services.AddScoped<IPlaybackResourceReaper, PlaybackResourceReaper>();
        services.AddScoped<SecureVideoPlayer>();
        services.AddScoped<ISecureVideoPlaybackSession>(provider =>
            provider.GetRequiredService<SecureVideoPlayer>());
        services.AddScoped<IPlaybackSurfaceSession>(provider =>
            provider.GetRequiredService<SecureVideoPlayer>());
        services.AddScoped<IPlaybackDiagnosticState>(provider =>
            provider.GetRequiredService<SecureVideoPlayer>());
        services.AddScoped<IPlaybackDiagnosticExporter, PlaybackDiagnosticExporter>();
        services.AddScoped<VideoPlayerControlViewModel>(provider =>
        {
            var viewModel = new VideoPlayerControlViewModel(
                provider.GetRequiredService<ISecureVideoPlaybackSession>(),
                provider.GetRequiredService<IPlaybackSurfaceSession>(),
                provider.GetRequiredService<IPlaybackPlatformStatus>(),
                provider.GetRequiredService<IPlaybackBackendInitializer>(),
                provider.GetService<IPlaybackPreferenceStore>());
            viewModel.ConfigureDiagnosticExporter(
                provider.GetRequiredService<IPlaybackDiagnosticExporter>());
            return viewModel;
        });

        // 用户数据文件是进程级唯一事实源；窄接口映射同一实例，调用方只取得自身所需能力。
        services.AddSingleton<SecretVideoUserDataStore>();
        services.AddSingleton<IPlaybackPreferenceStore>(provider =>
            provider.GetRequiredService<SecretVideoUserDataStore>());
        services.AddSingleton<IVideoLibrarySettingsStore>(provider =>
            provider.GetRequiredService<SecretVideoUserDataStore>());
        services.AddSingleton<IPlaybackHistoryStore>(provider =>
            provider.GetRequiredService<SecretVideoUserDataStore>());
        services.AddSingleton<ISecretVideoUserDataDiagnostics>(provider =>
            provider.GetRequiredService<SecretVideoUserDataStore>());
        services.AddTransient<IVideoLibraryScanner, VideoLibraryScanner>();
        services.AddScoped<IVideoLibraryCatalogSession, VideoLibraryCatalogSession>();
        services.AddScoped<PlaybackHistoryCoordinator>();
        services.AddScoped<VideoLibraryBrowserViewModel>();

        // 预检与输出事务无跨调用状态；队列运行器持有“当前项”和取消源，必须隔离在文档内。
        services.AddTransient<IStoragePreflightProbe, StoragePreflightProbe>();
        services.AddTransient<IOutputFileTransactionFactory, OutputFileTransactionFactory>();
        services.AddScoped(typeof(ISequentialVideoQueueRunner<>),
            typeof(SequentialVideoQueueRunner<>));
        services.AddTransient<IOutputPathConflictResolver, OutputPathConflictResolver>();
        services.AddTransient<ISecvid03Encryptor, Secvid03Encryptor>();
        services.AddScoped<IVideoEncryptionService, VideoEncryptorService>();
        services.AddScoped<IVideoBatchEncryptionService, VideoBatchEncryptionService>();
        services.AddTransient<ISecvid03Decryptor, Secvid03Decryptor>();
        services.AddTransient<DecryptionOutputPathResolver>();
        services.AddScoped<IVideoDecryptionService, VideoDecryptionService>();

        // Workflow Action 只声明经过筛选的无 UI 应用入口。AddWorkflowAction 会由 Host 在插件
        // 私有 Provider 中追加 scoped Handler，因此这里不能再手工注册第二份生命周期，也不能
        // 让现有 ViewModel 改走 Action 后形成 UI/自动化两套业务语义。
        registration.AddWorkflowAction<EncryptVideoWorkflowActionHandler>(
            EncryptVideoWorkflowAction.CreateDescriptor());

        // 注册 API 只冻结根模型声明；Document 和 Action 的 scoped 生命周期都由 Host 最终追加。
        registration.AddDocument<SecretVideoPlayerViewModel, SecretVideoPlayerView>(
            new DocumentDescriptor(
                VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
                "加密视频播放器",
                "支持 SECVID03/AES-256-GCM 认证分块和随机读取的加密视频播放器",
                "视频工具"));
        registration.AddDocument<SecretVideoLibraryViewModel, SecretVideoLibraryView>(
            new DocumentDescriptor(
                VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument,
                "加密视频库播放器",
                "扫描文件夹中的 SECVID03 视频，支持公开信息搜索和公共密码播放",
                "视频工具"));
        registration.AddDocument<VideoEncryptorViewModel, VideoEncryptorView>(
            new DocumentDescriptor(
                VideoSecurityPlayerContributionIds.VideoEncryptorDocument,
                "视频文件加密器",
                "使用 SECVID03/AES-256-GCM 分块加密视频，支持标题、描述和随机读取播放",
                "视频工具"));
        registration.AddDocument<VideoDecryptorViewModel, VideoDecryptorView>(
            new DocumentDescriptor(
                VideoSecurityPlayerContributionIds.VideoDecryptorDocument,
                "批量视频解密器",
                "使用一个公共密码批量解密 SECVID03 视频，并安全导出原始文件",
                "视频工具"));
    }
}
