## [2026-04-30 14:52] | Task: Google Vertex ADC token exchange

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4 Codex`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau 从 pi-mono-main 移植，持续推进 provider / auth fidelity 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` Google Vertex provider auth

**Key Actions:**

* **[Resolver]**: 新增 `GoogleVertexAccessTokenResolver`，读取 explicit credentials file、`GOOGLE_APPLICATION_CREDENTIALS` 与默认 gcloud ADC 文件。
* **[Service Account]**: 支持 ADC `service_account` JSON，按 OAuth JWT bearer grant 生成 RS256 assertion 并换取 access token。
* **[Authorized User]**: 支持 ADC `authorized_user` JSON，按 refresh token grant 换取 access token。
* **[Provider]**: `GoogleVertexProvider` 保留 API key / `x-goog-api-key` 路径；当检测到 ADC marker 或 credentials file 时改用 `Authorization: Bearer` 调用 Vertex `streamGenerateContent`。
* **[Options]**: 新增 `GoogleVertexOptions` / `GoogleVertexSimpleOptions`，支持显式 `AccessToken / CredentialsFile / Project / Location`。
* **[Tests]**: 新增 `GoogleVertexProviderTests`，覆盖 registry、API key、service account ADC、`StreamFunctions` options 保真、authorized user ADC、unsupported ADC type、credentials project id + option location endpoint。
* **[Docs]**: 同步 `next.md`、architecture、quality、baseline plan 与本切片 execution plan。

### 🧠 Design Intent (Why)

Vertex 在本地和云端常见路径不是只有 `GOOGLE_CLOUD_API_KEY`，还包括 ADC service account key 与 `gcloud auth application-default login` 生成的 authorized user refresh token。为了延续 Tau.Ai 当前“HttpClient + source-gen/手写协议 + 零 provider SDK 依赖”的边界，本轮只实现最小可验证 token exchange，不引入 Google auth SDK，也不一次性吞下 workload identity federation、impersonation 或 token cache。

### 📁 Files Modified

* `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexAccessTokenResolver.cs`
* `tests/Tau.Ai.Tests/GoogleVertexProviderTests.cs`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/completed/2026-04-30-google-vertex-adc-token.md`
