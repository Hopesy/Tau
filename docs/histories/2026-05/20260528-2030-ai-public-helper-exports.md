# 变更历史：Tau.Ai public helper export baseline

## 用户诉求

持续执行 Tau 100% pi-mono parity 计划，接续 Phase 2 `AI public API/bin and helper export closure` 候选项，继续关闭不依赖外部凭证的 `packages/ai` 公共 helper 缺口。

## 本次变更

- 新增 `AiHeaderUtilities.ToDictionary(...)`，对齐上游 `utils/headers.ts` 的 `headersToRecord` 意图，提供 `HttpHeaders` / `HttpResponseMessage` 的 .NET-native header snapshot helper。
- 新增 `ShortHash.Compute(...)`，固定上游 `utils/hash.ts` 的短 hash 算法输出。
- 新增 `ContextOverflowDetector`，对照上游 `utils/overflow.ts` 固定 provider context-overflow pattern、known non-overflow exclusion 和 usage 超过 context window 的 silent overflow 判断。
- 新增 `JsonSchemaHelpers.StringEnum(...)`，对齐上游 `utils/typebox-helpers.ts` 的 provider-compatible string enum schema helper。
- `Tau.CodingAgent` 的 retry/overflow 分类改为复用 `Tau.Ai.ContextOverflowDetector`，避免 CodingAgent 私有 regex 与 AI-level helper 分叉。
- `AiPublicApiCompileSampleTests` 扩展到新增 public helper，新增 `AiUtilityHelpersTests` 和 `CodingAgentRetryClassifierTests`。
- 同步 parity matrix、100% active plan、architecture、quality 和 `next.md`，明确 standalone `pi-ai` bin、public faux provider、完整 TypeBox/AJV、incomplete JSON parser 和 exact TypeScript export shape 仍是剩余缺口。

## 后续收口

2026-05-28 22:41 后续 Phase 2 AI public provider/auth status seam 切片已补 `Tau.Ai.Providers.Faux` public scripted provider baseline，并把 `ProviderAuthResolver` 的 models.json status source 改为可注入 `ModelConfigurationStore`。本文件保留 20:30 helper export 切片当时的剩余缺口边界；当前 Faux provider 已不再是 AI public helper/export closure 的剩余缺口，最新验证记录见 `20260528-2241-ai-faux-provider-auth-status-seam.md`。

## 设计意图

上游 `packages/ai/src/index.ts` 会把多个 utility helper 作为包级 public surface 暴露；Tau 没有 TypeScript barrel/subpath export 形态，但公共 .NET 消费者仍需要稳定 helper 入口。本切片选择最容易形成 .NET-native 等价且不依赖外部服务的四类 helper，先把 headers/hash/overflow/string-enum schema 的公共合同纳入编译和单元测试。

`ContextOverflowDetector` 放在 `Tau.Ai` 而不是继续留在 `Tau.CodingAgent`，是因为 overflow pattern 属于 provider-family 行为判断；CodingAgent 只消费该判断决定走 compact-and-retry 还是普通 transient retry。

## 验证

- `dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --filter "AiUtilityHelpersTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal`，通过 17/17。
- `dotnet test tests\\Tau.CodingAgent.Tests\\Tau.CodingAgent.Tests.csproj --filter CodingAgentRetryClassifierTests --no-restore --verbosity minimal`，通过 2/2。

## 受影响文件

- `src/Tau.Ai/Utilities/AiHeaderUtilities.cs`
- `src/Tau.Ai/Utilities/ShortHash.cs`
- `src/Tau.Ai/Utilities/ContextOverflowDetector.cs`
- `src/Tau.Ai/Utilities/JsonSchemaHelpers.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentRetryOptions.cs`
- `tests/Tau.Ai.Tests/AiUtilityHelpersTests.cs`
- `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentRetryClassifierTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
