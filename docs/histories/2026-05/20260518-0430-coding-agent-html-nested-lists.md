# Tau.CodingAgent HTML nested list rendering

Date: 2026-05-18

## Summary

Promoted HTML transcript list rendering from a single-level flat list to a real indent-aware nested list. The renderer now opens a fresh `<ul>` or `<ol>` whenever a list line is indented deeper than the current top of the list stack, pops the stack on a shallower indent, and reopens the proper kind when the same indent level switches between ordered and unordered. Code-fence content is unaffected; ordered/unordered detection at indent zero behaves exactly as before.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`:
  - `TryParseUnorderedListItem` and `TryParseOrderedListItem` now have overloads that also return the leading whitespace count (`indent`), with the old single-out signatures preserved as thin wrappers so other callers continue compiling.
  - `RenderMarkdownBlockContent` replaces the single `listKind` variable with a `List<(MarkdownListKind, int Indent)>` stack and dispatches through a new `AdjustListStack(kind, indent)` helper that pops deeper-indent frames, swaps mismatched kinds at the same indent, and pushes a new frame on deeper indent.
  - `CloseList()` now drains the whole stack so explicit "leave list" branches (blockquote start, heading, HR, paragraph, end of content) still close all open lists in the right order.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: new `TryHandleAsync_ExportHtmlCommand_RendersNestedLists` regression with three outer bullets, an inner bullet pair, an ordered inner list under a different outer bullet, and a final outer bullet, verifying that the HTML contains at least two `<ul>` opens, an `<ol>` for the inner ordered block, and the relative order of the items.
- Synced `next.md` (richer rendering bullet) and the active port plan (verification list entry).

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 124 tests passed (1 new + 123 prior).

## Decisions

- Kept the existing single-out overloads of the list parsers as wrappers. The new indent-aware overloads return both `itemText` and `indent`; other callers (task-list parser, etc.) only care about the text. Keeping both signatures avoids touching unrelated parser sites that don't need indent information.
- Indent comparisons use raw leading-whitespace count rather than a fixed multiplier (e.g., "every 2 spaces is one level"). Real Markdown content varies — Tau transcripts include both 2-space and 4-space indents — so comparing actual indents lets the renderer follow whatever convention the model emitted, with the renderer only caring about "more / less / same".
- The same-indent kind switch closes the current list and opens a new one rather than trying to merge mixed kinds at the same level. GitHub-style Markdown also treats those as separate lists; keeping the same behavior avoids visually-broken renders where a single `<ul>` accidentally contains both unordered and ordered items.
- Did **not** support items continuing onto wrapped lines or nested paragraphs inside a list item. The renderer currently treats each line as a discrete list item; multi-line list items (with hanging-indent paragraphs) remain a follow-up. The minimal indent-aware behavior unblocks the typical "one-line items, two levels deep" transcript pattern.
