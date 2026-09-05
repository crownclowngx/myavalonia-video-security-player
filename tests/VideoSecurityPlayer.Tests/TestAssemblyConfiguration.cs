using Xunit;

// VideoSecurityPlayer 的测试会共同驱动媒体线程、Avalonia Dispatcher 和全局资源诊断计数。
// 并行执行测试集合时，测试结果虽然通常仍为绿色，但覆盖率命中会因调度先后出现少量漂移；
// G14 需要比较两轮隔离发布证据，覆盖率属于不可忽略的发布事实，因此在测试程序集边界
// 关闭集合并行。这里没有改变生产代码或断言，只让同一组测试按确定顺序采集证据。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
