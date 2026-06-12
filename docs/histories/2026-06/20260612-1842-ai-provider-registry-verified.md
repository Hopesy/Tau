# 2026-06-12 17:42 — AI ProviderRegistry integer row-closure → verified

## 背景

继续 `/goal` 100% pi-mono parity 主线的 integer row-closure pilot。本轮领取 parity matrix 中 `packages/ai/src/api-registry.ts` → `src/Tau.Ai/Providers/ProviderRegistry.cs` 行（此前 `ported`）。该行是纯确定性、无网络依赖的 provider 注册表，适合作为 row-closure 候选（参见前几轮 [[row-closure-pilot-finds-bugs]] 的方法论）。

## 缺口

`ProviderRegistry` 此前没有专门的单元测试文件，只有 `BuiltInProvidersTests` 间接触及 `RegisteredApis` membership。注册表自身合同——lazy factory 初始化、replace 语义、`Get` / `TryGet`、`Unregister`、`UnregisterBySource` 选择性删除、`Clear`——均无直接覆盖。

## 改动

新增 `tests/Tau.Ai.Tests/ProviderRegistryTests.cs`（13 项测试），用最小 fake `IStreamProvider` 固定注册表自身合同：

- factory 重载延迟初始化（注册时不调用 factory，首次 `Get` 才调用）。
- factory 实例缓存（多次 `Get` 复用同一 `Lazy<T>` 实例）。
- provider 实例重载直接注册。
- 同名 api 重新注册时 replace（`AddOrUpdate`）。
- `Get` 命中返回 provider、未命中抛 `KeyNotFoundException`。
- `TryGet` 命中返回实例、未命中返回 null。
- `RegisteredApis` 反映当前注册键集合。
- `Unregister` 删除单个 api。
- `UnregisterBySource` 只删除匹配 `sourceId` 的条目，保留其它 source 与无 source 条目。
- `UnregisterBySource` 对未知 source 为 no-op。
- `Clear` 清空全部。

`Tau.Ai.Tests` 287（注：本轮起点实为上一 PKCE/sanitizer 轮后的 307）→ 320，新增 13 项。

## 设计差异（已在 matrix note 标注，不 overclaim）

上游 `registerApiProvider` 用 `wrapStream` / `wrapStreamSimple` 包装并在 `model.api !== api` 时抛 `Mismatched api`。Tau 的 `ProviderRegistry` 不做包装——provider 通过 `IStreamProvider.Api` 自报身份，api 匹配校验在更上层（`StreamFunctions` 解析路径）完成。`verified` 反映 Tau 注册表自身合同（lazy/replace/source/clear）的本地证据已完整，而非 TypeScript wrap-guard 形状逐字 parity。

## 验证

- `Tau.Ai` build 0 warning / 0 error。
- focused `Tau.Ai.Tests` 320/320。
- 项目级 `verify-dotnet.ps1 -SkipRestore`：Ai 320、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216，全绿。

## matrix / 计数

`packages/ai/src/api-registry.ts` 行 `ported` → `verified`。machine count：verified 3 → 4（ported 28、partial 197、missing 1、external-e2e-needed 31、non-goal-proposed 1，total 262 不变）。

## 仍 open

provider stream 函数的 api-mismatch wrap-guard 形状 parity、完整 TypeScript `ApiProvider<TApi, TOptions>` 泛型表面、真实 provider/OAuth e2e 仍由其它 partial / external-e2e-needed 行管理。
