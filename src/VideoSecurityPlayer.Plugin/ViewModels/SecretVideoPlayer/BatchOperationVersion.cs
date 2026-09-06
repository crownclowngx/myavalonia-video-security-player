namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

/// <summary>两类批次共用的有效性账本，只保存队列修订、操作代次和已接受计划的修订。</summary>
/// <remarks>
/// 修订回答“输入是否改变”，代次回答“回调是否属于当前操作”，二者不能合并。
/// 例如运行中移除尚未开始的项目要作废旧计划，但仍应接收当前运行中其他项目的进度。
/// 本组件不保存计划、密码、RunId、ItemId 或取消源；这些仍由领域流程或文档作用域负责。
/// 锁仅覆盖数值比较和更新，不执行外部回调，不改变 ViewModel 在 UI 线程更新集合的边界。
/// </remarks>
internal sealed class BatchOperationVersion
{
    private readonly object _sync = new();
    private long _revision;
    private long _preparedRevision = -1;
    private int _generation;

    public long Revision { get { lock (_sync) return _revision; } }
    public bool HasCurrentPlan { get { lock (_sync) return _preparedRevision == _revision; } }

    /// <summary>启动新操作或结束旧运行，使排队中的旧回调失效；不会改变输入修订。</summary>
    public int AdvanceOperation()
    {
        lock (_sync) return ++_generation;
    }

    public bool IsCurrentOperation(int generation)
    {
        lock (_sync) return generation == _generation;
    }

    /// <summary>只有代次和输入修订都未改变，才允许接受预检计划；拒绝不会破坏较新的计划。</summary>
    public bool TryAcceptPlan(int generation, long revision)
    {
        lock (_sync)
        {
            if (generation != _generation || revision != _revision) return false;
            _preparedRevision = revision;
            return true;
        }
    }

    public void InvalidatePlan()
    {
        lock (_sync)
        {
            _revision++;
            _preparedRevision = -1;
        }
    }
}
