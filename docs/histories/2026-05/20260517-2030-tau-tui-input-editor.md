# Tau.Tui interactive input editor baseline

Date: 2026-05-17

## Summary

First non-stub piece of `Tau.Tui`: an `InteractiveInputEditor` plus the abstractions to read keys and render the editor without touching `Console` in tests. Until now, `Tau.CodingAgent` (and any future TUI consumer) only had `Console.ReadLine`, which means no cursor movement, no inline editing, no history, and no Ctrl-C handling. This slice fills that gap for new consumers — the existing CLI prompt path stays on `Console.ReadLine` until a later integration slice.

## Changes

- `src/Tau.Tui/Abstractions/IConsoleKeyReader.cs`: `IConsoleKeyReader` (async ReadKey) and `IInteractiveRenderer` (prompt / render / commit / cancel) interfaces.
- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`:
  - `InteractiveInputEditor.ReadLineAsync(prompt, color, cancellationToken)` returning `InputResult { Submitted | Cancelled }`;
  - handles `Enter` (commit + history append, blank lines skipped), `Backspace` / `Delete`, `LeftArrow` / `RightArrow`, `Home` / `End`, `UpArrow` / `DownArrow` for history walk (deepest-first), Ctrl-C cancel (writes draft back to `InputBuffer` for recovery), printable chars inserted at cursor, control characters ignored.
  - `InputHistory` with explicit capacity, drops consecutive duplicates, `Peek(offsetFromEnd)` for navigation.
  - `InputResult` / `InputResultKind` value types so callers don't have to interpret null strings.
- `src/Tau.Tui/Runtime/SystemConsoleInputDevices.cs`: production-side `SystemConsoleKeyReader` (`Console.ReadKey(intercept:true)`) and `SystemConsoleInteractiveRenderer` (cursor positioning + padding when the buffer shrinks; gracefully no-ops when the console doesn't support `SetCursorPosition`, e.g., CI / redirected output).
- `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`: 10 new tests across char-append + commit, backspace, delete + cursor moves, Home/End, Ctrl-C cancel, Up/Down history walk, history-append on submit, duplicate-drop, and capacity truncation. Uses local `FakeKeyReader` / `FakeRenderer` (no `Console` dependency).
- Synced `next.md` (Tau.Tui editor + 键盘体系 flipped to `[~]`) and the active port plan (new `[~]` progress entry plus a targeted-tests bullet).

## Verification

- `dotnet build .\src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 14 tests passed (4 existing + 10 new).

## Decisions

- Split key reading and rendering into two interfaces (`IConsoleKeyReader`, `IInteractiveRenderer`) rather than extending `ITerminal`. Reading and painting have different stub needs: tests inject deterministic key sequences and assert on render calls, while production needs `Console.ReadKey(intercept:true)` plus cursor positioning. Two seams keep each test focused; the existing `ITerminal` stays untouched for the `Console.ReadLine` path that `Tau.CodingAgent` still uses.
- Did **not** integrate the editor into `Tau.CodingAgent` in this slice. Tau.CodingAgent's prompt currently runs through `ITerminal.PromptAsync` + `InteractiveConsoleSession.ReadInputAsync`; swapping to the new editor would require migrating session display, slash-command handling, and Ctrl-C semantics in the same change. Keeping the editor as an additive component for now means production behavior is unchanged and reviewers can validate the new logic in isolation.
- Cleared draft / re-saved `InputBuffer.Draft` on Cancel so a Ctrl-C doesn't lose typed text — the consumer can re-show the editor with the same draft if the surrounding session deems the cancellation as "abort current attempt, keep buffer".
- Picked Up/Down arrow for history navigation (matching readline / Powershell defaults) and skipped reverse-search (Ctrl-R) for this slice. Reverse-search and Ctrl-A / Ctrl-E / word jumps can layer on top of the same key-dispatch switch without changing the abstractions.
- Made `SystemConsoleInteractiveRenderer` resilient to terminals that throw on `SetCursorPosition` / `CursorVisible` (CI runners, redirected output). Failed positioning calls are best-effort no-ops, which keeps the editor usable in non-interactive harnesses while still doing the right thing on real terminals.
