namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>加密与解密共用的界面阶段说明，不依赖具体计划或队列类型。</summary>
public static class BatchInteractionPolicy
{
    /// <summary>按用户可以修复的顺序说明下一步；空字符串表示当前允许开始。</summary>
    public static string GetStartHint(bool hasWork, bool busy, bool outputReady,
        bool passwordReady, bool passwordMatches, bool planCurrent, bool runnable)
    {
        if (busy) return "正在处理；关闭此页面会取消未完成任务";
        if (!hasWork) return "请添加待处理文件";
        if (!outputReady) return "请选择输出目录";
        if (!passwordReady) return "请输入有效的本批次密码";
        if (!passwordMatches) return "两次输入的密码不一致";
        if (!planCurrent) return "配置尚未检查或已经变化，请重新检查批次";
        if (!runnable) return "没有可执行项目，请查看并修复检查问题";
        return string.Empty;
    }

    /// <summary>重试仍要重新检查；只有结果无待确认事项且输出未变化时才直接执行。</summary>
    public static bool CanRunRetry(bool canStart, bool hasIssues, bool outputsUnchanged) =>
        canStart && !hasIssues && outputsUnchanged;
}
