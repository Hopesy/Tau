## [2026-05-26 14:15] | Task: Tui fuzzy selector filtering

### Execution Context

* **Agent ID**: `Codex + worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户要求继续下一轮快速移植，默认多 Agent 并行推进真实 parity 缺口，少做低收益文档和单元测试。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **[Fuzzy foundation]**: 新增 `TuiFuzzyMatcher`，按上游 `pi-tui` fuzzy 规则实现字符顺序匹配、连续命中奖励、词边界奖励、gap/late match penalty、space-separated token all-match 和 alpha/numeric token swap。
* **[Single select]**: `TuiSelectList.SetFilter(...)` 的非空 filter 从 prefix match 升级为 fuzzy filter/sort；空 filter 仍恢复原始 item 顺序。
* **[Multi select]**: `TuiMultiSelectList` 的非空 filter 从 contains match 升级为 fuzzy filter/sort；空 filter 仍走既有 `BuildDisplayOrder()`，保留 selected-first / 当前显示顺序。
* **[Focused coverage]**: 新增 matcher/filter 单测，并扩展 selector component tests，固定 fuzzy 排序和空 query 顺序不漂移。
* **[Minimal docs]**: `next.md` 只同步 Tau.Tui selector foundation 行，记录 fuzzy filtering baseline。

### Design Intent (Why)

上游 selector/settings-list 使用 fuzzy 搜索而不是简单 prefix/contains。Tau 当前 selector 已经承载 model/theme/auth/settings 等交互入口，继续使用弱过滤会让模型和命令数量变大后的交互体验明显落后。该切片只在 Tui 库层补可测试 helper，并接入单选/多选列表，不触碰 CodingAgent 业务选择器和终端 host。

### Files Modified

* `src/Tau.Tui/Components/TuiFuzzyMatcher.cs`
* `src/Tau.Tui/Components/TuiSelectList.cs`
* `src/Tau.Tui/Components/TuiMultiSelectList.cs`
* `tests/Tau.Tui.Tests/TuiFuzzyMatcherTests.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `next.md`

### Validation

* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` -> 157/157 passed
