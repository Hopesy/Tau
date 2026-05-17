# Tau.Tui input editor word movement

Date: 2026-05-17

## Summary

Extended `InteractiveInputEditor` with word-level cursor movement and deletion: Ctrl+Left and Ctrl+Right jump to the previous/next word boundary, Ctrl+Backspace deletes the previous word, Ctrl+Delete deletes the next word. The word boundary helpers are exposed internally for direct testing across whitespace edge cases.

## Changes

- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`:
  - left/right arrow now branch on `ConsoleModifiers.Control`: bare arrows step a char; Ctrl jumps to the next word boundary via `FindPreviousWordBoundary` / `FindNextWordBoundary`.
  - Backspace/Delete likewise branch on Control: bare keys delete a single char; Ctrl variants delete the run between cursor and the next boundary.
  - New internal helpers `FindPreviousWordBoundary` / `FindNextWordBoundary` traverse runs of whitespace and non-whitespace deterministically (skip whitespace before crossing the word).
- `src/Tau.Tui/Tau.Tui.csproj`: added `InternalsVisibleTo` for `Tau.Tui.Tests` so the boundary helpers can be tested directly.
- `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`: 6 new test cases (4 facts + 2 theory sets of inline data) covering Ctrl-Left, Ctrl-Right, Ctrl-Backspace, Ctrl-Delete and the boundary helpers for empty strings, whitespace runs, end-of-buffer, and inner cursors.
- Synced `next.md` (editor + 键盘体系 descriptions) and the active port plan (verification list entry).

## Verification

- `dotnet build .\src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 31 tests passed (17 prior + 14 new across facts and theory inline data).

## Decisions

- Reused the existing key dispatch switch rather than introducing a key-binding table. Word movement / deletion is small enough to live alongside the arrow / backspace handlers; a binding table can come in when reverse-search and Ctrl-A / Ctrl-E enter the picture.
- Defined "word" as a non-whitespace run separated by whitespace, ignoring punctuation as a boundary. This matches the typical bash readline default (`forward-word` / `backward-kill-word`) and avoids surprises like Ctrl-Left stopping after a slash inside a path.
- Used `InternalsVisibleTo` to test the boundary helpers as pure functions. The helpers are deterministic predicates over a `char` list and benefit from theory-style coverage; bumping them to `public` would expose a tiny utility API for no real consumer benefit.
- Skipped Alt+Backspace as an alias for Ctrl+Backspace. `Console.ReadKey(intercept:true)` reports Alt via `ConsoleModifiers.Alt`, which could be added if user feedback asks, but Alt sequences differ across terminals (Windows Terminal, macOS Terminal, Linux TTY) — better to ship the well-supported Ctrl variants first.
