## [2026-05-03 22:30] | Task: powershell verify script

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续

### 🛠 Changes Overview

**Scope:** repository verification scripts and docs

**Key Actions:**

* **[Windows verify entry]**: 新增 `scripts/verify-dotnet.ps1`，按与 `verify-dotnet.sh` 相同的项目顺序执行 restore/build/test，并支持 `-SkipRestore`。
* **[Live validation]**: 实跑 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`，确认 source build、test build 和全部测试通过。
* **[Docs sync]**: 更新 `README.md`、`docs/ARCHITECTURE.md`、`docs/CICD.md`、execution plan，去掉已过期的 solution-build 失败描述，并补上 Windows 本机入口。

### 🧠 Design Intent (Why)

当前仓库标准入口仍保持 bash，但这台 Windows 机器的 `bash` 会落到 WSL 并失败于缺少 `/bin/bash`。继续让用户手工拼接等价 `dotnet build/test` 虽然可行，但不利于稳定协作和仓库知识收口。补一个明确的 PowerShell 入口，能把“本机验证现实”沉淀成仓库内可执行脚本，而不是停留在聊天说明里。

### 📁 Files Modified

* `scripts/verify-dotnet.ps1`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/CICD.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
