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

- `Tau.CodingAgent` 当前默认使用 `OPENAI_API_KEY`
- OAuth provider、集中认证和多 provider 凭证管理都还未完成

### 约束

- 不把真实密钥写入仓库
- 不在日志、history、计划文档里直接回显密钥
- 如果未来引入本地配置文件，要提供示例配置与忽略规则

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

仓库级依赖与 provenance 约束见：

- `docs/SUPPLY_CHAIN_SECURITY.md`

Tau 当前还缺少的是：

- .NET / NuGet 真实构建链进入 CI
- 与真实发布产物对应的 SBOM / provenance
- 更清晰的配置与 secrets 边界

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
