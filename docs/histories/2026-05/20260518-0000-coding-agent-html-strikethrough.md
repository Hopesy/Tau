# Tau.CodingAgent HTML strikethrough rendering

Date: 2026-05-17

## Summary

Continued the HTML transcript richer-rendering line by adding Markdown strikethrough rendering. Plaintext segments containing `~~text~~` now emit `<del>text</del>`, with the same safety rails as the existing inline emphasis: inline-code keeps the literal tildes, fenced code blocks preserve them as code, and a trailing-whitespace before the closing `~~` (or a newline inside the span) refuses the match.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`:
  - `AppendPlainTextMarkup` now branches to `TryParseStrikethroughSpan` between emphasis detection and bare-URL detection;
  - `TryParseStrikethroughSpan` looks for a `~~`-prefixed run, requires a boundary (start-of-text or non-alphanumeric prior char) before the opener, refuses adjacent whitespace on either side, refuses runs that span a newline, and verifies a boundary after the closer (matching the emphasis contract);
  - the rendered span recursively pipes the inner text through `AppendPlainTextMarkup`, so nesting like `~~**both**~~` produces `<del><strong>both</strong></del>`.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: new `TryHandleAsync_ExportHtmlCommand_RendersStrikethroughSpans` verifying the success path, nested emphasis, the trailing-whitespace rejection, inline-code preservation, and fenced-code preservation.
- Synced `next.md` (richer rendering bullet) and the active port plan (verification list).

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 122 tests passed (1 new + 121 prior).

## Decisions

- Followed the existing inline emphasis contract (boundary characters, no leading/trailing whitespace, recursive content rendering) rather than inventing a separate set of rules. Strikethrough's GitHub-flavored Markdown semantics are essentially the same as emphasis with a different marker, so reusing the heuristics keeps behavior coherent.
- Refused strikethrough across newlines. Multi-line `~~` runs in real transcripts are almost always paragraph-spanning quotes where the closing tildes belong to a different sentence; without the constraint, the parser would happily span paragraphs and eat code spans inline (the failing test that surfaced this issue grabbed text up to a backtick inside `~~code~~` two lines down). Keeping it single-line matches the practical Markdown contract and avoids leaking into unrelated content.
- Did not collapse strikethrough into the emphasis parser. The marker character (`~`) needs run-length two (not one or three), and falling under the same `'*' or '_'` branch would require special-casing the marker; a dedicated parser is clearer and only ~30 lines.
