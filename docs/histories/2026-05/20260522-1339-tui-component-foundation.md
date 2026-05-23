# Tau.Tui component/render/selector foundation

## Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell, .NET 10

## User Query

> 继续按照移植计划推进；下一步优先补 `Tau.Tui` 组件、渲染和 selector 基础层。

## Changes Overview

**Scope:** `Tau.Tui`, `Tau.Tui.Tests`, docs/history/release notes

**Key Actions:**

* **[TUI component contract]**: 新增 `ITuiComponent`、`ITuiInputComponent` 和 `TuiInputResult`，固定组件 render/input 最小合同。
* **[Basic components]**: 新增 `TuiContainer`、`TuiBox`、`TuiTextBlock`，提供纵向组件树、padding container 和文本块渲染。
* **[Text rendering helpers]**: 新增 `TuiText`，覆盖 visible width、ANSI escape 忽略、CJK/emoji 宽字符估算、截断、padding 和 word wrap。
* **[Selector foundation]**: 新增 `TuiSelectList`，支持过滤、选中态、描述列对齐、滚动提示和基础键盘交互。
* **[Diff plan]**: 新增 `TuiDiffRenderer`，以纯函数形式输出 full redraw 或 changed/cleared line operations。
* **[Selector session host]**: 新增 `ITuiRenderSurface`、`TuiOverlayHost` 和 `TuiSelectorSession`，固定 selector 打开、按键读取、input 分发、diff apply、选择/取消返回结果的可测试 loop。
* **[ANSI render sink]**: 新增 `TuiAnsiRenderSurface`，把 `TuiRenderDiff` 翻译为 synchronized ANSI output buffer；full redraw 清屏回 home 后写全量行，line diff 定位行、清行并写 replacement text。
* **[Tests/docs]**: 新增 `TuiComponentTests`，并同步 README、架构、质量评分、next、active plans 和 release notes。

## Design Intent

上游 settings/theme/scoped-model/thinking/OAuth/resource selector 都依赖同一类选择器、组件树和差分渲染能力。Tau 之前只有输入编辑器和 keybinding seam，继续在 `Tau.CodingAgent` 命令层手写 console UI 会让后续 selector 反复造轮子。本切片先把可测试的库内 foundation 固定下来，并在此基础上补一个单组件 selector session host，让后续 CodingAgent selector 能复用同一 render/input loop。随后补最小 ANSI diff sink，让 diff 能落到真实输出流。不直接接完整 CodingAgent selector UI，也不把 viewport/scrollback、overlay compositing、硬件 cursor、message/status area 和 theme rendering 混到同一个大改动里。

## Files Modified

* `src/Tau.Tui/Abstractions/ITuiComponent.cs`
* `src/Tau.Tui/Components/TuiContainer.cs`
* `src/Tau.Tui/Components/TuiBox.cs`
* `src/Tau.Tui/Components/TuiTextBlock.cs`
* `src/Tau.Tui/Components/TuiSelectList.cs`
* `src/Tau.Tui/Rendering/TuiText.cs`
* `src/Tau.Tui/Rendering/ITuiRenderSurface.cs`
* `src/Tau.Tui/Rendering/TuiDiffRenderer.cs`
* `src/Tau.Tui/Rendering/TuiAnsiRenderSurface.cs`
* `src/Tau.Tui/Runtime/TuiOverlayHost.cs`
* `src/Tau.Tui/Runtime/TuiSelectorSession.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `tests/Tau.Tui.Tests/TuiAnsiRenderSurfaceTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`

## Validation

* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` - passed, 0 warnings / 0 errors.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` - passed, 75/75 tests.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` - passed; test counts: Tau.Ai 194, Tau.Agent 58, Tau.Tui 75, Tau.CodingAgent 257, Tau.Pods 32.
* `git diff --check` - passed; only existing CRLF normalization warnings for docs files were reported.
