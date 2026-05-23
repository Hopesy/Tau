# CodingAgent theme selector baseline

## Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell, .NET 10

## User Query

> 继续继续，不要停；按 Tau 移植计划继续推进。

## Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs/history/release notes

**Key Actions:**

* **[Theme selector seam]**: 新增 `CodingAgentThemeSelector`，把 `CodingAgentThemeStatus` 转换成 `TuiSelectList`，并提供基于 `TuiSelectorSession` + `TuiAnsiRenderSurface` 的 console selector factory。
* **[Command wiring]**: `/theme` usage 扩展为 `/theme [current|list|select|set|clear] [name]`；`/theme select` 会通过可注入 selector 获取选择结果，校验后保存 canonical theme name。
* **[Runtime boundary]**: `CodingAgentHost` 接收 theme selector seam；生产入口只在真实交互式 editor 存在时注入 selector，print/RPC/redirected 路径不会混入 ANSI TUI 输出。
* **[Cancel behavior]**: selector 返回空值时输出 `theme selection cancelled`，不修改 settings，也不调用 runner。
* **[Tests/docs]**: 新增 router、host 和 selector helper 回归，并同步 README、架构、质量评分、next、active plans 和 release notes。

## Design Intent

上一切片已经把 `Tau.Tui` 的 selector foundation、selector session host 和 ANSI render surface 做成可测试库层。本切片选择 `/theme select` 作为第一条真实 CodingAgent selector 接线，因为 theme store/status/settings 合同已经稳定，且不会牵涉模型运行或 OAuth 副作用。这样可以先验证 command -> TUI selector -> settings persistence 的闭环，再继续接 settings/scoped-model/OAuth/resource selectors。

本切片没有把完整上游 theme rendering、theme file watcher、完整 TypeScript extension runtime 或完整 settings selector UI 写成完成。`/theme select` 只是 Tau-native theme selector baseline。

## Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentThemeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentThemeStoreTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`

## Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` - passed, 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` - passed, 261/261 tests.
* `git diff --check` completed with only existing CRLF normalization warnings.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed after the subsequent settings selector slice; repo-level test counts were `Tau.Ai.Tests` 194, `Tau.Agent.Tests` 58, `Tau.Tui.Tests` 75, `Tau.CodingAgent.Tests` 265, `Tau.Pods.Tests` 32.
