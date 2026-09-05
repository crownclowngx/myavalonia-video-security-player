using Xunit;

// Headless UI 测试共享 Avalonia Dispatcher 和静态平台状态。两轮隔离门禁要求覆盖率
// 逐字段一致，因此测试集合按顺序使用这些共享设施，避免调度噪声影响命中快照。
// 该设置仅属于测试程序集，不改变窗口、插件或 SDK 的生产行为。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
