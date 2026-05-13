# CodingAgent tree filters and search

## 用户诉求

用户要求继续推进当前 Tau 迁移主线。根据 active execution plan 和 `next.md`，本轮继续收口 `Tau.CodingAgent` JSONL session tree 的未完成缺口。

## 主要变更

- 为 `/tree` 增加命令行过滤模式：`default`、`no-tools`、`user-only`、`labeled-only`、`all`。
- 为 `/tree` 增加 `--search` / `-s`，按空格分词匹配 visible entry 的文本内容、label 和基础 metadata。
- 为 `/tree` 增加 `--label-time` / `--label-timestamps` / `+label-time`，可显示 latest label change timestamp。
- 为 settings JSON 增加上游兼容的 `treeFilterMode` 字段，作为 `/tree` 未显式传过滤模式时的默认值；无效值回退 `default`。
- 修正 `SaveDefaultModel` 只更新默认 provider/model，避免 `/model` / `/provider` 写入时清掉 `treeFilterMode` 等非模型设置。
- 默认 tree 输出对齐上游 tree selector 的核心语义：隐藏 bookkeeping entries，隐藏没有文本内容的 tool-only assistant message；`no-tools` 额外隐藏 tool result。
- tree 输出增加 leaf marker、缩进深度、filter/search 状态，继续保留 current branch marker。
- 补充 `CodingAgentCommandRouterTests` / `CodingAgentSettingsStoreTests`，覆盖 filter modes、search、settings `treeFilterMode`、label timestamp、bookkeeping 隐藏、tool-only assistant 隐藏、`no-tools` 行为和默认模型保存不覆盖 tree 设置。
- 同步更新 README、架构、质量评分、active plan 和 `next.md`，明确这只是命令行 tree viewer 增量，不等于完整 TUI interactive navigator。

## 设计动机

上游 `TreeSelector` 包含过滤、label timestamp、search、fold 和交互式选择，并通过 settings `treeFilterMode` 记住默认过滤模式。Tau 当前还没有完整 TUI overlay/select-list/search 组件栈，直接做完整 interactive navigator 会把 UI 基础设施和 session tree 语义混在一起。本轮先把可本地验证的过滤语义、命令行 search、settings default filter 和 label timestamp 固定在 `/tree` 命令面，减少后续实现真正 interactive navigator 时的语义漂移。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `next.md`
