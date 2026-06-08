# [2026-06-08 11:38] | Task: CodingAgent file/image initial prompt baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell / .NET 10

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity 迁移。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **CLI argument normalization**: Added `CodingAgentCliArguments` to parse print/RPC/theme/context flags, plain CLI messages and `@file` arguments in one place instead of keeping scattered ad hoc parsing in `Program.cs`.
* **Initial prompt builder**: Added `CodingAgentInitialMessageBuilder` to merge redirected stdin, `@file` text content and the first CLI message. Text files are wrapped in `<file name="...">` markers; empty files are skipped.
* **Image attachment baseline**: Added PNG/JPEG/GIF/WebP header sniffing for `@file` images and convert accepted images into `ImageContent` attachments for runner content-block input. `images.blockImages` inserts a text notice instead of sending image bytes; oversized inline images are omitted with an explicit Tau resize-not-implemented note.
* **Runtime wiring**: Updated print mode and interactive startup to run image initial prompts through `ContentBlock` runner input. Non-RPC redirected stdin now enters print mode, blank stdin is ignored, and RPC mode rejects `@file` arguments so JSONL stdin remains protocol-only.
* **Regression coverage**: Added builder tests for option/file/message parsing, stdin/file/message ordering, PNG image content, image blocking and empty file skip; added print-mode coverage proving image prompts use `ContentBlock` input.
* **Docs sync**: Updated the parity matrix, active 100% plan, `next.md`, `docs/QUALITY_SCORE.md` and architecture notes to mark CLI `@file` initial input as partial baseline while preserving clipboard/resize/provider-vision gaps.

### Design Intent

Upstream builds the initial prompt from piped stdin, processed `@file` text/images and the first CLI message before entering print or interactive mode. Tau already had image-capable runner paths and `ReadFileTool` image output, but the top-level CLI could not ingest files or images into the initial user prompt. This slice keeps the change small and local to startup ingestion, while explicitly not claiming full clipboard or image pipeline parity.

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentPrintMode.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPrintModeTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/ARCHITECTURE.md`

### Upstream Reference

* `packages/coding-agent/src/cli/file-processor.ts`
* `packages/coding-agent/src/cli/initial-message.ts`
* `packages/coding-agent/src/main.ts`
* `packages/coding-agent/src/utils/mime.ts`
* `packages/coding-agent/src/core/tools/path-utils.ts`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentPrintModeTests" --no-restore --verbosity minimal` -> 11/11 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 445/445 passed.
* `git diff --check` -> passed with CRLF normalization warnings only.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed (`Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 445, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 215).

### Remaining Boundaries

* Clipboard image paste is not implemented in this slice.
* Photon resize/convert/EXIF orientation handling remains open.
* Real provider vision e2e was not executed.
* Full TUI attachment/editor ingestion and terminal image runtime wiring remain open.
