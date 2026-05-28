## [2026-05-26 15:30] | Task: TUI multiline input editor parity baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 继续下一轮 pi-mono -> Tau 移植；在 100% parity 前持续推进，不因低收益文档或单元测试拖慢速度。

### 🛠 Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **[Input editor]**: `InteractiveInputEditor` 支持多行 buffer baseline，Shift/Alt+Enter 插入换行，普通 Enter 仍提交；行尾反斜杠 + Enter 会删除反斜杠并插入换行，对齐上游 terminal fallback。
* **[Line-aware editing]**: Home/End、Ctrl-A/E、Ctrl-K/U 改为当前行语义；Backspace/Delete、Ctrl+Backspace/Ctrl+Delete、Ctrl+Left/Right 在换行边界可合并或跨越行；Up/Down 在多行 buffer 内优先纵向移动，避免误触 history。
* **[Renderer]**: `SystemConsoleInteractiveRenderer` 可按多行 buffer 重绘、清理旧行，并在提交/取消时把 cursor 放到输入块末尾。
* **[Tests]**: 补 targeted editor tests 覆盖多行提交、newline fallback、custom Enter disable、行内 Home/End、行合并、Up 多行移动和 Ctrl-K/U 当前行语义。
* **[Docs]**: `next.md` 更新 Tau.Tui 输入编辑器当前 parity 状态和剩余 editor 缺口。

### 🧠 Design Intent (Why)

上游 `packages/tui` 的完整 editor 很大，包含 box 渲染、sticky visual column、paste marker、undo/kill-ring 和 autocomplete。当前切片先把最影响真实输入体验的多行编辑语义落到现有 `IConsoleKeyReader` / `IInteractiveRenderer` 合同里，避免同时扩张 CodingAgent host 或 selector subsystem。

### 📁 Files Modified

* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `src/Tau.Tui/Runtime/SystemConsoleInputDevices.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `next.md`
