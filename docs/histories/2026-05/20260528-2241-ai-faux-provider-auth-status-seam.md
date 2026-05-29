# 变更历史：Tau.Ai Faux provider 与 auth status store seam 收口

## 用户诉求

持续执行 Tau 100% pi-mono parity 计划，接续 Phase 2 `AI public API/bin and helper export closure` 候选项，继续关闭不依赖外部凭证的 AI public provider / auth status contract 缺口。

## 本次变更

- 新增 `Tau.Ai.Providers.Faux` public scripted provider baseline，对照上游 `packages/ai/src/providers/faux.ts` 固定 .NET-native scripted test provider 入口。
- Faux provider 覆盖注册/注销、默认与多模型定义、queued responses、factory responses、text/thinking/tool-call helper、usage/cache estimate、streamed delta ordering、多 tool call、terminal error/aborted message 和 exhausted response error。
- `AiPublicApiCompileSampleTests` 扩展到 `Tau.Ai.Providers.Faux`，确保外部消费者样例能直接注册 Faux provider、消费 tool-use assistant response 并注销 provider。
- `ProviderAuthResolver` 构造函数新增可注入 `ModelConfigurationStore`，`GetStatus(model)` 的 models.json status 检查复用注入 store，不再在 resolver 内部固定创建默认 store。
- `ProviderAuthResolverTests` 中 models.json status 相关测试改为显式注入临时 `OAuthCredentialStore` 与 `ModelConfigurationStore`，不再通过进程级 `TAU_MODELS_FILE` 共享临时文件路径。
- 同步 parity matrix、100% active plan、architecture、quality 和 `next.md`，把 `packages/ai/src/providers/faux.ts` 从 missing/remaining gap 更新为 ported baseline，同时保留 standalone `pi-ai` bin、完整 TypeBox/AJV、incomplete JSON parser、option-level callbacks/signals、exact TypeScript export/subpath shape 和真实 provider/OAuth e2e 缺口。

## 设计意图

上游 Faux provider 是 `packages/ai` public test/provider surface 的一部分。Tau 之前已有 provider registry 与 stream facade，但缺少可由外部消费者直接注册、排队响应并稳定产出 stream events 的 public scripted provider。把 Faux 做成 `Tau.Ai.Providers.Faux` 的 .NET-native helper，可以让后续 Agent/CodingAgent/WebUi/Mom 的 provider-adjacent tests 避免各自重复造 fake stream provider。

`ProviderAuthResolver` 的 models.json status 检查原先直接创建默认 `ModelConfigurationStore`，测试只能通过进程环境变量指向临时 models.json。Windows/xUnit 并行验证下，这会把进程级状态和临时文件生命周期耦合在一起，容易在目录删除阶段遇到文件占用。改成构造期注入 store 后，生产默认行为不变，测试和未来宿主可以用明确 store 边界复用同一 resolver。

本切片不把 Faux 的所有 TypeScript option callback / `AbortSignal` / `onPayload` / `onResponse` 语义宣称完成；这些属于 shared `StreamOptions` / provider callback surface，仍按 matrix 保持后续 contract closure 项。

## 验证

- `dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`，通过 259/259。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore`，通过；计数为 `Tau.Ai.Tests` 259、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore -RunSmoke`，通过；计数同上，并覆盖 WebUi smoke 与 Mom `--once` smoke。

## 受影响文件

- `src/Tau.Ai/Providers/Faux/FauxProvider.cs`
- `tests/Tau.Ai.Tests/FauxProviderTests.cs`
- `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
- `src/Tau.Ai/Auth/ProviderAuthResolver.cs`
- `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
