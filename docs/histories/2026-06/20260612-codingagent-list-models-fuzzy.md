## [2026-06-12] | CodingAgent --list-models shared fuzzy matcher

### Scope

`src/Tau.Tui/Components/TuiFuzzyMatcher.cs`, `src/Tau.CodingAgent/Runtime/CodingAgentModelListFormatter.cs`, `tests/Tau.CodingAgent.Tests/CodingAgentModelListFormatterTests.cs`

### Context

Upstream `packages/coding-agent/src/cli/list-models.ts` decides `--list-models` membership with the `pi-tui` `fuzzyFilter` (`packages/tui/src/fuzzy.ts`), then sorts the surviving rows alphabetically by provider/id. Tau already had a faithful port of `fuzzy.ts` in `TuiFuzzyMatcher` (scored ranking, word-boundary/consecutive bonuses, gap penalties, and the alphanumeric-swap fallback so a query like `4g` still matches `gpt-4o`), but it was `internal` to Tau.Tui. `CodingAgentModelListFormatter` therefore used a local plain-subsequence matcher with no swap fallback, so `--list-models` membership diverged from upstream for transposed alphanumeric queries.

### Changes

- Made `TuiFuzzyMatcher` and `TuiFuzzyMatch` public (Tau.Tui is the upstream `pi-tui` analogue and the natural home for the shared `fuzzyFilter`; CodingAgent already references Tau.Tui). Added a class doc comment explaining the cross-module reuse.
- `CodingAgentModelListFormatter.Format` now decides search membership via `TuiFuzzyMatcher.Filter` (which applies upstream's multi-token "all tokens must match" semantics and the alphanumeric-swap fallback), then keeps the existing alphabetical provider/id sort, matching upstream's filter-then-sort order. Removed the now-unused local `IsFuzzyMatch`/`IsSubsequence` subsequence helpers.
- Added a targeted test proving the alphanumeric-swap fallback (`4g` matches `openai gpt-4o` but not `gemini-2.5-pro`); existing render / `gem` filter / no-match tests unchanged.

### Validation

- `dotnet test tests/Tau.CodingAgent.Tests --filter CodingAgentModelListFormatterTests` 4/4.
- `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-dotnet.ps1 -SkipRestore`: Ai 287, Agent 123, Tui 251, CodingAgent 631, WebUi 61, Pods 216 — all green.

### Boundary / still open

Closes only the `--list-models` membership parity (shared fuzzy matcher + swap fallback). Full upstream model catalog coverage, real provider/auth e2e for listed models, and richer TUI model-selector ranking display remain open. Tau keeps `Name` out of the fuzzy text to match upstream's `${provider} ${id}` match target.
