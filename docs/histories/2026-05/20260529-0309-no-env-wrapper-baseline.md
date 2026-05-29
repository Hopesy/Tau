## [2026-05-29 03:09] | Task: No-env wrapper baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的 100% 可审计移植。

### Changes Overview

**Scope:** `scripts` / release parity docs

**Key Actions:**

* 新增 `scripts/invoke-no-env.ps1`，提供 PowerShell-first 子进程 no-env runner。
* 新增 `scripts/verify-no-env.ps1`，在临时 Tau 状态目录里运行项目验证，并可用 `-RunSmoke` 额外验证 `tau-ai list` 和 CodingAgent RPC `get_state`。
* no-env runner 清除 OpenAI、Anthropic、Google/Gemini/Vertex、Azure OpenAI、AWS/Bedrock、HF、OpenAI-compatible aliases、Slack 和 Tau auth/config/session/log 相关环境变量，只对子进程生效。
* no-env runner 设置 `PI_NO_LOCAL_LLM=1` 与 `TAU_NO_LOCAL_LLM=1`，并可把 `TAU_AUTH_FILE`、`TAU_MODELS_FILE`、CodingAgent session/settings/history/keybindings 和 `TAU_LOG_FILE` 指到临时目录。
* no-env runner 对 stdin input file 使用 shell file redirection，避免 Windows PowerShell / .NET `StandardInput` 写入 UTF-8 BOM 后破坏 CodingAgent RPC JSONL。
* 同步 README、active parity matrix、100% parity plan、`next.md`、`docs/QUALITY_SCORE.md` 和 release artifact manifest gap 文案。

### Design Intent (Why)

上游 root `test.sh` 和 `pi-test.sh --no-env` 的关键价值是避免测试/CLI smoke 被本机 provider 凭证、AWS/Azure/HF 环境变量或本地 auth 文件污染。Tau 在 Windows 本机已经以 PowerShell 验证链为权威入口，因此本轮先建立可复用的子进程级 no-env 隔离，而不是移动用户真实 auth 文件。这样验证可以稳定复现“无凭证环境下仍能 build/test 和跑无 provider 调用 smoke”，同时不破坏用户本机的实际 auth store。

### Files Modified

* `scripts/invoke-no-env.ps1`
* `scripts/verify-no-env.ps1`
* `scripts/build-release-artifacts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Verification

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-no-env.ps1 -DryRun -FilePath powershell -ArgumentListBase64 <redacted>` passed. Dry run listed only environment variable names and did not print secret values.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-no-env.ps1 -FilePath powershell -ArgumentListBase64 <redacted>` passed and confirmed a removed provider env variable was absent in the child process.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore` passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore -RunSmoke` passed. It ran the same project gate with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44 and `Tau.Pods.Tests` 166, then covered existing `tau-ai` / WebUi / Mom smoke plus no-env `tau-ai list` and CodingAgent RPC `get_state`.
