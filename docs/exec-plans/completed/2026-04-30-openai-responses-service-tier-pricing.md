# OpenAI Responses service-tier pricing 支持计划

## 目标

把 `openai-responses` / `openai-codex-responses` 的 `service_tier` 请求与响应语义补到 Tau 的 usage/cost 计算层：请求可发送 `service_tier`，响应可记录 effective service tier，`ModelCatalog.CalculateCost` 对 `flex` / `priority` 应用上游一致的价格倍率。

## 范围

包含：

- `Usage` 记录 effective service tier
- Responses stream parser 从 `response.service_tier` 或请求 option 解析 tier
- OpenAI Responses provider 传入 requested `ServiceTier`
- Codex Responses provider 传入 requested `ServiceTier`，并保留上游 Codex 特例：响应 `default` 但请求为 `flex`/`priority` 时按请求 tier 计价
- `ModelCatalog.CalculateCost` 对 `flex=0.5`、`priority=2` 应用倍率
- 单测覆盖 OpenAI request/response tier、Codex default->requested tier、ModelCatalog multiplier
- 同步 `next.md`、architecture、quality、baseline plan 与 history

不包含：

- 动态/实时价格表
- provider e2e 校验真实账单
- Azure service-tier 支持
- Copilot dynamic headers / vision behavior

## 背景

上游 `openai-responses.ts` 与 `openai-codex-responses.ts` 都支持 `serviceTier` option，并在 usage cost 上应用倍率：`flex` 为 0.5，`priority` 为 2。Codex 还对 response service tier 为 `default` 但 request tier 为 `flex`/`priority` 的情况做请求 tier 回退。Tau 当前只把 `service_tier` 写入 request body，没有把 tier 带进 usage/cost 层。

## 风险

- 风险：Tau 当前 `Usage` 只记录 token，不记录成本。
  - 缓解：只给 `Usage` 增加可空 `ServiceTier`，成本仍通过现有 `ModelCatalog.CalculateCost` 统一计算，避免在 stream event 内嵌成本对象。
- 风险：已有 `Usage` equality 测试可能受影响。
  - 缓解：新增字段默认 `null`，现有 `new Usage(...)` 比较保持通过。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 service-tier usage/cost 支持
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 验证结果

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`：通过，61 passed。
- 项目级顺序 build/test（7 个 source 项目 build、5 个 test 项目 build、5 个 test 项目 test）：通过；测试计数为 Tau.Ai 61、Tau.Agent 2、Tau.Tui 4、Tau.CodingAgent 6、Tau.Pods 7。

## 决策记录

- 2026-04-30：成本倍率落在 `ModelCatalog.CalculateCost`，而不是 provider 内直接计算成本。原因是 Tau 当前 usage 与 cost 是分离模型，保持 provider 只负责 token/tier 事实，成本统一由 catalog 计算。
