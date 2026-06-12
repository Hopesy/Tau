# WebUi-local import/export 整行收口到 verified（首个 verified>0）

- 日期：2026-06-12 17:47
- 主线：`GOAL.md` 100% pi-mono parity；本轮按 integer row-closure 试点，从 parity matrix 选 Tau-native、无 external-e2e 依赖的 `WebUi-local import/export` 行，建立完整本地证据链后把状态从 `ported` 收口到 `verified`。
- 角色：Main Integrator（matrix/next/QUALITY_SCORE/history/commit 边界 + WebUi 本地导出导入证据补强）。

## 背景与动机

截至上一轮，parity matrix 机器计数为 verified=0、ported=31、partial=197、missing=1、external-e2e-needed=31、non-goal-proposed=1（共 262）。此前 4 刀都在 `partial` 行内收紧子合同，按诚实记账不足以翻转整行，因此 verified 长期停在 0。

本轮改变策略：选一条 Tau-native、不依赖真实 provider/服务/运行时 smoke 的行先收口，让 verified 第一次离开 0，建立可审计的整行闭合范式。`WebUi-local import/export`（`WebChatJsonlExporter/Importer` + `WebChatHtmlExporter` + `WebChatMarkdownExporter`）满足条件：确定性输出、纯 .NET、无外部 e2e 依赖。

## 收口前的证据缺口

- `WebChatHtmlExporter.Render` 无专属单元测试。
- `WebChatMarkdownExporter.Render` 无专属单元测试。
- importer 多个结构化错误码未被测试：`empty_line`、`missing_type`、`invalid_entry_type`、`missing_field`（已测的有 `invalid_json`、`missing_session_header`、`unsupported_version`、`duplicate_message_id`、`invalid_parent_chain`）。

## 本轮改动

- 新增 `tests/Tau.WebUi.Tests/WebChatExportImportRoundTripTests.cs`（9 个测试）：
  - HTML 导出：role-classed `<article class="message message--{role}">`、HTML 编码（`WebUtility.HtmlEncode`）、thinking/text/tool-call/attachment/error 渲染、`redactor.Enabled` 时的 redaction footer、脱敏开/关行为。
  - Markdown 导出：`# title` + provider/model/created/updated 元数据、`## Role — timestamp`、`<details>` thinking、`### Tool — \`name\` (status)` 工具块、attachments 列表、error blockquote，脱敏开/关行为。
  - importer 错误码：`empty_line`、`missing_type`、`invalid_entry_type`、`missing_field` 现在都有断言覆盖。
- `Tau.WebUi.Tests` 计数 61 -> 70（新增 9）。

## 状态变更

- parity matrix `WebUi-local import/export` 行：`ported` -> `verified`。Proof 列重写为完整本地证据清单，并明确这是 Tau-native surface（非直接上游 component package surface），`verified` 反映“对存在的这一面已有完整本地证据”，不等同上游 component-package 形状 parity。
- matrix 机器计数：verified 0 -> 1，其余不变（ported=31、partial=197、missing=1、external-e2e-needed=31、non-goal-proposed=1，共 262）。这是 verified 首次离开 0。

## 验证

- 项目级 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过：Ai 287、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216，0 warning / 0 error。

## 同步

- `next.md`：新增本轮验证基线条目。
- `docs/QUALITY_SCORE.md`：最新增量记录本轮收口。
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`：行状态与 proof 更新。

## 仍 open

- 该行只覆盖 Tau-native 线性 JSONL/JSON/HTML/Markdown 导出导入面；上游 web-ui component package 的可复用 Lit/mini-lit 组件 + CSS export shape 仍是独立 `partial` 行。
- 全仓仍有 ported=31、partial=197、external-e2e-needed=31 等待后续切片，verified=1 只是第一步。
