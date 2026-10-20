# W12: Help menu, keyboard shortcut list, About and command palette (#34)

Branch `claude/w12-help-palette`.

## What the person gets

- **Help > Keyboard shortcuts** lists every menu command that has a key, plus the keys that are not menu commands (nudging, Esc, Ctrl+click, tab switching). Commands with no key are named at the bottom, so the list is complete.
- **Help > About** shows the app and build version, the runtime, the MCP protocol version, the MCP companions connected now (each with the version it reported) and the data folders: application data, picture bytes, asset library, recovery, browser profile and exports.
- **Ctrl+Shift+P** (or Help > Command palette) opens a palette. Typing filters by every word, matching the command name or its menu; names that start with the query come first. Up/Down move, Enter runs, Esc or the same key closes it. A disabled command (for example Delete with nothing selected) does nothing.
- While a Help panel is open, other editor shortcuts are ignored, as they are for the other dialogs.

## How it is built

One list of commands (`src/web/src/editor/appCommands.ts`) now feeds the menu bar, the shortcut list and the palette, so they cannot disagree. Arrange commands come from the shared arrange actions that the context menu and Arrange panel already use. The key handler in `App.tsx` is unchanged apart from Ctrl+Shift+P; the shortcut list is not generated from it, so a new key needs a line in the command list or in `fixedShortcuts`.

About comes from a new page-to-host call, `host.app.about`. The MCP shim now sends its assembly version in its first frame (`clientVersion`). The host keeps it only if it is a short run of letters, digits and `.-+_` (`Wire.CleanVersion`), because the About box shows it. The label stays the server's (`Client N`); caller text is still never echoed. The agent view still has no host services.

## Verification

| Check | Result |
|---|---|
| `npx vitest run` | 404 passed (7 new in `tests/help.test.ts`: search ranking, fixed shortcuts, panel state, late About answer dropped) |
| `npx playwright test` (all) | 116 passed; 5 new in `tests/browser/help.spec.ts` |
| `dotnet test tests/Diagram.Ipc.Tests` | 25 passed (version listed, odd version dropped, label never the caller's) |
| `dotnet test` Host, Mcp, Contracts | 145, 14, 24 passed |
| Windows: `LiveControl`, `TabTests`, `Workspace` | 22 passed, including `AboutNamesTheBuildTheConnectedShimWithItsVersionAndTheDataFolders` (real shim over the real pipe) |
| `tsc --noEmit` | clean |

Manual check in the installed app (Help menu look, About with a real agent connected, palette feel): NOT RUN.

## Limits

- The MCP companion has no version of its own: it reports the shim assembly version, and the build/commit shown is whatever the app was built with (empty when the build embeds none).
- The shortcut list is a maintained list, not read from the key handler.
- Preferences for shortcuts (#35) and a Help > documentation link are out of scope.
