# Tau.Tui input editor reverse search

Date: 2026-05-17

## Summary

Added bash readline-style **reverse history search** (Ctrl-R) to `InteractiveInputEditor`. When the user hits Ctrl-R, the editor enters a search mode that walks the history newest-to-oldest, narrowing as the user types and cycling through matches when Ctrl-R is pressed again. Enter accepts and submits the matched entry; Esc / Ctrl-G cancels and restores whatever buffer was in flight before the search began.

## Changes

- `src/Tau.Tui/Abstractions/IConsoleKeyReader.cs`: extended `IInteractiveRenderer` with a `RenderSearch(string pattern, string? match, int cursorInMatch)` method so renderers can paint the `(reverse-i-search) \`pattern': match` UI without conflating it with the normal buffer paint.
- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`:
  - `InputHistory.FindContaining(pattern, startOffsetFromEnd)` returns `(match, offset-from-end, matchIndex)` for the first entry containing the pattern, starting from the given offset.
  - Ctrl-R now invokes `RunReverseSearchAsync`, which tracks the in-flight pattern + offset + current match, calls `RenderSearch` on every key, supports Ctrl-R cycling, Backspace pattern shrink, Esc / Ctrl-G cancel, and Enter accept.
  - On accept, the editor commits the matched entry as the final input (writing it to history if non-blank, same as a normal submit). On cancel, the editor restores the original buffer and re-renders the regular prompt.
- `src/Tau.Tui/Runtime/SystemConsoleInputDevices.cs`: `SystemConsoleInteractiveRenderer.RenderSearch` repaints the current line with the `(reverse-i-search) \`...': ...` prefix, pads trailing characters when the match shrinks, and tracks prompt length so subsequent `Render` calls land at the right column.
- `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`: 4 new tests covering the happy path (Ctrl-R + pattern + Enter → submits matched history entry), Ctrl-R cycling to an older match, Esc restoring the original buffer, and Backspace shrinking the pattern.
- `tests/Tau.Tui.Tests/InteractiveConsoleSessionTests.cs`: extended the local `CapturingRenderer` and `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs` `FakeRenderer` with `RenderSearch` so the interface change does not break the existing tests.
- Synced `next.md` and the active port plan with the new state of editor / keybindings.

## Verification

- `dotnet build .\src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 39 tests passed (35 prior + 4 new).

## Decisions

- Introduced a dedicated `RenderSearch` renderer entry-point rather than smuggling the search UI into `Render`. The search prompt has a different prefix shape and varies the cursor position relative to the match, not the user's typed pattern; sharing one method would have forced every renderer to special-case the prefix.
- Reused `InputHistory` directly via a new `FindContaining` helper. The history already holds the entries; adding a search method keeps the editor logic small (no second buffer) and makes the search behavior unit-testable in isolation from key dispatch.
- Mapped Enter to "accept and submit", matching bash readline default. Pressing Enter in the middle of a search commits to the current match — operators rarely want to drop back into editing mode after a successful search; if they do, Ctrl-G / Esc gives them the original buffer back.
- Mapped Ctrl-R-during-search to "cycle to older match" by bumping `offset` past the current match. If no older match exists, `FindContaining` returns null and the renderer paints an empty match — operators can hit Backspace to shrink the pattern instead of restarting the search.
- Restored the previous buffer (not an empty one) on cancel. Operators often invoke Ctrl-R mid-typing; losing their in-flight buffer would be surprising. The "original buffer" captured at search entry is exactly what they see again after Esc.
