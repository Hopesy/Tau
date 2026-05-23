
# harness-init
本项目是参考项目C:\Users\zhouh\Desktop\pi-mono-main的.NET移植版。
学习 MAGI 的三脑原则 轮流执行
1.审视，提出问题
2.执行，解决问题
3.提升，指引方向。
4.按照这个大轮次进行完整功能的移植，执行完一个计划后，开始指定下一步移植计划，不要停止思考和行动

## 先记住这两件事

- **复杂任务先落 execution plan**：不要只靠聊天上下文推进；只要任务跨轮次、跨提交、风险偏高或需要阶段性验证，就先读 `docs/PLANS_GUIDE.md` 并把 plan 落到 `docs/exec-plans/`。
- **实质变更必须补 history**：只要仓库内容发生了真实修改，就在同一轮把记录写进 `docs/histories/`；history 是默认动作，不是收尾时可选加分项。

其他文档、规范和检查项，默认都服务于这两件事：让 plan 可执行、让 history 可追溯。

## 每轮开始先读

- `docs/REPO_COLLAB_GUIDE.md`：仓库级协作、提交、文档同步与测试约定。
- `docs/ARCHITECTURE.md`：仓库整体结构和预期边界。

## 代码改完前要读

- `docs/HISTORY_GUIDE.md`：什么时候记 history、怎么命名、怎么脱敏；这是默认必做项。
- `docs/QUALITY_SCORE.md`：当前质量分层和主要短板。

## 按任务需要选读

- `docs/PLANS_GUIDE.md`：什么时候要写 execution plan，怎么维护；复杂任务优先看这个。
- `docs/PRODUCT_SENSE.md`：产品价值、取舍方式和优先级判断。
- `docs/DESIGN.md`：当前阶段的交互与宿主设计原则。
- `docs/RELIABILITY.md`：运行稳定性、观测性和上线前的基本要求。
- `docs/SECURITY.md`：认证、数据处理、外部集成等安全默认约束。
- `docs/FRONTEND.md`：如果仓库包含前端界面，这里记录对应规范。
- `docs/releases/README.md`：如何维护面向用户的发布记录。
- `docs/references/README.md`：沉淀到仓库里的外部参考资料。

## 工作规则

- 优先选择小而清晰、对仓库和 Agent 都友好的抽象。
- prompt、规则、架构约束尽量都版本化落在仓库里。
- 复杂任务不要只靠聊天上下文，应该先落 execution plan 再推进。
- 完成的代码变更要记到 `docs/histories/`，不要把 history 留到事后补票。
