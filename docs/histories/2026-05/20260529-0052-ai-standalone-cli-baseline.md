## [2026-05-29 00:52] | Task: AI standalone CLI baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的 100% 可审计移植。

### Changes Overview

**Scope:** `Tau.Ai.Cli` / `Tau.Ai.Tests` / parity docs

**Key Actions:**

* 新增 `src/Tau.Ai.Cli` executable baseline，对照上游 `packages/ai/src/cli.ts` 实现 `help`、`list`、`login [provider]` 和交互式 provider selection。
* CLI 默认写 Tau auth store（`TAU_AUTH_FILE`、`./.tau/auth.json`、`~/.tau/auth.json`），同时提供 `--auth-file auth.json` 入口显式复刻上游 cwd `auth.json` 写入语义。
* 新增 `AiCliRunnerTests` 覆盖 help/list、unknown provider、显式 auth file、交互选择和默认 auth store；把新项目纳入 `Tau.slnx` 与 `scripts/verify-dotnet.ps1`。
* 将根 `auth.json` 加入 `.gitignore`，避免兼容测试路径生成的本地 credential store 入仓。
* 同步 active parity matrix、100% parity plan、`next.md`、`docs/QUALITY_SCORE.md`、`docs/ARCHITECTURE.md` 和 `docs/SECURITY.md`，明确本切片不声明真实 OAuth e2e、发布层 `pi-ai` alias、TypeBox/AJV 或 exact TypeScript export/subpath shape 完成。

### Design Intent (Why)

Phase 2 matrix 将 `packages/ai/src/cli.ts` / package `bin.pi-ai` 标为 AI public API/bin 缺口。Tau 现有 `/login` 只挂在 `Tau.CodingAgent`，不能作为 `pi-ai login/list` 的 package-level 等价入口。新增独立 `Tau.Ai.Cli` 可以把 OAuth registry、credential store 和 provider list 暴露到 AI package 边界，同时保留 Tau 默认更安全的 `.tau/auth.json` 路径；`--auth-file auth.json` 用于需要逐项对照上游 cwd auth 行为的测试或兼容场景。

### Files Modified

* `src/Tau.Ai.Cli/Tau.Ai.Cli.csproj`
* `src/Tau.Ai.Cli/Program.cs`
* `src/Tau.Ai.Cli/AiCliRunner.cs`
* `tests/Tau.Ai.Tests/AiCliRunnerTests.cs`
* `tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj`
* `Tau.slnx`
* `scripts/verify-dotnet.ps1`
* `.gitignore`
* `docs/ARCHITECTURE.md`
* `docs/SECURITY.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Verification

* `dotnet build src\Tau.Ai.Cli\Tau.Ai.Cli.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --filter AiCliRunnerTests --verbosity minimal` passed 7/7 after correcting the test to expect the persisted OAuth `expiresAt` field while still proving reserved metadata fields such as `access` are filtered.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed 280/280.
* `dotnet run --project src\Tau.Ai.Cli\Tau.Ai.Cli.csproj --no-build -- list` exited 0 and listed 5 built-in OAuth providers.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed the same build/test gate plus `tau-ai list`, WebUi, and Mom `--once` smoke.
* `git diff --check` exited 0; output only contained CRLF normalization warnings.
