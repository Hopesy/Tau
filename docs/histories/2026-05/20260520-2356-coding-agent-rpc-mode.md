## [2026-05-20 23:56] | Task: 接入 CodingAgent RPC mode baseline

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows`

### 📥 User Query

> 继续推进 Tau.CodingAgent 与 pi-mono 的 parity 移植，接着当前 active plan 中未完成的 RPC mode 切片执行。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、`tests/Tau.CodingAgent.Tests`、`README.md`、`docs/exec-plans/active/`、`next.md`

**Key Actions:**

* **RPC host baseline**: 新增 `CodingAgentRpcHost`，用 LF-delimited JSONL 作为 stdin/stdout framing，避免把交互式 TUI 文本写入 headless stdout。
* **命令面接线**: 覆盖 `prompt`、`steer`、`follow_up`、`abort`、`new_session`、`get_state`、`set_model`、`get_available_models`、`compact`、`fork`、`clone`、`get_session_stats`、`get_messages` 和 `get_commands`。
* **生产入口**: `Program.cs` 新增 `--mode rpc` / `--mode=rpc` 检测；RPC 模式不创建交互式 editor，直接以 `Console.In` / `Console.Out` 启动 headless host。
* **测试覆盖**: 新增 RPC targeted tests，覆盖 prompt accepted response + agent event stream、LF JSONL framing、active prompt 下 steering/follow-up/abort、state/model/messages/commands 查询、compact flat/tree 持久化和 invalid JSON 错误响应。
* **文档同步**: README、ARCHITECTURE、QUALITY_SCORE、next 和 active plans 均写明这是 Tau-native RPC baseline，不等于完整上游 extension UI / bash / retry-settings / export_html / session switch parity。

### 🧠 Design Intent (Why)

上游 coding-agent 的 RPC mode 是 IDE / 扩展 / 外部进程嵌入入口，但完整协议包含 extension UI、bash、retry/settings、session switch 和 export_html 等多个子协议。Tau 当前已经有 runner、settings、flat session、JSONL tree 和 compaction seam，因此第一刀先做 headless JSONL baseline：保证外部进程能用稳定的逐行 JSON 驱动现有 runtime，并且 stdout 不被 welcome/status/TUI 渲染污染。

`prompt` 先返回 accepted response，再异步输出 agent events。这样调用端能立即知道命令被接收，同时仍能按 JSONL 流消费后续 `AgentEvent` / `StreamEvent`。`steer` 和 `follow_up` 复用上一切片暴露的 runner seam，只允许在 active prompt 期间调用；普通 `prompt` 在 active prompt 期间如果带 `streamingBehavior=steer|followUp` 也会转成对应运行中输入。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `next.md`

### ✅ Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：0 警告，0 错误
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：197/197 通过
* RPC smoke：`get_state` / `get_commands` 通过 `dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build -- --mode rpc` 返回逐行 JSON response，stdout 未出现交互式 welcome / prompt 文本
