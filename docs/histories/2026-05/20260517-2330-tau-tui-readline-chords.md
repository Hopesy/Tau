# Tau.Tui input editor readline chords

Date: 2026-05-17

## Summary

Added bash readline-style chord shortcuts to `InteractiveInputEditor`: Ctrl-A jumps to line start, Ctrl-E jumps to line end, Ctrl-K kills to end of line, Ctrl-U kills to start of line. These complement the prior word-movement slice and bring Tau's CLI closer to the keystroke vocabulary users already know from bash, zsh, and most readline-based REPLs.

## Changes

- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`: added a guarded `Ctrl-` chord block after the existing Ctrl-C cancel check; A/E reposition the cursor, K removes characters from cursor to end, U removes from start to cursor (resetting cursor to 0). Each branch ends with an explicit `_renderer.Render(...)` + `continue` so the main switch sees a fresh iteration.
- `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`: four new `Fact` tests covering Ctrl-A, Ctrl-E, Ctrl-K, Ctrl-U with realistic buffer states.
- Synced `next.md` and the active port plan with the new chord coverage.

## Verification

- `dotnet build .\src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 35 tests passed (31 prior + 4 new).

## Decisions

- Handled the Ctrl chord set as a single guarded block separate from the bare-arrow switch. The bare keys (`LeftArrow`, `RightArrow`, `Backspace`, `Delete`) already branch on `ConsoleModifiers.Control` for word-level behavior; A/E/K/U are *only* meaningful with Control, so an explicit Ctrl-only block reads cleaner and prevents accidental hits when typing the letters `a`, `e`, `k`, `u`.
- Skipped a clipboard / yank buffer (Ctrl-Y). The kill chords just discard text for now — yank is useful but introduces shared state (kill ring) and an API for paste; that warrants its own slice once we know what shape `Tau.CodingAgent` wants to expose.
- Continued issuing `_renderer.Render` from each branch so the visible cursor and buffer track the chord effect immediately, matching the bare-key behavior in the surrounding switch.
