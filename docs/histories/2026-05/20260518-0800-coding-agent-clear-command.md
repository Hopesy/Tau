# Tau.CodingAgent /clear command

Date: 2026-05-18

## Summary

Added a `/clear` slash command that wipes the terminal screen while leaving the session, runner, settings, and history intact. The router accepts an optional `clearScreenAction` callback so the command is host-driven; `Tau.CodingAgent.Program` wires it through `InteractiveConsoleSession.ClearScreen()`, which writes the standard ANSI `ESC[2J ESC[H` sequence. Non-interactive sessions (no callback wired) return a clear "not supported" error.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`: new constructor parameter `Action? clearScreenAction`; `/clear` dispatcher invokes the callback, returns an empty `Status` so no spurious "status>" line is written, and errors when called without arguments-but-extra-args (`Usage`) or without a wired callback (`not supported`).
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`: `/clear` registered with usage + description.
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`: host forwards `() => _ui.ClearScreen()` into the router.
- `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`: new `ClearScreen()` method writes the ANSI clear-screen + cursor-home sequence via the underlying terminal; closes any in-progress streaming line first so partial buffer text doesn't linger.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: 3 new tests for callback invocation, no-callback error, and usage rejection; help-line assertion updated to include `/clear`.
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`: help-line assertion updated.
- Synced `next.md` (new bullet) and the active port plan (verification list).

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 144 tests passed (3 new + 141 prior with two help-line assertion updates).

## Decisions

- Modeled `/clear` as a host-callback rather than coupling the router to `ITerminal` or `InteractiveConsoleSession`. The router stays UI-agnostic; the host injects whatever clear-screen behavior makes sense. This keeps the existing test pattern (small lambdas instead of full fakes) working for the new command.
- Returned an empty `Status` instead of "screen cleared" so the host doesn't immediately re-print a status line under the freshly-cleared screen. `RenderCommandResult` already short-circuits on whitespace messages, so the cleanup is automatic.
- Used ANSI `ESC[2J ESC[H` rather than `Console.Clear()`. The terminal interface only exposes `Write` / `WriteLine`, and the ANSI sequence is universally supported by modern terminals (Windows Terminal, conhost with VT processing enabled, Linux/macOS terminals). Older Windows hosts without VT processing will show the sequence as text — acceptable best-effort behavior for a convenience command.
- Closed the streaming line before clearing so partial assistant deltas don't end up half-rendered after the next prompt; existing transcript invariants stay intact.
