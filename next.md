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
- [ ] 与 `ModelCatalog` 对齐的默认模型解析层
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

- [ ] 从 ASP.NET Core 占位页进到真实聊天 UI
- [ ] 流式消息绑定
- [ ] 会话、附件、设置、provider/model 选择

### Tau.Mom

- [ ] Slack 对接
- [ ] workspace / sandbox / tool delegation
- [ ] message / runtime flow

### Tau.Pods

- [ ] pod 配置
- [ ] SSH / lifecycle / model management
- [ ] 真正的 CLI 命令体系

## P2：工程化

- [ ] release 产物改为真实 Tau 可执行产物
- [ ] solution build 的环境异常诊断文档化
- [ ] provider e2e 测试
- [ ] coding-agent 默认路径的更高层回归测试
- [ ] 可观测性：provider 调用、auth、tool execution 的最小日志
