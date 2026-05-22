## [2026-05-21 09:00] | Task: CodingAgent RPC thinking controls baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` RPC host、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **RPC command handling**: 新增 `set_thinking_level` 与 `cycle_thinking_level`，让 `--mode rpc` headless client 可以调整 runner thinking level。
* **Settings persistence**: 两个 RPC 命令复用现有 settings `defaultThinkingLevel` 合同，保存时保留同一 settings 文件中的默认模型、tree filter、retry 和 enabledModels scope。
* **Safety guard**: active prompt 期间拒绝改 thinking level，避免运行中切换 reasoning 档位污染当前 turn。
* **Protocol shape**: `cycle_thinking_level` 到 off 时返回显式 `data: null`，对齐上游 RPC 响应形态，而不是让 null 被 JSON ignore policy 吃掉。
* **Tests and docs**: 补 targeted RPC tests，覆盖持久化、`get_state`、显式 `data:null`、无效 level 和 active prompt 拒绝；同步 README、architecture、quality score、next、release notes 和两份 active execution plans。

### 🧠 Design Intent (Why)

上游 RPC 已有 `set_thinking_level` / `cycle_thinking_level`，Tau 也已经有 CLI `/thinking`、runner `ThinkingLevel` 与 settings `defaultThinkingLevel`。这个切片选择复用同一事实源，先补 headless client 的 reasoning 档位控制，不引入第二套状态。

本切片刻意不扩大到 cycle model、retry/settings RPC、session switch、bash/extension UI 或完整 command provenance；这些仍需要各自的协议和安全边界。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
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
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，223/223。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 223、`Tau.Pods.Tests` 32。
* `git diff --check -- src\Tau.CodingAgent tests\Tau.CodingAgent.Tests README.md docs next.md`：通过，退出码 0；仅出现 CRLF normalization warnings。
