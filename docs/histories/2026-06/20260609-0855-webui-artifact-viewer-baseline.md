## [2026-06-09 08:55] | Task: WebUi artifact viewer baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续推进 Tau 对 `C:\Users\zhouh\Desktop\pi-mono-main` 的 100% parity 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.WebUi` artifact pane viewer baseline and WebUi browser tests.

**Key Actions:**

* **Artifact viewer routing**: 对照上游 `packages/web-ui/src/tools/artifacts/artifacts.ts` 的 artifact type dispatch，Tau 前端现在按 MIME / extension 分流 Markdown、SVG、image、PDF、text/code 和 generic files。
* **Specialized lightweight previews**: Markdown 复用现有 `renderText`，SVG 使用 sandboxed iframe preview + download，image / PDF / generic file 提供 data-url download，text/code 扩展名集合补齐到更接近上游 `TextArtifact` 覆盖面，generic fallback 显示不可预览提示。
* **Browser regression**: `WebUiBrowserFlowTests` 新增 Markdown/SVG/PDF/generic viewer flow；测试在写入 artifacts 后显式重选本测试创建的 session，再刷新 artifact pane，避免整套 browser tests 中后台 session load 竞态切走当前 session。
* **Docs sync**: 同步 active matrix、100% parity active plan、`next.md` 和 `docs/QUALITY_SCORE.md`，明确本轮只关闭 lightweight viewer baseline，不关闭 JS REPL、Docx/Excel rich viewers、pdfjs rich renderer、Lit component package、release/static smoke 或最终 WebUi verified 状态。

### 🧠 Design Intent (Why)

上游 WebUi artifact surface 不只是 HTML/text/image，还包含 Markdown、SVG、PDF、Text、Generic、Docx、Excel 等专用元素。Tau 已有 server-backed artifact store、HTML sandbox providers 和 artifact operation semantics，但前端 preview 仍把多数非 HTML/image artifact 落到普通 text/code 视图，导致用户可见 artifact pane 与上游差距明显。

本轮选择轻量 viewer baseline，而不是直接引入 pdfjs/docx-preview/xlsx 等依赖，是为了先关闭可本地验证的用户可见 routing/render/download 合同，同时保持简单、避免把 richer renderer 依赖和 release/static asset packaging 混进同一个提交。Docx/Excel rich viewer、pdfjs page renderer、view/code toggles 和 reusable Lit component package 继续作为后续 partial gaps 管理。

### 📁 Files Modified

* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`

### ✅ Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "ArtifactsPane_RendersSpecializedViewerBaselines|WebUiPageTests" --no-restore --verbosity minimal` -> 3/3 passed.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` -> 51/51 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter WebUiEndpointTests --no-restore --verbosity minimal` -> 11/11 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; project counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 456, `Tau.WebUi.Tests` 51, `Tau.Pods.Tests` 215.

### Remaining Gaps

* **partial**: `javascript_repl` tool and full LLM-facing artifact tool/message reconstruction are still not ported.
* **partial**: Docx/Excel rich viewers, pdfjs page renderer, view/code toggles and reusable Lit component package remain open.
* **external-e2e-needed**: WebUi release/static browser smoke remains open.
* **partial**: WebUi final `verified` status remains blocked by branch/tree true persistence, richer artifact runtime, release/static smoke and broader WebUi parity gaps.
