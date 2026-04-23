# 技术债追踪

这里记录那些暂时不阻塞当前任务、但已经值得留档的技术债。

| 日期 | 区域 | 债务描述 | 为什么会存在 | 计划中的后续动作 |
| --- | --- | --- | --- | --- |
| 2026-04-23 | `Tau.Tui` | 当前只有最小 transcript / input buffer / session 层，还不是真正的组件系统、编辑器和差分渲染 UI | 先把 `Tau.CodingAgent` 的第一条交互路径收口，再向 richer TUI 扩展 | 继续作为 CLI P0 之后的优先补强项 |
| 2026-04-23 | Build/CI | 项目级 restore/build/test 已稳定，但 `Tau.slnx` 仍存在 solution metaproj / workload resolver 异常 | 为恢复 `Tau.CodingAgent` 独立构建，当前临时用了显式项目构建链和 `HintPath` DLL workaround | 继续定位 solution build 根因，并争取回收到更正常的 `ProjectReference` 结构 |
| 2026-04-23 | Provider/Auth | 已补第一轮 provider、registry、auth 骨架，但真实 OAuth/login、generated models 和高保真 provider 行为仍不完整 | 先把 provider 可发现、model 可查询、auth 可解析这条主干做实，再逐个补 provider fidelity | 按 `next.md` 继续推进 Bedrock、Responses/Codex fidelity、OAuth login flow 和 generated models |
