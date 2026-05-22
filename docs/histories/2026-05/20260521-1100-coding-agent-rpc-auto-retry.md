## [2026-05-21 11:00] | Task: CodingAgent RPC auto retry baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` RPC parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` RPC host、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **RPC command handling**: 新增 `set_auto_retry` 和 `abort_retry`，让 `--mode rpc` headless client 可以开关自动重试，并取消等待中的 retry delay。
* **Retry policy reuse**: `set_auto_retry` 复用现有 settings retry fields；开启时优先使用已配置 attempts/base delay，缺失时回到 Tau 默认 retry policy，关闭时写入 settings retry `0/0`。
* **Prompt retry parity**: RPC `prompt` 复用 host-level retry classifier、rollback snapshot、settings retry policy 和 JSONL retry audit；成功 retry 只持久化成功 attempt 的 messages，失败、耗尽或取消时不把失败输入落盘。
* **Cancellation boundary**: `abort_retry` 只取消 pending retry delay，不取消正在执行的模型请求，取消时写 `auto_retry_end(success=false, finalError="Retry cancelled")`。
* **State and docs**: `get_state` 增加 `autoRetryEnabled`；README、architecture、quality score、next、release notes 和两份 active execution plans 同步 RPC auto retry baseline 与剩余完整 RPC parity 边界。

### 🧠 Design Intent (Why)

上游 RPC 的 `set_auto_retry` / `abort_retry` 本质是委派给 session retry 开关和 retry abort。Tau 已经有 CLI `/retry`、host-level retryable error auto-retry、rollback snapshot 和 JSONL `auto_retry_start` / `auto_retry_end` audit，因此这个切片选择复用同一事实源，不在 RPC host 里新增第二套 retry settings store。

`abort_retry` 的边界刻意保持窄：它只取消等待中的 retry delay，不等同于 `abort` 当前 agent turn。这样 headless client 可以停止继续重试，但不会误杀已经发出的模型请求，也和上游 `abortRetry()` 的语义一致。

本切片不宣称完整 settings selector/UI、bash/abort_bash、session switch、extension UI、queue modes 或 full command provenance 已完成。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter FullyQualifiedName~CodingAgentRpcHostTests`：通过，19/19。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，230/230。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 230、`Tau.Pods.Tests` 32。
* `git diff --check`：通过，退出码 0；仅出现既有 CRLF normalization warnings。
