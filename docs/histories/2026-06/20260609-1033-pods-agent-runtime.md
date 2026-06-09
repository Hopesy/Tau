# [2026-06-09 10:33] | Task: Pods agent runtime baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell / .NET 10

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity 迁移。

### Changes Overview

**Scope:** `Tau.Pods`, adjacent `Tau.CodingAgent` CLI argument parsing, active parity docs/history sync.

**Key Actions:**

* **Pod agent runner**: Added `PodAgentRunner`, which creates a temporary Tau.Ai `models.json` provider for the configured pod vLLM endpoint and starts Tau.CodingAgent with `TAU_MODELS_FILE`, `TAU_PROVIDER`, `TAU_MODEL`, `--provider`, `--model` and `--system-prompt`.
* **CLI integration**: Changed top-level `agent [--config path] [--pod id] <name> [message/options...]` from stable not-implemented failure to an async process runner path while keeping the redacted upstream-style argument plan on stderr.
* **Secret boundary**: Temporary models config stores `PI_API_KEY` as an env var name or `dummy`; tests assert the literal env value is not written to stderr or config.
* **CodingAgent args**: Let `Tau.CodingAgent` parse and apply `--provider`, `--model` and `--system-prompt`; fixed `--json` as a boolean option so prompt text after it is preserved.
* **Regression coverage**: Added/updated Pods CLI tests for argv/env/config generation, temp config deletion, responses-model config shape, redaction and child exit-code propagation; updated CodingAgent initial-message parser tests for the new CLI options.
* **Docs sync**: Updated `GOAL.md`, active parity matrix/plan, `next.md` and `docs/QUALITY_SCORE.md` to mark the local pod-agent process runner baseline closed while keeping real pod prompt e2e and remote smoke gaps open.

### Design Intent

Upstream `packages/pods/src/commands/prompt.ts` currently constructs local coding-agent arguments and then throws `Not implemented`. Tau keeps that upstream fact explicit and does not claim exact upstream runtime parity. The implementation closes Tau's product-level runtime gap by routing the configured pod model through the existing Tau.Ai custom model config path and CodingAgent CLI, instead of inventing a separate provider stack inside `Tau.Pods`.

The runner preserves stdout/stderr ownership for the child process while writing audit plan and result summaries to stderr. This keeps `agent` usable for prompt output and still leaves a reviewable redacted execution record.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodAgentResults.cs`
* `src/Tau.Pods/Services/PodAgentRunner.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "Agent|PodsCli" --no-restore --verbosity minimal` -> 96/96 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter CodingAgentInitialMessageBuilderTests --no-restore --verbosity minimal` -> 6/6 passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 216/216 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 456/456 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed (`Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 456, `Tau.WebUi.Tests` 61, `Tau.Pods.Tests` 216).
* `git diff --check` -> passed with CRLF normalization warnings only.

### Remaining Boundaries

* No real pod endpoint prompt e2e was executed in this slice.
* No real remote SSH/HF download/setup/GPU/vLLM startup smoke was executed.
* Full upstream JSON output parity for pod-agent execution remains open.
* Multi-version rollback and long-running remote transport hardening remain open.
