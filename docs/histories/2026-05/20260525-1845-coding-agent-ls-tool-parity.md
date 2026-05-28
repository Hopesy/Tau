## [2026-05-25 18:45] | Task: CodingAgent ls tool parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 用户持续要求快速推进 pi-mono -> Tau 移植，降低大段文档和低收益单元测试成本，按下一轮继续迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **[ls tool parity]**: `ListDirectoryTool` 对齐上游 `ls.ts` 的核心输出合同：新增 `limit` 参数，默认 500；合并目录和文件统一排序；目录只追加 `/`；包含 dotfile；不再输出 `[DIR]` 前缀和文件大小。
* **[error/empty output parity]**: 缺失路径返回 `Path not found`，文件路径返回 `Not a directory`，空目录返回 `(empty directory)`，limit 截断时给出 `Use limit=<n>` 续查提示。
* **[focused coverage]**: 新增 `ListDirectoryToolTests` 覆盖排序/格式、dotfile、limit 截断、空目录、文件路径和缺失路径错误。
* **[minimal docs]**: 只同步 `next.md`、CodingAgent parity plan、总 plan 决策和本 history；未扩写 README/ARCHITECTURE/QUALITY。

### 🧠 Design Intent (Why)

上游 `ls` tool 的目录输出是模型判断项目结构的基础输入。Tau 旧实现带 `[DIR]` 前缀、文件大小和目录/文件分组，并硬编码目录 200 / 文件 300 上限，会让工具输出和上游行为漂移。本切片先固定用户可见的 entry format、排序、limit 和错误文本；上游 64KB byte truncation / render details 牵涉通用 tool output 截断和渲染层，保留为后续独立切片，避免扩大当前迁移面。

### 📁 Files Modified

* `src/Tau.CodingAgent/Tools/ListDirectoryTool.cs`
* `tests/Tau.CodingAgent.Tests/ListDirectoryToolTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter FullyQualifiedName~ListDirectoryToolTests --verbosity minimal` -> 5/5 passed
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
