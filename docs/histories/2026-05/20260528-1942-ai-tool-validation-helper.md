# 变更历史：AI tool validation helper baseline

## 用户诉求

持续执行 Tau 100% pi-mono parity 计划，接续 Phase 2 候选队列，推进 AI public API / helper export closure。

## 本次变更

- 将原本位于 `Tau.Agent` 内部的 `ToolArgumentValidator` 上移到 `Tau.Ai`，作为公共 `ToolArgumentValidator` helper 暴露。
- 新增公共入口 `ValidateToolCall(...)` / `ValidateToolArguments(...)`，对齐上游 `packages/ai/src/utils/validation.ts` 的核心职责：根据工具 schema 校验并返回可能被 coercion 后的参数。
- `Tau.Agent.Runtime.ToolExecutor` 改为复用 `Tau.Ai.ToolArgumentValidator`，继续保持 prepare / schema validation / error-as-tool-result 的既有 Agent loop 行为。
- 新增 `ToolArgumentValidatorTests`，覆盖 tool lookup、required/properties、array items、number/integer/boolean/string coercion、enum 和格式化错误消息。
- 同步 parity matrix、quality 和 next，把 AI-level validation helper 从“只在 Agent 内部实现、未公开”更新为“公共 baseline 已有，完整 AJV/TypeBox keyword parity 仍待后续补齐”。

## 设计意图

上游 `utils/validation.ts` 属于 `pi-ai`，不是 Agent 私有实现。Tau 之前把 schema validation 放在 Agent 内部，能保护 runtime loop，但不能满足 AI package helper/public API parity。把验证器上移到 `Tau.Ai` 后，Agent 和未来其它宿主可以共用同一 schema validation 语义，避免后续在 CodingAgent/WebUi/Mom 或测试 helper 里重复造一套不一致的参数校验。

当前实现故意只标为 baseline：它覆盖 Tau 已消费的 JSON schema 子集和 AJV `coerceTypes` 风格的主要 coercion，但没有宣称完整 AJV/TypeBox keyword 覆盖，也没有照搬浏览器 CSP/runtime-codegen fallback，因为这些语义在 .NET 中需要单独决策。

## 验证

- `dotnet test tests\\Tau.Agent.Tests\\Tau.Agent.Tests.csproj --filter AgentRuntimeContractTests --no-restore --verbosity minimal`，通过 15/15。
- 首次并行跑 `Tau.Ai.Tests` 与 `Tau.Agent.Tests` 时，`Tau.Ai` 编译命中 `VBCSCompiler` 对 `src\\Tau.Ai\\obj\\Debug\\net10.0\\Tau.Ai.dll` 的文件占用；这是并发验证写同一 obj 的本机锁，不是代码失败。
- `dotnet build-server shutdown; dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --filter ToolArgumentValidatorTests --no-restore --verbosity minimal`，通过 4/4。
- 接手复核后串行追加验证：`dotnet test tests\\Tau.Ai.Tests\\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`，通过 225/225。
- 接手复核后串行追加验证：`dotnet test tests\\Tau.Agent.Tests\\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`，通过 115/115。
- 接手复核后项目级验证：`powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore`，通过；计数为 `Tau.Ai.Tests` 225、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 433、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
- 接手复核后运行态验证：`powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore -RunSmoke`，通过；覆盖同一测试计数、WebUi smoke 和 Mom `--once` smoke。
- `git diff --check` 通过，仅保留既有 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md` CRLF 归一化 warning。

## 受影响文件

- `src/Tau.Ai/Validation/ToolArgumentValidator.cs`
- `src/Tau.Agent/Runtime/ToolExecutor.cs`
- `tests/Tau.Ai.Tests/ToolArgumentValidatorTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
