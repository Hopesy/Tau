## [2026-06-09 00:12] | Task: WebUi sandbox runtime providers

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### 📥 User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity 主线，从当前 active matrix 的 WebUi sandbox/artifact runtime bridge 缺口继续推进。

### 🛠 Changes Overview

**Scope:** `Tau.WebUi` / `Tau.WebUi.Tests` / WebUi parity docs

**Key Actions:**

* **[WebUi sandbox helpers]**: HTML artifact iframe 现在从当前 session 的 user messages 收集只读 attachment snapshot，并注入 `listAttachments()`、`readTextAttachment(id)`、`readBinaryAttachment(id)`。
* **[File-return runtime]**: 新增 `returnDownloadableFile(fileName, content, mimeType?)` helper，支持 string、object、Blob 和 Uint8Array；binary 内容先转成 JSON-safe base64 string，再通过 runtime bridge 发送 `file-returned`。
* **[Runtime contract]**: `WebRuntimeMessageRequest.Filename` 改为 `[JsonPropertyName("fileName")]`，对齐上游 `FileDownloadRuntimeProvider` 的 `fileName` casing，同时继续兼容 Tau 旧 `filename` 输入。
* **[One-time semantics]**: 服务端 `file-returned` 只返回 `fileName` / `mimeType` metadata，不写入 `WebArtifactStore`，避免把上游一次性下载语义误并入 artifacts。
* **[Tests]**: WebUi page/static test 固定 helper 注入；endpoint test 固定 `file-returned` metadata 和非持久化；新增 Chromium browser flow，验证 HTML artifact iframe 能读取 session attachment 并调用 `returnDownloadableFile`。
* **[Docs]**: 同步 active matrix、100% active plan、`next.md` 和 `docs/QUALITY_SCORE.md`，把 attachments/file-return provider baseline 标为本地已覆盖，同时保留 JS REPL、artifact tool/renderers、release/static browser smoke 等 open gaps。

### 🧠 Design Intent (Why)

上游 WebUi 的 `AttachmentsRuntimeProvider` 是只读 snapshot provider，不需要 runtime messaging；`FileDownloadRuntimeProvider` 是一次性 file-return/download provider，明确不让返回文件成为后续 LLM 可访问 artifact。本次实现按这个语义边界扩展 Tau 已有 HTML artifact iframe runtime bridge：把 attachments 暴露给 sandbox，把 file-return 作为 metadata message 处理，不扩大 artifact persistence schema。

### ✅ Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "WebUiPageTests|ArtifactEndpoints" --no-restore --verbosity minimal`：4/4 通过。
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "HtmlArtifact_CanReadSessionAttachmentsAndReturnDownloadableFile" --no-restore --verbosity minimal`：1/1 通过。
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`：49/49 通过。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter WebUiEndpointTests --no-restore --verbosity minimal`：11/11 通过。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 119、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 456、`Tau.WebUi.Tests` 49、`Tau.Pods.Tests` 215。

### 📁 Files Modified

* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebArtifactService.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
