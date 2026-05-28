## [2026-05-24 10:47] | Task: 并行推进移植 baseline

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 开始快速移植，不需要做单元测试，多 Agent 并行加速。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.CodingAgent`, `Tau.Tui`, `Tau.WebUi`, `Tau.Mom`, `Tau.Pods`

**Key Actions:**

* **Tau.Ai**: Bedrock SSO client registration 复用逻辑改为只复用未过期 registration；缺少过期时间但缺少 renewal 元数据时保留 legacy refresh fallback。
* **Tau.CodingAgent**: RPC `bash` 增加 `bash_event` / `bash_output` 流式 JSONL 事件，同时保留最终 `bash` result response；`get_state` 暴露 `isBashRunning`。
* **Tau.Tui**: 新增 `TuiCompositionHost`，把 transcript viewport 与 focused input overlay 组合到同一 surface，并把未消费键回退给 transcript 滚动；同时刷新动态可见 overlay 的焦点，避免已隐藏 overlay 继续吃键。
* **Tau.WebUi**: CodingAgent JSONL conservative import 会把来源 tree/audit 信息保存到 WebChat session `SourceMetadata`，不再只在响应里返回。
* **Tau.Mom**: 新增 `--validate-slack` preflight，验证 Slack token 配置、`auth.test` 与 `apps.connections.open`，不启动 worker。
* **Tau.Pods**: vLLM serve systemd/remote command 增加 Hugging Face cache snapshot discovery fallback，优先解析 `refs/main`，单 snapshot 时自动使用真实 snapshot path。

### Design Intent (Why)

本轮按移植速度优先策略推进互不重叠的真实 parity 缺口：worker 只碰各自模块，主控负责串行整合、build 验证和最小 history。没有把这些 seam 描述成完整 parity：真实 Slack smoke、真实 GPU/HF/vLLM smoke、完整 RPC terminal subsystem、完整 WebUi branch runtime 和完整 Tui terminal host 仍留给后续批次。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按用户要求未跑单元测试。

### Files Modified

* `src/Tau.Ai/Providers/Bedrock/BedrockSsoResolver.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentShellRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.Tui/Runtime/TuiCompositionHost.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptSession.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.Mom/MomCommandLine.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
