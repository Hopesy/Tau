# Tau next

这份文件只记录 **当前还没做完、后续需要继续推进的缺口**，方便快速检索。

## P0：当前最值得继续推进的项

### Tau.Ai

#### Provider / API fidelity

- [ ] `Amazon Bedrock` 真实实现 SigV4 / bearer token 调用，而不是当前 placeholder
- [ ] `openai-responses` 与 `openai-codex-responses` 提高协议保真度，补齐和上游更一致的 payload / stream 语义
- [ ] `Mistral` 从 OpenAI-compatible 过渡到更接近原生 conversations 行为
- [ ] `Google Vertex` 从 API key 模式扩到真正 ADC token exchange
- [ ] `Google Gemini CLI / Antigravity` 从当前简化版凭证载荷扩到更完整的请求/响应细节

#### Model registry / generated models

- [ ] 建立 generated models 管线，不再只靠手写 `BuiltInModels`
- [ ] 引入更完整的 provider 列表和模型全集
- [ ] 支持 typed/default model 解析与更接近上游的 default model 策略
- [ ] 把 compatibility / capability / routing 元数据补全

#### OAuth / auth

- [ ] Anthropic OAuth 真实 login / refresh 流程
- [ ] GitHub Copilot device flow
- [ ] OpenAI Codex OAuth flow
- [ ] Gemini CLI / Antigravity login flow
- [ ] auth.json 的迁移、写回、刷新后持久化策略

#### 配置 / 安全

- [ ] auth.json schema 明文化
- [ ] secret 持久化边界和脱敏规则
- [ ] provider-specific headers / dynamic auth header 支持
- [ ] 自定义 provider / custom model 配置入口

### Tau.CodingAgent

- [ ] session 持久化
- [ ] settings / model selection / provider selection
- [ ] auth 管理入口
- [ ] richer rendering
- [x] 显式 `Create(provider, model, history)` runner 工厂
- [ ] 与 `ModelCatalog` 对齐的默认模型解析层继续收口
- [ ] 把当前 `Tau.CodingAgent` / `Tau.CodingAgent.Tests` 的 DLL `HintPath` workaround 收回到更正常的 `ProjectReference` 结构
- [ ] 解决当前本机上 `Tau.slnx` / metaproj / workload resolver 的 build 异常

### Tau.Tui

- [ ] 真正的输入编辑器
- [ ] 组件系统
- [ ] 消息区 / 状态区
- [ ] 键盘体系
- [ ] 更稳定的差分渲染层

## P1：后续应用面

### Tau.WebUi

- [x] 最小聊天 UI
- [x] session 持久化（`output/webui-sessions.json`）
- [x] provider/model 选择入口（`/api/catalog` + 会话设置）
- [ ] 流式消息绑定
- [ ] richer rendering / thinking / tool timeline 展示
- [ ] auth/settings UX
- [ ] 附件体系
- [ ] 更高层的 WebUi 行为测试

### Tau.Mom

- [x] 本地文件委派 worker
- [x] `--once`
- [x] inbox/outbox/archive
- [x] 结构化 `.json` 请求（`prompt/provider/model/workingDirectory/metadata`）
- [ ] Slack 对接
- [ ] workspace / sandbox / tool delegation
- [ ] message / runtime flow
- [ ] 更高层 delegation flow 与端到端测试

### Tau.Pods

- [x] config init/list/validate/status
- [x] probe（HTTP endpoint / TCP ssh target）
- [x] exec（SSH pod remote command execution）
- [ ] deploy / stop / restart / health
- [ ] 真正的 CLI 运维命令体系

## P2：工程化

- [ ] release 产物改为真实 Tau 可执行产物
- [ ] solution build 的环境异常诊断文档化
- [ ] provider e2e 测试
- [ ] coding-agent 默认路径的更高层回归测试
- [ ] 可观测性：provider 调用、auth、tool execution、session / delegation / pod probe 的最小日志
- [ ] `scripts/verify-dotnet.sh` 对运行态 smoke 的进一步自动化

## 当前已知环境现实

- [ ] 当前 Windows 环境下 `bash` 仍可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`
- [ ] 本地标准命令继续保持 bash 形式，但现场执行要接受等价顺序 `dotnet` 验证作为兜底
- [ ] `Tau.WebUi / Tau.Mom` 仍保留 `HintPath` workaround，暂时不要和产品能力改动混做
