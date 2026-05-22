## [2026-05-20 13:00] | Task: 添加 Thinking Level 用户控制

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows`

### 📥 User Query

> 继续推进 Tau.CodingAgent 与 pi-mono 的 parity 移植，接着当前 active plan 中未完成的切片执行。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、`tests/Tau.CodingAgent.Tests`、`docs/exec-plans/active/`

**Key Actions:**

* **命令面接线**: 新增 `/thinking [current|cycle|off|minimal|low|medium|high|xhigh]`，用于查看、设置、循环或关闭当前 runner 的 reasoning/thinking level。
* **runtime 状态接线**: `ICodingAgentRunner` 暴露 `ThinkingLevel`，`RuntimeCodingAgentRunner` 把该值写入 `SimpleStreamOptions.Reasoning`，让后续普通 turn 使用当前 thinking level。
* **settings 持久化**: `CodingAgentSettingsStore` 新增 `DefaultThinkingLevel` 字段，启动入口会把 settings 中的默认 thinking level 恢复到 runner；`/thinking` 修改会保留既有 provider/model/tree/retry 设置。
* **测试覆盖**: 更新 `/help` 预期并补充 `/thinking` targeted tests，覆盖默认 off、显式 high、cycle 顺序、off 清空 runtime/settings、非法参数 usage 和 settings round-trip。

### 🧠 Design Intent (Why)

上游 coding-agent 已把 thinking/reasoning 作为用户可调能力。Tau provider 层已经具备 `ThinkingLevel` 与 `SimpleStreamOptions.Reasoning`，所以这一切片不需要等待 TUI selector 或 settings UI；先把稳定的 slash command 与 settings 持久化落地，就能让脚本和交互 CLI 在支持 reasoning 的模型上立即切换档位。

`off` 用 `null` 表示，避免给现有 `ThinkingLevel` enum 增加兼容性成本；settings 写入字符串而不是 enum 值，减少后续 enum 命名变化对旧配置文件的破坏。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### ✅ Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：0 警告，0 错误
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：189/189 通过
* 真实 CLI smoke：临时 `TAU_CODING_AGENT_SETTINGS_FILE` 下执行 `/thinking`、`/thinking high`、`/thinking cycle`、`/thinking off`、`/quit`，控制台输出依次显示 `off/high/xhigh/off`，退出码 0
