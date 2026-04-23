# 产品判断原则

Tau 不是泛泛的 “AI SDK 模板”，而是 **`pi-mono` 在 .NET 生态里的长期移植项目**。  
产品判断的核心，不是堆功能数，而是判断每一轮改动是否让 Tau 更接近一个 **可持续维护、可验证、可嵌入真实 .NET 工作流的 Agent 运行时与工具集**。

## 核心用户

当前明确服务两类用户：

1. **.NET / C# 开发者**
   - 希望直接在 .NET 项目里使用多 provider LLM、Agent runtime、coding agent 能力。
   - 不想依赖 Node.js 作为宿主运行时。
2. **要做 Agent 产品或内部工具的团队**
   - 需要一个可控、可改造、可测试的 .NET 基础层。
   - 更重视长期可维护性、代码边界和部署一致性，而不是短期 demo 速度。

## 产品真正有价值的点

Tau 的价值不在于“再做一个聊天应用”，而在于以下几点：

- **用 C# 14 / .NET 10 重建 pi-mono 的核心设计**
  - EventStream
  - Agent 双层循环
  - Tool 调用与状态管理
  - 多 provider 流式抽象
- **让 .NET 用户可以不经过 Node 包装层，直接消费这些能力**
- **把运行时、CLI、未来 Web UI、Slack bot、Pods 管理这些能力放在一个统一架构里演进**

## 当前阶段判断

当前阶段不是“产品面铺开”，而是 **CLI-first 的移植收口阶段**。

也就是说，当前最重要的问题不是：

- Web UI 好不好看
- Slack bot 是否能连上
- Pod 管理是否完整

而是：

- `Tau.Ai` 和 `Tau.Agent` 的边界是否稳定
- `Tau.CodingAgent` 是否能成为第一条真实用户路径
- `Tau.Tui` 是否能承载这条路径
- 仓库是否具备最小测试和 CI 闭环

## 当前阶段优先级

### P0：必须优先满足

1. **第一条用户路径真实可走通**
   - `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`
   - 用户能输入
   - Agent 能流式响应
   - 工具调用可见且可定位失败
2. **核心边界稳定**
   - `Tau.Ai` 负责模型/流式协议
   - `Tau.Agent` 负责 loop / state / tool orchestration
   - `Tau.CodingAgent` 负责交互宿主和工具装配
   - `Tau.Tui` 负责终端 UI
3. **工程化可验证**
   - `bash scripts/verify-dotnet.sh`
   - 仓库 CI 覆盖真实 .NET 构建链

### P1：CLI P0 稳定后进入

- 更完整的模型注册与 provider 覆盖
- 更像上游的 interactive mode / session / settings / auth 流程
- `Tau.WebUi` 的第一版聊天界面

### P2：再往后

- `Tau.Mom`
- `Tau.Pods`
- OAuth / 扩展生态 / 插件化 / 更多 UI 面

## 做取舍时优先看什么

当前阶段按这个顺序判断：

1. **可信度**
   - 能不能解释清楚这段代码为什么存在
   - 有没有验证路径
   - 是否和真实仓库状态一致
2. **边界清晰**
   - 模块职责是否明确
   - 是否能避免未来反复返工
3. **最小可用**
   - 是否真的让第一条路径前进了一步
4. **体验**
   - 在不破坏边界的前提下，让交互更顺手
5. **扩展性**
   - 只有在前 4 项成立时才考虑提前抽象

## 当前阶段不应该做什么

- 为了“看起来完整”同时平推 `WebUi / Mom / Pods`
- 还没收口 CLI，就开始做大量外观型 UI 设计
- 因为上游有某个功能，就机械地 1:1 复制进 Tau
- 在没有真实验收路径的情况下提前引入复杂配置、插件体系或 provider 兼容层

## 阶段性完成标准

当下面这些成立时，说明当前阶段基本完成：

- `Tau.CodingAgent` 不再依赖裸 `Console.ReadLine()`
- `Tau.Tui` 足以支撑第一条交互路径
- 有最小 smoke 测试覆盖关键环节
- CI 覆盖真实 .NET 项目级构建与测试
- 对后续 `WebUi / Mom / Pods` 的进入顺序已有基于 CLI 基线的明确判断
