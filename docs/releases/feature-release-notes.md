# 功能发布记录

## 2026-05

| 日期 | 功能域 | 用户价值 | 变更摘要 |
| --- | --- | --- | --- |
| 2026-05-22 | CodingAgent | 用户可以在 CLI 内查看、列出、选择或清除当前主题，不必手改 settings JSON。 | 新增 `/theme [current\|list\|set\|clear] [name]` baseline，复用 theme loader/status 发现 built-in、用户、项目、显式和 extension-contributed themes，`set` 会校验主题存在后写入 settings `theme`，`clear` 回到默认 `dark`。 |
| 2026-05-21 | CodingAgent | CLI 启动时会自动带入项目级 `AGENTS.md` / `CLAUDE.md` 指令，不需要用户手动复制仓库协作规则。 | 新增 context files baseline：默认发现 `~/.tau/AGENTS.md` 或 `CLAUDE.md`，再按父目录到当前目录加载项目链；同目录优先 `AGENTS.md`；生成 system prompt 会注入 `# Project Context`；`--no-context-files` / `-nc` 可禁用，`/reload` 会刷新当前 context file 列表。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 新建会话时可以保留父会话来源，后续 `/session`、`/tree` 和 HTML export 能追溯分叉来源。 | RPC `new_session` 现在读取可选 `parentSession`，写入 JSONL session header 的 `parentSession` metadata，并继续追加 `session_info action=new`；CLI `/new` 行为保持不变。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以读取并批量更新 Tau 当前支持的 settings，不必逐个调用零散控制命令。 | 新增 RPC `get_settings` / `update_settings` baseline，覆盖默认模型、tree filter、retry、default thinking、enabledModels、steering/follow-up mode 和 auto-compaction enabled；更新后会同步当前 runner 状态，active prompt 期间拒绝修改。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以直接执行一次 shell 命令，并能取消运行中的 shell 命令。 | 新增 RPC `bash` / `abort_bash` baseline，`bash` 后台执行并返回 `output`、`exitCode`、`cancelled`、`truncated` 和可选 `fullOutputPath`，`abort_bash` 只取消当前 shell 命令；并发 `bash` 会被拒绝。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以切换 steering/follow-up 队列模式，并持久化 auto-compaction 开关。 | 新增 RPC `set_steering_mode` / `set_follow_up_mode` / `set_auto_compaction` baseline；`get_state` 现在返回 runner/settings backed `steeringMode`、`followUpMode` 和 `autoCompactionEnabled`，settings 支持 `steeringMode`、`followUpMode`、`autoCompactionEnabled` 并读取旧 `queueMode`。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以切换 JSONL session，并展示可 fork 的用户消息列表。 | 新增 RPC `switch_session` / `get_fork_messages` baseline，复用 JSONL tree session restore 与 user message extraction；active prompt 期间拒绝切换，切换后同步 flat snapshot。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以开关自动重试，并取消等待中的 retry delay。 | 新增 RPC `set_auto_retry` / `abort_retry` baseline，复用 settings retry policy、rollback 和 JSONL retry audit；取消等待时写 `Retry cancelled`，失败输入不落盘。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以按 scope 切换模型，不需要交互式 CLI。 | 新增 RPC `cycle_model` baseline，复用 settings `enabledModels` 或全部可用模型，保存默认模型，候选不足时返回显式 `data:null`，active prompt 期间拒绝切换。 |
| 2026-05-21 | CodingAgent | Headless / RPC client 可以调整 thinking level，不需要交互式 CLI。 | 新增 RPC `set_thinking_level` / `cycle_thinking_level` baseline，持久化 settings `defaultThinkingLevel`，active prompt 期间拒绝变更。 |
| 2026-05-21 | CodingAgent | 用户可以在 CLI 内查看当前 settings 路径和有效配置摘要，不必打开 JSON 文件核对。 | 新增 `/settings [current\|path]` read-only baseline，展示当前 model/thinking、默认 model、tree filter、retry、default thinking 和 enabledModels scope。 |
| 2026-05-21 | CodingAgent | 用户可以在 CLI 内管理模型切换 scope，不必手改 settings 文件。 | 新增 `/scoped-models [set\|add\|remove\|clear\|all]` baseline，写入 settings `enabledModels`，`clear/all` 回到全模型。 |
| 2026-05-21 | CodingAgent | 用户可以在 CLI 内查看 Tau 最近的本地发布记录，不需要离开当前会话去翻文档。 | 新增 `/changelog [count\|all]` baseline，默认读取 `docs/releases/feature-release-notes.md`，并支持 `TAU_CODING_AGENT_CHANGELOG_FILE` 覆盖来源。 |

## 2026-04

| 日期 | 功能域 | 用户价值 | 变更摘要 |
| --- | --- | --- | --- |
| 2026-04-08 | 模板仓库 | 提供了一套可直接用于新项目启动的 Agent-first 基础模板。 | 补齐了 AGENTS 入口、execution plan、history、release note、CI/CD 和供应链安全骨架。 |
