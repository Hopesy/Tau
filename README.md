# Tau

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植仓库，目标是在 C# / .NET 生态中重建其核心 AI Agent 能力，而不是简单包一层兼容壳。

当前仓库已经从单纯 **CLI-first 收口** 进入 **多应用面最小产品切片阶段**：

- `Tau.Ai` / `Tau.Agent` / `Tau.CodingAgent` / `Tau.Tui` 已有可运行核心基线
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择 + 流式/附件/会话管理 + JSON/HTML/Markdown/JSONL 导入导出 + CodingAgent JSONL 只读预览/tree metadata/filter/audit/保守导入** 的 Web 宿主
- `Tau.Mom` 已从纯文本 worker 推进到 **支持结构化委派请求 + 本地 events + Slack-compatible message envelope + transport/responder seam + Slack event mapper + Slack Socket Mode transport seam + Slack Web API responder seam + Slack startup backfill + Slack private file download + per-channel queue dispatcher + true cancellable stop + Mom sandbox/tool delegation seam + runtime delegation response/tool/usage 可观测事件 + 显式 sandbox validation 入口 + Docker sandbox validate/exec 可测试 seam + 附件 staging + workspace layout bootstrap + prompt debug snapshot + 本地多消息 session/model carry-over** 的本地委派宿主
- `Tau.Pods` 已从静态 config CLI 推进到 **支持 probe / exec / health / deploy / stop / restart / model lifecycle / vLLM serve command planner 与 `vllm plan/preflight/deploy/status/health/stop` CLI baseline、vLLM revision/prefetch、health retry/backoff 且 SSH exec 走 ArgumentList argv 构造并结构化处理本地 ssh 进程失败** 的运维 CLI
