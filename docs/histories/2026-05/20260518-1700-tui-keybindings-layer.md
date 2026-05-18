# Tau.Tui keybinding customisation layer

Date: 2026-05-18

## Summary

Extracted `InteractiveInputEditor`'s previously hardcoded `switch (key.Key)` and `(key.Modifiers & ConsoleModifiers.Control) != 0` chains into an `IKeyBindingMap` lookup. Default behaviour is unchanged — every keystroke that previously had a dedicated branch now maps to a named `EditorAction` and resolves through the same table. Users can override the map by dropping a JSON file at `~/.tau/coding-agent-keybindings.json` (override path via `TAU_CODING_AGENT_KEYBINDINGS_FILE`); only the bindings that differ from the default need to be listed.

This is the first step of the broader Tau.Tui P0 work; it does not yet introduce a real component system, message/status regions, or a diff renderer. The keybinding seam is what made the other layers easier to talk about, because every key now flows through a single resolver.

## Changes

- `src/Tau.Tui/Abstractions/EditorAction.cs`: new `EditorAction` enum (Cancel, Submit, DeletePrevChar/Word, DeleteNextChar/Word, CursorLeft/Right/PrevWord/NextWord/LineStart/LineEnd, KillToLineStart/End, HistoryPrev/Next, ReverseSearch, None), `KeyBinding(Key, Modifiers)` record struct, and `IKeyBindingMap.Resolve(ConsoleKeyInfo)`.
- `src/Tau.Tui/Runtime/KeyBindingMap.cs`: concrete `KeyBindingMap` with the default chord table, `WithOverrides` for layering additional/disabling bindings (an override to `EditorAction.None` removes a default), and `KeyBindingFileStore.LoadOrDefault(path)` / `Parse(json)` that read `{ "bindings": [ { "key": "...", "modifiers": [...], "action": "..." } ] }` and fall back to `KeyBindingMap.Default` on missing file / malformed JSON / unknown enum names.
- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`: optional `bindings: IKeyBindingMap?` ctor parameter (default `KeyBindingMap.Default`); the main loop now resolves an `EditorAction` first and dispatches with a single `switch`. Character insertion happens in the `default`/`None` arm, gated on `!char.IsControl` and `(modifiers & Control) == 0` so that overriding e.g. Enter to `None` makes Enter a no-op rather than letting `\r` be inserted into the buffer.
- `src/Tau.CodingAgent/Program.cs`: reads `TAU_CODING_AGENT_KEYBINDINGS_FILE` or `~/.tau/coding-agent-keybindings.json`, hands the resulting map to the editor.
- `tests/Tau.Tui.Tests/KeyBindingMapTests.cs`: 9 new tests for the map (`Default` resolves Ctrl-C/Enter/Ctrl-Backspace correctly, unknown keys return `None`; `WithOverrides` adds new bindings and removes default ones via `EditorAction.None`; `KeyBindingFileStore` returns default on `null`/missing path, parses valid JSON, falls back to default on empty arrays or unknown enum values).
- `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`: 2 new editor-behaviour tests demonstrating that a custom map actually changes the editor's behaviour — `F1` rebound to `Submit` ends the prompt, and `Enter` rebound to `None` keeps the buffer alive until `Ctrl-C` cancels.

## Verification

- `dotnet build src/Tau.Tui/Tau.Tui.csproj --verbosity minimal` and `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --verbosity minimal` — succeeded.
- `dotnet test tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj --verbosity minimal` — 56/56 (45 prior + 11 new).
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --verbosity minimal` — 155/155 (no regression).

## Decisions

- **One enum, not a `Action<EditorState>` delegate map**: a typed enum keeps configuration declarative (the JSON file lists strings, not C# code) and makes the editor loop's `switch` easy to read. Trade-off: extending the action set requires editing the enum + the switch, which is acceptable for a P0 baseline.
- **`EditorAction.None` doubles as "disable this binding"**: when the JSON sets an existing default key to `None`, `WithOverrides` removes that entry from the map so the editor treats the key as unbound. This matches user expectations from common editors (Emacs `(global-set-key ... nil)`) and avoids introducing a separate "remove" verb.
- **Char insertion is now gated on `Control` modifier absence**: previously the editor only checked `!char.IsControl(KeyChar)`, which already rejected Enter / Backspace because those produce control chars. After the refactor, the same default arm also catches keys an override has unmapped to `None`. The `Control` gate prevents an obscure path where a key like `Ctrl-A` (which produces `\x01`) might insert if a user rebound it to `None` — `\x01` is a control char so `char.IsControl` already excludes it, but the explicit modifier check is defence in depth.
- **No backwards-compat keys removed**: every keystroke that worked before still works. The Ctrl-A / Ctrl-E / Ctrl-K / Ctrl-U / Ctrl-R chord behaviours, word-level chord movement, history navigation, and reverse search are all preserved by the default map.
- **Did not yet expose binding map in `Tau.CodingAgent.Settings`**: keeping the override surface to a single file makes the configuration story tractable for now. Settings-level integration can layer on if users start asking to round-trip key bindings via `/retry`-style commands.
