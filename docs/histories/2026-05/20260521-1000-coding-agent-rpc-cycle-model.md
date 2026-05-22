## [2026-05-21 10:00] | Task: CodingAgent RPC cycle model baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` RPC parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` RPC host、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **RPC command handling**: 新增 `cycle_model`，让 `--mode rpc` headless client 可以切换到下一个可用模型。
* **Scoped model contract**: 复用 settings `enabledModels` 有序 scope；scope 缺失或为空时使用全部可用模型。
* **Settings persistence**: 切换成功后保存默认 provider/model，并同步 flat session 与 JSONL tree session。
* **Protocol shape**: 候选模型不足两个时返回显式 `data: null`，避免 JSON ignore policy 吃掉 null 响应。
* **Safety guard**: active prompt 期间拒绝切换模型，保持和 `set_model` / thinking controls / session utility RPC 一致的运行中保护。
* **Tests and docs**: 补 3 个 targeted RPC tests，覆盖 scoped cycle、默认模型持久化、`thinkingLevel/isScoped` 响应、单模型 explicit null 和 active prompt 拒绝；同步 README、architecture、quality score、next、release notes 和两份 active execution plans。

### 🧠 Design Intent (Why)

上游 RPC 已有 `cycle_model`，Tau 也已经有 `/model`、`/scoped-models`、runner `SelectModel()` 与 settings `enabledModels` 合同。这个切片选择复用同一事实源，先补 headless client 的 next-model 切换能力，不扩 runner interface，也不在 RPC host 里发明第二套 scope 存储。

settings scope 中无效或漂移的模型引用会被跳过；如果显式 scope 全部无效，RPC 回退到全部可用模型，避免坏 settings 让 headless client 卡死。`cycle_model` 只报告当前 `thinkingLevel`，不在模型切换中改变 reasoning 档位。

本切片刻意不扩大到 retry/settings RPC、session switch、bash/extension UI、queue modes 或 full command provenance；这些仍需要各自的协议和安全边界。

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
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，226/226。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 226、`Tau.Pods.Tests` 32。
* `git diff --check -- src\Tau.CodingAgent tests\Tau.CodingAgent.Tests README.md docs next.md`：通过，退出码 0；仅出现 CRLF normalization warnings。
