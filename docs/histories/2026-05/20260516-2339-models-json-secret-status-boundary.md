# Tau.Ai models.json secret status boundary

Date: 2026-05-16

## Summary

Continued the pi-mono port by tightening the next secret boundary after the `auth.json` write-back slice. The goal was not to remove request-time `models.json` credential resolution, but to make read-only auth status surfaces safe: checking status must not execute `!command` values or leak request credentials.

## Changes

- Added a non-resolving models.json request credential status path in `ModelConfigurationStore`.
- Updated `ProviderAuthResolver.GetStatus(...)` so `/auth` and WebUi auth status detect models.json `apiKey` / credential-like headers without resolving env values, executing `!command`, or printing secrets.
- Added `.gitignore` entries for default local `./.tau/models.json` and temporary variants.
- Added `docs/references/models-json-schema.md` to document supported fields, value resolution, trusted `!command` behavior, and secret boundaries.
- Updated `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/references/README.md`, `docs/references/auth-json-schema.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active pi-mono port plan.

## Verification

- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --filter "FullyQualifiedName~ProviderAuthResolverTests|FullyQualifiedName~ModelConfigurationStoreTests" --logger "console;verbosity=minimal"` — 10 tests passed.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --logger "console;verbosity=minimal"` — 99 tests passed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` — passed; build/test chain plus WebUi and Mom smoke completed.
- `git check-ignore -v .\.tau\models.json` — confirmed default local models config is ignored.
- `git diff --check` — passed; only expected CRLF normalization warnings were reported by Git.

## Decisions

- Kept `!command` support for runtime request dispatch because it is already part of the Tau-supported models.json value-resolution subset.
- Changed auth status to inspect configuration shape only. This preserves user-visible credential status without turning status checks into command execution.
- Treated default `./.tau/models.json` as local state because it can contain API keys or credential headers; versioned examples should be separate files.
