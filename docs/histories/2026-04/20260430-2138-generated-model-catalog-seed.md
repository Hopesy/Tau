## [2026-04-30 21:38] | Task: Generated model catalog seed

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 pi-mono 到 Tau 的迁移，推进下一块 Tau.Ai：建立 generated models 管线，不再只靠手写 `BuiltInModels`。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` model registry、generated seed/generator、catalog merge、typed/default model 解析、Tau.Ai/Tau.CodingAgent/Tau.Mom 回归测试、迁移计划与仓库状态文档。

**Key Actions:**

* **[Generated seed]**: 新增 `src/Tau.Ai/Registry/generated-models.seed.json`，作为 Tau 自己的 generated model source of truth。
* **[Generator]**: 新增 `scripts/generate-tau-ai-models.ps1`，把 seed JSON 生成 `GeneratedBuiltInModels.g.cs`，不再只能手写 `BuiltInModels`。
* **[Catalog merge]**: `ModelCatalog` 现在会合并手写 `BuiltInModels` 和 generated catalog，并允许 generated 条目后写覆盖。
* **[Expanded models]**: 先导入 Azure Responses、OpenAI Codex、GitHub Copilot Responses、Google Gemini CLI、Google Antigravity 的一批新增模型，随后继续把 seed 扩到更接近上游的已支持模型族覆盖；当前 generated seed 共 66 个模型，范围仍只限 Tau 已支持 API。
* **[Regression tests]**: 补 generated catalog merge 与新增模型查询回归，让 registry 的扩展路径有固定门禁。
* **[Default selection]**: 继续把 provider/model 默认解析收口到 `ModelCatalog`，统一 default provider、default model、canonical `provider/model` 引用和冲突检测，并让 CodingAgent / WebUi / Mom 复用同一规则。
* **[Docs sync]**: 更新 `next.md`、architecture、quality score、baseline plan，并归档本切片 execution plan。

### 🧠 Design Intent (Why)

当前 Tau 最大的问题不是“能不能再手补几个模型”，而是模型表没有可再生成的接缝。先把 seed JSON 和 generator 落进仓库，意味着后续扩模型不再只能继续把 `BuiltInModels.cs` 变成越来越大的手工清单；同时这轮只导入当前 Tau 已支持 API 的模型，避免 generated catalog 比 provider 支持面跑得更快，制造“能查到但不能调用”的假能力。

### 🔬 Validation

* `powershell -ExecutionPolicy Bypass -File scripts/generate-tau-ai-models.ps1`
* `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore --verbosity minimal`
* `dotnet test tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj --no-build --verbosity minimal`
* 项目级顺序 build/test（本轮完成后再次执行）

### 📁 Files Modified

* `src/Tau.Ai/Registry/generated-models.seed.json`
* `scripts/generate-tau-ai-models.ps1`
* `src/Tau.Ai/Registry/GeneratedBuiltInModels.g.cs`
* `src/Tau.Ai/Registry/ModelCatalog.cs`
* `tests/Tau.Ai.Tests/ModelCatalogTests.cs`
* `docs/exec-plans/completed/2026-04-30-generated-model-catalog-seed.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
