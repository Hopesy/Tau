## [2026-05-27 23:36] | Task: coding-agent read_file image parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> Continue the Tau pi-mono migration quickly, using parallel agents where useful and minimizing low-value documentation and tests.

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* Added image-file handling to `read_file`: PNG, JPEG, GIF and WebP are detected from magic headers, not file extensions.
* Image reads now return a text note plus an `ImageContent` base64 attachment, reusing Tau's existing image content path.
* Added a conservative inline image size guard: oversized image payloads now return a text notice and omit the `ImageContent` attachment.
* Kept text-file offset/limit/truncation behavior unchanged.
* Added focused `ReadFileToolTests` coverage for image-header detection, oversized-image omission and image-extension text fallback.

### Design Intent

The upstream read tool can pass images back to the model. Tau already had `ImageContent` support in providers, sessions and HTML export, but the local read tool only returned text. This slice closes that visible tool gap without adding image processing dependencies or claiming parity for auto-resize, terminal image rendering or syntax-highlight render details. The inline size guard keeps large images from being forwarded unbounded until the real resize pipeline is ported.

### Files Modified

* `src/Tau.CodingAgent/Tools/ReadFileTool.cs`
* `tests/Tau.CodingAgent.Tests/ReadFileToolTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
