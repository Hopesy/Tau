# Tau.Ai auth secret persistence boundary

Date: 2026-05-16

## Summary

Continued the pi-mono port by tightening the first concrete secret persistence boundary around `auth.json`. OAuth login/refresh had already made credential write-back real, so this slice makes that local file safer and documents the remaining limits instead of treating all secret handling as solved.

## Changes

- Added `.gitignore` entries for default local Tau credential/session state:
  - `./.tau/auth.json`
  - temporary `auth.json` files
  - JSONL coding-agent session state and cloned session directories
- Updated `OAuthCredentialStore.Save()` so Unix writes use owner-only file mode (`0600`).
- Filtered reserved OAuth metadata names during credential write-back: `type`, `refresh`, `access`, `expires`, `expiresAt`, `key`, and `apiKey`.
- Added regression tests for reserved metadata filtering and Unix file mode behavior.
- Updated `docs/references/auth-json-schema.md`, `next.md`, the active pi-mono port plan, and `docs/QUALITY_SCORE.md` to describe what is now covered and what remains open.

## Verification

- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --filter "FullyQualifiedName~OAuthProviderTests|FullyQualifiedName~OAuthCredentialStoreTests|FullyQualifiedName~ProviderAuthResolverTests" --logger "console;verbosity=minimal"` — 26 tests passed.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --logger "console;verbosity=minimal"` — 97 tests passed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` — passed; build/test chain plus WebUi and Mom smoke completed.
- `git diff --check` — passed; only existing CRLF normalization warnings were reported by Git.

## Decisions

- Kept this as an `auth.json` baseline rather than a broad “all secrets solved” claim. `models.json` secrets, export/share surfaces, and runtime logs still need separate review.
- Used platform-specific file mode handling instead of trying to emulate Unix permissions on Windows.
- Skipped reserved metadata keys instead of failing login/refresh; provider metadata is auxiliary, while the canonical credential fields must remain unambiguous.
