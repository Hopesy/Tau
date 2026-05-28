# 2026-05-28 agent proxy stream provider

## 用户诉求

用户要求按 100% pi-mono parity `/goal` 持续执行，不停在计划层。

## 主要变更

- 新增 `Tau.Agent.Proxy.ProxyStreamProvider`，对齐上游 `packages/agent/src/proxy.ts` 的代理流能力。
- 新增 `ProxyStreamOptions`，支持通过 `ProxyStreamOptions.ProxyUrl/AuthToken` 或 `Model.BaseUrl` + `StreamOptions.ApiKey` 配置代理服务。
- 代理请求发送到 `/api/stream`，携带 `Authorization: Bearer <token>`，并序列化 `model/context/options` envelope。
- 实现 stripped proxy events 到 Tau `StreamEvent` 的重建：`start`、text/thinking/toolcall start/delta/end、`done`、`error`。
- 新增 `ProxyStreamProviderTests`，覆盖请求体、Authorization header、text/toolcall 事件重建、usage/stop reason 映射和 HTTP error 脱敏错误路径。
- 更新 100% parity matrix，把 Agent stream proxy 从 `missing` 调整为 `ported`，并记录真实 proxy-server e2e 仍未验证。

## 设计意图

上游 proxy 不是普通 provider，而是让应用把 LLM 调用交给拥有鉴权能力的服务端。Tau 的 Agent runtime 当前通过 `IStreamProvider` 获取模型流，所以最小、清晰的 .NET-native 对齐方式是实现一个可注册到任意 `model.Api` 的 provider，而不是改 `AgentRuntime` 公共循环。

请求序列化没有复用运行时反射序列化，而是显式组装 proxy envelope 并使用 source-generated JSON context，避免 AOT/trim 场景下的 `JsonTypeInfo` 缺失。测试中第一次失败暴露了嵌套 `List<Dictionary<string, object>>` 的 source-gen 元数据缺口，最终改成稳定的 `List<object>` envelope。

## 关键受影响文件

- `src/Tau.Agent/Proxy/ProxyStreamProvider.cs`
- `tests/Tau.Agent.Tests/ProxyStreamProviderTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/histories/2026-05/20260528-1316-agent-proxy-stream-provider.md`

## 验证

- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter ProxyStreamProviderTests --verbosity minimal`
  - 结果：2/2 通过。
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --verbosity minimal`
  - 结果：91/91 通过。

## 剩余缺口

- 尚未跑真实 proxy-server e2e；matrix 保留为 `ported`，不标 `verified`。
- `Tau.Agent` 高层 facade、完整 event/error/cancel 语义和跨模块 correlation closure 仍在 Phase 2 backlog。
