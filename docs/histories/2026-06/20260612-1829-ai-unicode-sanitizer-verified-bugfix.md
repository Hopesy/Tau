# AI unicode sanitizer 直测收口并修复 valid-pair 丢字节 bug

时间：2026-06-12 18:29（本地）
主线：`GOAL.md` 100% pi-mono parity / parity matrix integer row-closure pilot
分类：CodingAgent/Ai 测试收口 + 生产 bug 修复

## 审视

延续 integer row-closure pilot，从 parity matrix 选了下一条纯确定性、无 external-e2e 依赖的 Tau-native 行：`packages/ai/src/utils/sanitize-unicode.ts` -> `src/Tau.Ai/Security/UnicodeTextSanitizer.cs`（状态 `ported`）。

该行此前只被 8 个 provider request 集成测试（`ProviderRequestTextSanitizerTests`）间接覆盖，没有针对 `UnicodeTextSanitizer.RemoveUnpairedSurrogates` 自身合同的直测。上游也没有 `sanitize-unicode.ts` 专项测试，所以补直测属于真实 Tau-native 验证，而非镜像上游测试。

## 执行

新增 `tests/Tau.Ai.Tests/UnicodeTextSanitizerTests.cs`（15 个测试），直接 pin 上游 docstring 合同：

- 保留 well-formed surrogate pair（不被误删）。
- 删除孤立 high surrogate、孤立 low surrogate（中部 / 末尾 / 开头位置）。
- 连续两个 high surrogate 都判为 unpaired；low+high 反序对两者都删。
- valid pair 紧邻孤立 surrogate 时，孤立的被删、pair 完整保留。
- 多个 distinct pair 全部保留；空串 / null 原样返回。
- 端到端不变式：sanitize 后不存在任何孤立 surrogate，且原 pair 完整存活。

**测试源码工程约束**：所有 surrogate code unit 一律用 raw `char` 强转（`(char)0xD83D` 等）和 `new string([High, Low])` 构造，源文件保持纯 ASCII（已用 `Get-Content -Encoding Byte` 验证无 >127 字节），不嵌入 literal emoji 也不用 `char.ConvertFromUtf32`，彻底规避 editor / file-encoding / console round-trip 把 astral-plane 字符写坏的问题。断言一律在 raw code unit / `Length` 上比较，不依赖 xUnit 失败信息里对 surrogate 的 `�` 渲染。

### 发现并修复的生产 bug

直测立即暴露了 `UnicodeTextSanitizer.RemoveUnpairedSurrogates` 的真实 bug：当 valid pair 之前没有任何孤立 surrogate 时，pair 的 low surrogate 会被丢掉，只剩孤立 high surrogate（如 `"only <pair> paired"` -> `"only \ud83d paired"`，长度 14 -> 13）。

根因是 null-conditional 短路：

```csharp
builder?.Append(current);
builder?.Append(text[++i]);   // builder 为 null 时整个右值短路，++i 副作用不执行
```

`builder` 在尚未遇到孤立 surrogate 时为 null，`builder?.Append(text[++i])` 会把整个右侧（含 `++i`）短路掉，于是循环自身的 `i++` 落到 low surrogate 上，被当成孤立 low surrogate 删除。

修复：把游标推进 `i++` 从 `Append` 实参里拆出来，作为无条件语句执行，`builder?.Append(text[i + 1])` 只负责 buffer：

```csharp
builder?.Append(current);
builder?.Append(text[i + 1]);
i++;
continue;
```

8 个 provider 集成测试一直没抓到这个 bug，因为它们的输入总是把孤立 surrogate 放在 emoji 之前，会提前 allocate `builder`，从而让 `text[++i]` 真正求值。新直测是第一个用「前面没有孤立 surrogate 的 valid pair」触发该路径的用例。

## 提升

- 把 parity matrix `packages/ai/src/utils/sanitize-unicode.ts` 行从 `ported` 提升为 `verified`，machine count verified 2 -> 3（ported 29、partial 197、missing 1、external-e2e-needed 31、non-goal-proposed 1，total 262 不变）。
- `next.md`、`docs/QUALITY_SCORE.md` 已同步本轮增量与 bug 修复说明。

## 验证

- 直测 `UnicodeTextSanitizerTests` 15/15 通过。
- 完整 `Tau.Ai.Tests` 307/307（292 -> 307，新增 15）。
- 项目级 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过：Ai 307、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216。
- `git diff --check`：仅既有 docs CRLF normalization warning。

## 边界 / 仍 open

- 该行是无 external-e2e 依赖的纯字符串 sanitize 合同，`verified` 反映本地证据完整。
- provider 端真实 e2e（真实云端拒绝孤立 surrogate 的行为）仍由各 provider 行的 `external-e2e-needed` 管理，不被本行 `verified` 覆盖。
