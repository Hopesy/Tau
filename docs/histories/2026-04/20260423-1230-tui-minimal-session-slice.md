## [2026-04-23 12:30] | Task: 实现最小 TUI 会话层

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 下一步

### 🛠 Changes Overview

**Scope:** `src/Tau.Tui`, `src/Tau.CodingAgent`, `docs/histories`

**Key Actions:**

* **[建立最小终端抽象]**: 新增 `ITerminal` 和 `SystemConsoleTerminal`，把控制台读写从 `Tau.CodingAgent` 主程序里抽离到 `Tau.Tui`。
* **[建立最小会话层]**: 新增 `InteractiveConsoleSession`，统一管理欢迎语、输入提示、流式文本、thinking、工具状态、错误和退出输出。
* **[抽离可测试宿主]**: 新增 `CodingAgentHost`、`ICodingAgentRunner` 和 `RuntimeCodingAgentRunner`，把交互宿主、runtime 装配和入口解耦。
* **[接入 CodingAgent]**: 将 `Tau.CodingAgent/Program.cs` 改为通过 `Tau.Tui` 驱动交互流程，不再直接使用裸 `Console.ReadLine()` / `Console.WriteLine()` 组织主交互逻辑。
* **[补 smoke 测试]**: 为 `Tau.Tui` 和 `Tau.CodingAgent` 新增最小行为测试，验证欢迎语、工具状态输出、错误路径和退出路径。
* **[接入主 CI]**: 更新 `.github/workflows/ci.yml`，将 `dotnet build` 与逐项目 `dotnet test --no-build --no-restore` 纳入主线。
* **[补消息历史基线]**: 为 `InteractiveConsoleSession` 新增 `TranscriptEntry` 和 `InputBuffer`，把 `you / tau / thinking / tool / error` 的最小语义与输入提交边界固定下来。

### 🧠 Design Intent (Why)

当前主计划的下一步是把 `Tau.CodingAgent` 从 demo 状态拉成 CLI-first 的真实 P0 路径。这一轮没有急着上复杂 TUI 组件，而是先建立最小但清晰的终端抽象、会话层和可测试宿主，再把 smoke 测试与 CI 一起补进来，先形成“代码 + 验证 + 门禁”的最小闭环。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.Tui/Abstractions/ITerminal.cs`
* `src/Tau.Tui/Runtime/InputBuffer.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.Tui/Runtime/SystemConsoleTerminal.cs`
* `src/Tau.Tui/Runtime/TranscriptEntry.cs`
* `.github/workflows/ci.yml`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/FakeTerminal.cs`
* `tests/Tau.Tui.Tests/FakeTerminal.cs`
* `tests/Tau.Tui.Tests/InteractiveConsoleSessionTests.cs`
* `docs/histories/2026-04/20260423-1230-tui-minimal-session-slice.md`
