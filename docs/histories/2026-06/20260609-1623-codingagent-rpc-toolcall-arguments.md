## [2026-06-09 16:23] | Task: CodingAgent RPC toolCall arguments schema

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续继续

### Changes Overview

**Scope:** `Tau.CodingAgent` RPC schema parity.

**Key Actions:**

* **RPC message projection**: changed `get_messages` and shared RPC message serialization to use the same `ToRpcMessage` / `ToRpcContent` path as streamed agent events instead of returning the session-store DTO directly.
* **Tool call arguments shape**: parse valid `ToolCallContent.Arguments` JSON object strings into JSON objects at the RPC output boundary so `ToolCall.arguments` matches upstream `Record<string, any>` in `toolcall_end.toolCall`, assistant partial/message payloads, agent lifecycle messages and `get_messages`.
* **Regression tests**: added RPC host coverage for object-shaped tool call arguments and `thoughtSignature` in both streaming assistant events and `get_messages`.
* **Docs sync**: updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md` and the active parity matrix/plan to mark only this local RPC object-shape baseline as closed while leaving broader RPC and extension/runtime gaps open.

### Design Intent (Why)

Tau keeps `ToolCallContent.Arguments` as a JSON string internally because provider parsers, validation, HTML export and session persistence already rely on that compact contract. Upstream RPC clients, however, receive `ToolCall.arguments` as an object. This change keeps the internal model stable and moves the compatibility conversion to the RPC boundary, where the schema mismatch actually appears.

Invalid, empty or non-object argument payloads fall back to `{}` at the RPC boundary to preserve the upstream object contract without throwing while writing JSONL events.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
