using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class R1BatchOperationVersionTests
{
    [Fact]
    public void 输入编辑使旧预检过期而当前运行进度仍有效()
    {
        var version = new BatchOperationVersion();
        Assert.False(version.HasCurrentPlan);
        var generation = version.AdvanceOperation();
        var revision = version.Revision;
        Assert.True(version.TryAcceptPlan(generation, revision));
        Assert.True(version.HasCurrentPlan);
        version.InvalidatePlan();
        Assert.False(version.HasCurrentPlan);
        Assert.False(version.TryAcceptPlan(generation, revision));
        Assert.True(version.IsCurrentOperation(generation));
        Assert.True(version.TryAcceptPlan(generation, version.Revision));
    }

    [Fact]
    public void 同一修订的旧操作不能覆盖新计划且完成后旧回调失效()
    {
        var version = new BatchOperationVersion();
        var first = version.AdvanceOperation();
        var second = version.AdvanceOperation();
        Assert.True(version.TryAcceptPlan(second, version.Revision));
        Assert.False(version.TryAcceptPlan(first, version.Revision));
        Assert.True(version.HasCurrentPlan);
        Assert.False(version.IsCurrentOperation(first));
        version.AdvanceOperation();
        Assert.False(version.IsCurrentOperation(second));
    }

    [Fact]
    public void 文档之间账本独立且并发递增不丢失修订()
    {
        var first = new BatchOperationVersion();
        var second = new BatchOperationVersion();
        Parallel.For(0, 100, _ => first.InvalidatePlan());
        Assert.Equal(100, first.Revision);
        Assert.Equal(0, second.Revision);
        var generations = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 100, _ => generations.Add(first.AdvanceOperation()));
        Assert.Equal(100, generations.Distinct().Count());
        Assert.True(first.IsCurrentOperation(100));
    }
}
