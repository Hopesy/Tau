## [2026-06-09 15:53] | Task: CodingAgent image resize / EXIF baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell, .NET 10

### User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，领取一个可验证切片并保持文档/history/验证闭环。

### Changes Overview

**Scope:** `Tau.CodingAgent` image ingestion and read tool.

**Key Actions:**

* 新增 `CodingAgentImagePreprocessor`，让 CLI `@file` 与 `read_file` 共用图片预处理合同。
* 对照上游 image pipeline，覆盖本地 PNG resize 到 2000x2000 / 4.5MiB base64 headroom、尺寸说明和 JPEG/WebP EXIF orientation 尺寸解析。
* `ReadFileTool` 启动时消费 `images.autoResize` settings，并把 resized/dimensions metadata 写入 `ReadFileToolDetails` 和 HTML transcript metadata。
* 新增 image preprocessor、initial message builder 和 read_file targeted tests。
* 同步 `GOAL.md`、active parity plan/matrix、`next.md`、`docs/QUALITY_SCORE.md` 中的当前状态和剩余边界。

### Design Intent

上游 `file-processor.ts` 和 `read.ts` 都通过 `resizeImage()` 在图片进入模型前处理尺寸、inline payload 大小和坐标映射说明。Tau 之前只做 MIME sniff 与大小拒绝，并在超限时提示自动 resize 未实现。本次改动先关闭无外部依赖、可本地验证的 PNG resize / EXIF dimension metadata baseline，并明确保留 full Photon JPEG/GIF/WebP resize/convert、clipboard image paste、provider vision e2e 和完整 TUI attachment/editor path 作为后续缺口。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ImagePreprocessor|InitialMessageBuilder|ReadFileTool" --no-restore --verbosity minimal`：24/24 passed。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：489/489 passed。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 280、Agent 120、Tui 251、CodingAgent 489、WebUi 61、Pods 216）。
* `git diff --check`：passed，仅保留既有文档 CRLF/LF 规范化 warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentImagePreprocessor.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `src/Tau.CodingAgent/Tools/ReadFileTool.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentImagePreprocessorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/ReadFileToolTests.cs`
* `tests/Tau.CodingAgent.Tests/ImageTestData.cs`
* `GOAL.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
