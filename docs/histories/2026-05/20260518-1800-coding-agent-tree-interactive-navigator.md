# Tau.CodingAgent interactive `/tree --interactive` navigator

Date: 2026-05-18

## Summary

Added an interactive navigator over the JSONL session tree. `/tree --interactive` (or `/tree -i`) hands the current entry view to a key-driven navigator that lets the user move with `j` / `k` / `↑` / `↓`, jump to first / last with `g` / `G` (Shift-G or `End`), confirm with `Enter`, and cancel with `q` / `Esc`. The selected entry id is surfaced as a status line so the user can immediately feed it to `/fork <id>` or `/label <id>` without manual copy-paste.

This is the first interactive TUI surface inside the CLI beyond the input editor itself. It reuses the existing `IConsoleKeyReader` seam introduced by the input editor work, so testing the loop is purely deterministic.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`:
  - New `CodingAgentTreeViewItem(EntryId, DisplayLine, IsCurrentLeaf, IsOnBranch)` record so callers can manipulate entries without re-parsing the formatted string.
  - New `CodingAgentTreeSessionStore.EnumerateView(options)` that walks the filter + search + max-entries pipeline once and yields structured items.
  - Refactored `FormatTree(options)` to call `BuildViewItems` and join the produced `DisplayLine`s. Existing output is byte-for-byte identical.
  - Controller forwards `EnumerateView`.
- `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs` (new): `NavigateAsync(items, IConsoleKeyReader, TextWriter, clearScreen?, ct)` runs the key loop, redraws on each input, and returns a `Result(SelectedEntryId?, LastIndex, Frames)`. The renderer prints a header `tree navigator: N entries, selected K/N — j/k/↑/↓ move, g/G first/last, Enter select, q/Esc quit` followed by each `DisplayLine` prefixed with `>>` for the selected row and `  ` for others. Default selected index is the last entry (matches `/tree`'s leaf marker).
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`:
  - `TryParseTreeOptions` now also returns a `bool interactive` and recognises `--interactive` / `-i`.
  - Router takes an optional `treeNavigator` callback `Func<IReadOnlyList<CodingAgentTreeViewItem>, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>>?`.
  - `HandleTreeCommand` dispatches to the navigator when the flag is set and the callback is wired; falls back to error `"interactive tree navigator is not available in this mode"` otherwise. On selection emits `"selected entry <id>"`; on cancel `"tree navigator cancelled"`.
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`: `/tree` usage line now ends with `[--interactive]`.
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`: forwards an optional `treeNavigator` callback to the router.
- `src/Tau.CodingAgent/Program.cs`: lifts `SystemConsoleKeyReader` to a top-level local so it can be shared between the input editor and the new navigator. When an interactive editor exists, wires `CreateTreeNavigator(keyReader)` that adapts the navigator to `Console.Out` with an ANSI `[2J[H` clear-screen between frames.
- `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs` (new, 8 tests): empty input returns null + zero frames; Enter without movement returns the last entry; `j` / `k` move selection; lowercase `g` jumps to first, Shift-`G` to last; `q` and `Esc` both cancel; renderer emits header + `>>` highlighting; unknown keys do not redraw (frame counter stays at 1).
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` (3 new tests): interactive dispatch invokes the configured callback and returns the selected id; cancelled navigation produces a cancelled status; missing callback returns a clear error. Updated the existing `--search` usage assertion to include `[--interactive]`.

## Verification

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --verbosity minimal` — succeeded.
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --verbosity minimal` — 166/166 (155 prior + 8 navigator + 3 router interactive).
- `dotnet test tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj --no-build --verbosity minimal` — 56/56 (no regression).

## Decisions

- **Single navigator class, not a `Tui` abstraction**: an `IInteractiveNavigator` interface would be premature. The navigator's contract is "given items + key source + writer, return selection"; making it a concrete class keeps reuse trivial when more navigators land (sessions list, providers list, etc.). The router's seam is a `Func`, which decouples the host wiring from the navigator's exact shape.
- **Selection emitted as a status string, not a side effect**: returning `"selected entry <id>"` keeps the existing `/tree` contract intact (status messages only) and lets the user pipe the id into the next command. Auto-running `/fork <id>` on Enter would have been more magical but also harder to back out of and would conflict with future selection actions (label, view diff, etc.).
- **Default selection is the last entry**: matches the existing `>` marker in `/tree`'s textual view; users navigating backward from "now" is the dominant pattern when triaging an active session.
- **Filter cycle (`f`) and search (`/`) deferred**: deliberately scoped this baseline to "move + select". The view items already include `IsCurrentLeaf` / `IsOnBranch` flags so a follow-up can add filter cycling without changing the data layer.
- **ANSI clear between frames**: matches `/clear`'s precedent. Terminals that don't support ANSI will see the escape codes printed, which is acceptable for a best-effort interactive surface and unaffects the test path (tests pass a no-op `clearScreen`).
- **`--interactive` flag parser sits alongside `--search` / `--label-time`**: kept the parser monolithic for now. A second parser pass that knows about positional + flag conflicts would be a refactor we don't need yet.
