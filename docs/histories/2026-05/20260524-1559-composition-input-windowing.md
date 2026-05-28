## [2026-05-24 15:59] | Task: Composition input windowing 收口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `model selector` 增加 composition selector delegate，`/model select`、交互式裸 `/model`、`Ctrl+L` 在 composition 主屏模式下复用当前 session surface。
* **Tau.Tui**: `TuiCompositionInteractiveRenderer` 的输入窗口从“按字符硬截”改成“按可见宽度滑窗”，光标位置和显示窗口基于 `TuiText.VisibleWidth(...)` 计算。
* **Tau.Tui**: reverse-search 也改走同一套宽度滑窗规则，长 match 或长 query 不再简单靠字符串截断决定可见窗口。

### Design Intent (Why)

高频 selector 主路径已经接入 composition 后，下一层最明显的粗糙点就是输入 overlay 的可见窗口。这里先把单行 prompt 和 reverse-search 的窗口逻辑改成宽度感知，至少保证长文本、宽字符和光标位置在 composition 主屏下不会再完全按 UTF-16 长度生硬裁切。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`
