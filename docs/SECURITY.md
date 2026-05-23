# 安全默认约束

Tau 当前是本地开发者工具链项目，安全边界要围绕“**本地 Agent 运行时 + provider 凭证 + 文件/命令工具**”来定义，而不是套通用 SaaS 模板。

## 当前信任边界

### 已在范围内

- 本地开发机器上的 Tau 进程
- `Tau.CodingAgent` 当前工作目录内的文件访问
- 模型 provider 的 API 凭证
- 通过 Agent 工具发起的文件读写、搜索和 shell 执行

### 暂未进入真实实现范围

- Web 对外服务面
- Slack 机器人集成
- Pod 远程管理与 SSH 链路
- 多用户认证授权

这些模块在真正进入实现前，只记录规划，不假装已经有完整安全承诺。

## 凭证与配置

### 当前现实

- `Tau.Ai` 当前按 explicit API key、环境变量、`auth.json`、`models.json` 的顺序解析 provider 凭证
- `auth.json` 支持 API key 与 OAuth 条目，OAuth login/refresh 会写回本地凭证文件
- `models.json` 支持自定义 provider/model、request `apiKey`、`authHeader` 和 provider/model headers

### 约束

- 不把真实密钥写入仓库
- 不在日志、history、计划文档里直接回显密钥
- 默认本地 `./.tau/auth.json` 和 `./.tau/models.json` 都必须忽略；需要版本化时只提交无密钥示例文件
- `auth.json` 是 credential write-back store；`models.json` 是 request-time 配置，不承载 OAuth refresh 写回
- HTML transcript 导出（`/export`、`/share`）默认走 `CodingAgentSecretRedactor`：匹配常见 AWS access key、GitHub token、Slack token、Anthropic / OpenAI key、`Bearer …` header、JWT 模式时替换为 `[redacted]`；可通过 `TAU_CODING_AGENT_REDACT_SECRETS=0` 显式关闭以备调试
- Tau.WebUi `GET /api/sessions/{id}/export.html` 复用同一组规则（`TauSecretRedactor` 在 Tau.Ai 中实现），默认开启脱敏；可通过 `TAU_WEBUI_REDACT_SECRETS=0` 关闭
- `JsonlTauLogSink` runtime event log 默认使用 `TauSecretRedactor`，对 category、event 和 field value 中的常见 secret pattern 替换为 `[redacted]`；`TAU_LOG_REDACT_SECRETS=0` 可在 sink 创建时显式关闭。该规则不处理 field key，不覆盖所有 provider-specific secret 形态，也不覆盖 CodingAgent session JSONL、WebUi JSONL export/import、Mom channel log 或 prompt debug JSONL

## 文件与命令工具边界

当前 `Tau.CodingAgent` 已具备文件和 shell 类工具，所以这是当前最现实的风险面。

### 默认原则

- 工具描述和实现要尽量明确边界
- 报错时优先显式失败，不做会扩大影响面的隐式补救
- 不要把高风险行为包装成普通查询行为

### 当前阶段重点

- 先把工具行为做清楚、可定位、可测试
- 后续再考虑更细的路径限制、确认流或沙箱策略

## 日志与敏感信息

- 日志优先记录阶段、错误类型和工具名
- 不默认记录完整 prompt、完整文件内容或完整命令输出
- 文档和 history 里不落本地敏感路径、密钥和用户私有数据

## 依赖与供应链

Tau 当前没有保留旧 harness-init 的 GitHub Actions / SBOM / provenance 脚手架。供应链边界先按真实 .NET 项目状态处理：

- NuGet 依赖变化必须能通过 `scripts/verify-dotnet.ps1` / `scripts/verify-dotnet.sh` 的 restore、build、test 暴露。
- 真实发布产物出现前，不维护仓库元数据包式的 release / provenance 占位流程。
- 后续如果重新接入 CI、SBOM 或 provenance，必须基于 Tau 的真实可执行产物和当前依赖链重新建文档，不恢复旧模板占位。
- 导出/share、非 `JsonlTauLogSink` 的 JSONL session/export/channel/prompt-debug 文件和跨运行态配置迁移仍需要继续守住默认脱敏边界。

## 本地配置参考

- `docs/references/auth-json-schema.md`
- `docs/references/models-json-schema.md`

## 后续进入更高风险模块时的要求

### `Tau.WebUi`

- 明确暴露面
- 不把 provider key 直接暴露给浏览器端
- 区分本地开发模式与真实部署模式

### `Tau.Mom`

- Slack token / bot token 不入仓库
- workspace、tool、sandbox 边界必须先定义再编码

### `Tau.Pods`

- 明确 SSH / API key / remote shell 权限边界
- 不让远程执行能力先于最小安全约束落地
