// 作者：xxx
namespace Tau.Ai.Tests;

/// <summary>【AI】【测试隔离】修改进程环境的用例不能与读取默认认证、配置和代理环境的其他测试并行。</summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
