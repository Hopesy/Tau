# Tau.CodingAgent input editor integration

Date: 2026-05-17

## Summary

Wires the new `InteractiveInputEditor` into `Tau.CodingAgent`'s interactive REPL prompt. The previous slice landed the editor as a Tau.Tui library but left `Console.ReadLine` in place for CodingAgent; this slice makes the editor the default whenever the process owns an interactive console (no redirected stdin/stdout). Redirected I/O and the new `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1` opt-out fall back to the legacy `ReadLine` path.

## Changes

- `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`: optional `InteractiveInputEditor` constructor parameter; `ReadInputAsync` now uses the editor when present (writing back `InputBuffer.Draft` and treating editor cancellation as `null` input to match the `Console.ReadLine` EOF semantic), and falls back to `ITerminal.PromptAsync` otherwise.
- `src/Tau.CodingAgent/Program.cs`: helper `CreateInteractiveEditorIfAttached()` returns a `new InteractiveInputEditor(new SystemConsoleKeyReader(), new SystemConsoleInteractiveRenderer())` when the process is interactive and `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR != 1`; otherwise returns `null` so the existing `Console.ReadLine`-backed path remains for piped input or hostile terminals.
- `tests/Tau.Tui.Tests/InteractiveConsoleSessionTests.cs`: 3 new tests verifying editor-preferred input, terminal fallback, and that editor cancellation is surfaced as `null` from `ReadInputAsync`.
- Synced `next.md` and the active port plan with the new integration state.

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 17 tests passed (14 prior + 3 new).
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` — passed (383 tests across Tau.Ai 174 / Tau.Agent 54 / Tau.Tui 17 / Tau.CodingAgent 120 / Tau.Pods 18).

## Decisions

- Detected interactive mode via `Console.IsInputRedirected || Console.IsOutputRedirected`. Either redirection means we cannot rely on `Console.ReadKey(intercept:true)` and cursor positioning; the editor would either throw or render garbled output. Falling back to `Console.ReadLine` keeps piped input, test harnesses, and CI runners working without surprises.
- Added `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1` as an explicit escape hatch. Some terminals (e.g., older Windows consoles, certain SSH multiplexers) still behave oddly with `SetCursorPosition`; the env var lets operators downgrade without rebuilding.
- Mapped editor `Cancelled` results to `null` from `ReadInputAsync`, matching the existing `Console.ReadLine` semantic. `CodingAgentHost.RunAsync` treats `null` as a shutdown signal, which makes Ctrl-C in the editor produce the same effect as Ctrl-D / EOF previously did — a single, well-understood exit path.
- Did not delete `InteractiveConsoleSession`'s `_terminal` field. `WriteUserMessage`, `WriteAssistantText`, status / tool / error output still go through the terminal interface; only the read path now branches.
