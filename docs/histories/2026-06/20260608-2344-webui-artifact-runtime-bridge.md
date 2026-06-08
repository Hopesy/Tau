## [2026-06-08 23:44] | Task: WebUi artifact runtime bridge baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### 🛠 Changes Overview

**Scope:** `Tau.WebUi` / `tests/Tau.WebUi.Tests` / `tests/Tau.CodingAgent.Tests` WebUi fixture / parity docs

**Key Actions:**

* **Artifact state baseline**: 新增 per-session `WebArtifactStore` / `WebArtifactService`，默认把 artifact state 持久化到 `output/webui-artifacts.json`。
* **HTTP/runtime contract**: 新增 artifact CRUD endpoints 和 `/api/sessions/{id}/runtime/messages`，固定 `artifact-operation list/get/createOrUpdate/delete`、console、file-returned、execution complete/error 的 runtime response baseline。
* **Browser surface**: 前端新增 artifact split pane、refresh、text/image/html preview，并给 HTML artifact iframe 注入 `listArtifacts` / `getArtifact` / `createOrUpdateArtifact` / `deleteArtifact` helper。
* **Cleanup contract**: 删除 WebUi session 时同步删除该 session 的 artifact state，避免 orphan artifact 持久化。
* **Tests/docs**: 补 artifact endpoint、runtime message、unsafe filename/session cleanup、browser artifact pane 和 HTML runtime bridge 静态回归；同步 `tests/Tau.CodingAgent.Tests` 里的 shared WebUi endpoint fixture 注册，避免新增 route dependency 导致旧 endpoint tests 500；同步 matrix、active plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`。

### 🧠 Design Intent (Why)

上游 `packages/web-ui` 的 artifact/sandbox 能力不是单一 UI 面板，而是 artifact state、sandbox runtime provider、iframe message bridge 和 tool/renderer 的组合。Tau 这轮先关闭低歧义、可本地验证的 server-backed artifact store + iframe runtime bridge baseline，让 WebUi 能在真实 ASP.NET host 里持久化、预览并让 HTML artifact 读写同 session artifacts。

本切片没有把当前 baseline 误标为完整上游 parity：`javascript_repl` tool、attachments runtime provider、file-return download provider、artifact logs、full artifact tool update/rewrite semantics、specialized Docx/Excel/Pdf/Svg/Markdown/Text viewers、reusable Lit component package 和 release/static browser smoke 仍保持 open。

### ✅ Validation

* `git diff --check` 通过；仅报告既有 CRLF 规范化 warning，无 whitespace error。
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` 通过，48/48。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter WebUiEndpointTests --no-restore --verbosity minimal` 通过，11/11。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过；计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 119、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 456、`Tau.WebUi.Tests` 48、`Tau.Pods.Tests` 215。

### 📁 Files Modified

* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Program.cs`
* `src/Tau.WebUi/Services/WebArtifactService.cs`
* `src/Tau.WebUi/Services/WebArtifactStore.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/Services/WebUiOptions.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFixture.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
