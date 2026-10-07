// 作者：xxx
// 1. 【CodingAgent】【集成测试】限制同时启动的 Node 宿主数量，工具内部的并发执行和重入测试仍保持真实并行
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
