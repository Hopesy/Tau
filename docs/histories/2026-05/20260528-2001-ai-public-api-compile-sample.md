# 变更历史：Tau.Ai public API compile sample baseline

## 用户诉求

持续执行 Tau 100% pi-mono parity 计划，接续 Phase 2 `AI public API/bin and helper export closure` 候选项，优先关闭不依赖外部凭证的公共 API 可消费性缺口。

## 本次变更

- 新增 `AiPublicApiCompileSampleTests`，用外部消费者样例编译并运行 `Tau.Ai` 当前公共入口。
- 样例覆盖 message/content/tool/model/options/usage、`ProviderRegistry`、`StreamFunctions`、`AssistantMessageStream` / stream event/result、`ModelCatalog` cost/model helper、`ProviderAuthResolver`、OAuth registry 入口和 `ToolArgumentValidator`。
- 同步 parity matrix、100% active plan、quality 和 `next.md`，把该切片记录为 `.NET public API compile-sample baseline`，同时保留 standalone `pi-ai` bin、TypeBox/faux/helper export 等无直接 .NET 映射项的剩余缺口。

## 设计意图

上游 `packages/ai/src/index.ts` 和 `package.json` 通过 TypeScript barrel/subpath exports 暴露 API；Tau 没有同形 package barrel，公共形状应通过 .NET namespace 和 assembly surface 固定。这个测试把 Tau 当前真实可消费的 public surface 纳入编译与运行回归，避免后续 provider/helper 重构破坏外部消费者入口。

本切片没有实现 standalone `pi-ai login/list`，也没有把 TypeBox、faux provider、overflow、headers/hash 等 helper exports 宣称完成。这些仍按 matrix 保持 `partial` / `missing`，后续需要实现 Tau-native 等价物或形成明确非目标决策。

## 后续收口

2026-05-28 22:41 后续 Phase 2 AI helper/provider 切片已补 `AiHeaderUtilities`、`ShortHash`、`ContextOverflowDetector`、`JsonSchemaHelpers.StringEnum` 和 `Tau.Ai.Providers.Faux` public scripted provider baseline，并把 `ProviderAuthResolver` 的 models.json status source 改为可注入 `ModelConfigurationStore`。本文件保留 20:01 compile-sample 切片当时的范围与验证计数；当前最新 AI provider/auth seam 验证记录见 `20260528-2241-ai-faux-provider-auth-status-seam.md`。

## 验证

- `dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --filter AiPublicApiCompileSampleTests --no-restore --verbosity minimal`，通过 1/1。
- `dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`，通过 226/226。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore`，通过；计数为 `Tau.Ai.Tests` 226、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 433、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。

## 受影响文件

- `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
