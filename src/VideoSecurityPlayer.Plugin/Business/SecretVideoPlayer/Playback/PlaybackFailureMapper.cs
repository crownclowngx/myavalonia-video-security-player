namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>把内部异常映射为稳定、安全的播放失败。</summary>
internal static class PlaybackFailureMapper
{
    public static PlaybackFailure MapLoad(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            PlaybackDeploymentException deploymentException =>
                MapDeployment(deploymentException.Result),
            OperationCanceledException => Failure(
                PlaybackFailureCode.Cancelled,
                "操作已取消。"),
            UnauthorizedAccessException => Failure(
                PlaybackFailureCode.AuthenticationFailed,
                "密码错误或加密视频认证失败。"),
            FileNotFoundException or DirectoryNotFoundException => Failure(
                PlaybackFailureCode.InputUnavailable,
                "视频文件不存在或已被移动。"),
            InvalidDataException => Failure(
                PlaybackFailureCode.InvalidFormat,
                "视频文件格式无效或内容已损坏。"),
            IOException => Failure(
                PlaybackFailureCode.InputUnavailable,
                "视频文件当前无法读取。"),
            ArgumentException => Failure(
                PlaybackFailureCode.InvalidRequest,
                "播放请求无效。"),
            _ => Failure(
                PlaybackFailureCode.Unknown,
                "加载视频时发生未知错误。")
        };
    }

    public static PlaybackFailure MapMediaInput(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            InvalidDataException or EndOfStreamException => Failure(
                PlaybackFailureCode.CorruptedContent,
                "加密视频内容认证失败或文件已被截断。"),
            UnauthorizedAccessException => Failure(
                PlaybackFailureCode.AuthenticationFailed,
                "密码错误或加密视频认证失败。"),
            FileNotFoundException or DirectoryNotFoundException or IOException => Failure(
                PlaybackFailureCode.InputUnavailable,
                "播放时无法继续读取视频文件。"),
            ObjectDisposedException => Failure(
                PlaybackFailureCode.Cancelled,
                "播放资源已关闭。"),
            _ => Failure(
                PlaybackFailureCode.DecodeFailed,
                "视频读取或解码失败。")
        };
    }

    public static PlaybackFailure ParseFailed() =>
        new(
            PlaybackFailureCode.ParseFailed,
            "LibVLC 无法解析该媒体。",
            "请先检查文件完整性；若所有媒体均失败，请重新执行部署自检或重新部署插件。",
            "PLAYBACK_PARSE_FAILED");

    public static PlaybackFailure DecodeFailed() =>
        new(
            PlaybackFailureCode.DecodeFailed,
            "LibVLC 无法解码或播放该媒体。",
            "请确认媒体编码受支持；若所有媒体均失败，请重新部署插件。",
            "PLAYBACK_DECODE_FAILED");

    public static PlaybackFailure SurfaceRestoreFailed() =>
        Failure(PlaybackFailureCode.SurfaceRestoreFailed, "视频输出表面恢复失败，请手动播放。");

    private static PlaybackFailure Failure(PlaybackFailureCode code, string message) =>
        new(code, message);

    public static PlaybackFailure MapDeployment(DeploymentCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var first = result.Issues.FirstOrDefault();
        return new PlaybackFailure(
            PlaybackFailureCode.DeploymentUnavailable,
            first?.Summary ?? "安全视频播放运行库不可用。",
            first?.SuggestedAction ?? "请重新部署 VideoSecurityPlayer Windows x64 发布包并重启宿主。",
            first is null ? "DEPLOYMENT_UNAVAILABLE" : $"DEPLOYMENT_{first.Code}");
    }
}
