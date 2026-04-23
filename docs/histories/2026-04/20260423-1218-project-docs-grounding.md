## [2026-04-23 12:18] | Task: 收口项目文档

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 根据我们的目标按照 agents.md 的指引将真实项目填充到文档中，尤其是计划文档。

### 🛠 Changes Overview

**Scope:** `docs/`, `README.md`

**Key Actions:**

* **[计划收口]**: 补强 `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`，把 Tau 的当前阶段、实施切片、退出标准和决策记录写实。
* **[模板文档替换]**: 将 `PRODUCT_SENSE`、`RELIABILITY`、`FRONTEND`、`SECURITY`、`CICD`、`SUPPLY_CHAIN_SECURITY`、`DESIGN` 等模板态文档替换为 Tau 当前真实项目描述。
* **[索引与债务同步]**: 更新设计文档索引、产品规格索引、execution plan README 和技术债追踪，使文档之间能相互引用并反映当前主计划。
* **[仓库入口补实]**: 新增根 `README.md`，让仓库级检查与项目入口文档一致。

### 🧠 Design Intent (Why)

Tau 已经不是纯模板仓库，而是进入真实移植阶段的项目。继续保留模板腔文档会让计划、优先级和质量判断漂移，所以先把“当前真实目标、先做什么、不做什么、下一步怎么推进”落到仓库里，再继续实现代码。

### 📁 Files Modified

* `README.md`
* `docs/CICD.md`
* `docs/DESIGN.md`
* `docs/FRONTEND.md`
* `docs/PRODUCT_SENSE.md`
* `docs/RELIABILITY.md`
* `docs/SECURITY.md`
* `docs/SUPPLY_CHAIN_SECURITY.md`
* `docs/design-docs/index.md`
* `docs/exec-plans/README.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/tech-debt-tracker.md`
* `docs/histories/2026-04/20260423-1218-project-docs-grounding.md`
* `docs/product-specs/index.md`
