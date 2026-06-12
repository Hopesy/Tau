# 20260612-1801 AI OAuth PKCE row closed to verified

## 背景

继 WebUi-local export/import 行作为首个 `verified` 行收口后，本轮延续整数行收口（integer row-closure）策略，挑选下一条 Tau-native、确定性、无 external-e2e 依赖的 `ported` 行收口为 `verified`，让 matrix 的 `verified` 机器计数继续从 1 增长到 2。

选定的行是 `packages/ai/src/utils/oauth/pkce.ts` → `src/Tau.Ai/Auth/OAuth/OAuthPkce.cs`：纯 RFC 7636 PKCE verifier/challenge 生成，只依赖 `RandomNumberGenerator` 与 `SHA256`，没有网络/provider/runtime 依赖，是最干净的可本地收口候选。上游 `pi-mono-main` 对 `pkce.ts` 没有任何测试，因此 Tau 的本地证据链就是该面唯一的验收依据。

## 改动

`tests/Tau.Ai.Tests/OAuthProviderTests.cs` 在原有 2 个 PKCE 测试（基本 shape + 与 SHA-256 公式一致、两次生成不同）基础上新增 5 个测试，固定 RFC 7636 合同的剩余维度：

- verifier base64url 解码后正好是 32 字节（熵合同，对照上游 `new Uint8Array(32)`）。
- challenge base64url 解码后正好是 32 字节（SHA-256 输出长度），且字符串长度 43。
- verifier 与 challenge 都只包含 base64url 字母表字符（`A-Za-z0-9-_`），不含 `+`、`/`、`=`。
- challenge 等于对 verifier 的 ASCII 字节做 SHA-256 后再 base64url 编码（再次以独立路径锁定 `code_challenge_method=S256` 语义）。
- 多次迭代（50 次）生成的 verifier 全部互不相同且长度稳定为 43（无偏熵/无 padding 抖动）。

`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 把 `packages/ai/src/utils/oauth/pkce.ts` 行从 `ported` 改为 `verified`，证据列补充测试文件与覆盖维度，并注明这是纯确定性 Tau-native 面、上游无对应测试。

## 验证

- focused：`dotnet test tests/Tau.Ai.Tests`（含构建）287→292，全部通过（新增 5 个 PKCE 测试）。
- 项目级 gate：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 全绿：Ai 292、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216。

## 计数影响

matrix 状态机计数：verified 1→2，ported 31→30，partial 197、missing 1、external-e2e-needed 31、non-goal-proposed 1 不变（总 262 不变）。

## 仍然 open

PKCE 行收口只代表该纯密码学生成面的本地证据完整；真实 provider OAuth 登录/刷新 e2e（Anthropic/Copilot/Gemini CLI/Antigravity/OpenAI Codex）仍是各自 `external-e2e-needed` / `partial` 行的缺口，不被本行 `verified` 覆盖。
