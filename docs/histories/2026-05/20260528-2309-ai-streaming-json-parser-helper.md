## [2026-05-28 23:09] | Task: Tau.Ai streaming JSON parser helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### User Query

> 继续执行 `goal.md`，持续把 `pi-mono-main` 的能力 100% 可审计移植到 Tau。

### Changes Overview

**Scope:** `Tau.Ai` public helper surface, parity matrix, plan, next, quality and history.

**Key Actions:**

* **上游对照**: 读取 `packages/ai/src/utils/json-parse.ts`，确认上游 `parseStreamingJson` 是 public export，并被 OpenAI/Anthropic/Mistral/Bedrock 等 streaming tool-call argument path 使用。
* **行为校准**: 在临时目录用 `partial-json@0.1.7` 对照 complete JSON、invalid/empty fallback、incomplete object/array/string/literal/number 等边界输出。
* **代码收口**: 新增并校准 `StreamingJsonParser.ParseStreamingJson(...)` public helper baseline，覆盖 complete JSON fast path、空/非法输入 `{}` fallback、incomplete nested object/array/string/literal recovery、incomplete decimal drop 和 incomplete exponent base recovery。
* **测试覆盖**: 扩展 `AiUtilityHelpersTests` 和 `AiPublicApiCompileSampleTests`，固定外部消费者可调用的 public helper surface。
* **文档同步**: 更新 architecture、quality、100% plan、parity matrix 和 next，明确 public incomplete JSON parser baseline 已落地，同时保留 standalone `pi-ai` bin、完整 TypeBox/AJV、exact TypeScript export/subpath shape 和 provider-wide parser adoption 缺口。

### Design Intent (Why)

上游 `parseStreamingJson` 依赖 `partial-json` 在 streaming tool-call arguments 尚未完整闭合时尽量恢复可用对象。Tau 不引入额外运行时依赖，使用小型 .NET-native parser 固定当前需要的 public helper 合同，并用 `partial-json@0.1.7` 的实际输出校准容易漂移的 literal / number 边界。

### Files Modified

* `src/Tau.Ai/Utilities/StreamingJsonParser.cs`
* `tests/Tau.Ai.Tests/AiUtilityHelpersTests.cs`
* `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "AiUtilityHelpersTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal` - passed, 29/29.
