# W15: single instance, file association and Open with (#37)

Branch `claude/w15-single-instance`.

## Behaviour

- **One window per Windows user and logon session.** The first launch owns a named pipe (`agentic-diagram.launch.v1.<user SID>.<logon session>`, `CurrentUserOnly`, `FirstPipeInstance`). A later launch finds it taken, sends the files it was given (full paths, resolved where it was started) and exits. The running window comes to the front and opens each file in a tab, after it has brought back the last session's tabs. If nothing answers within 8 s the second launch says so and exits; it never starts a second window.
- **Two Windows users:** each user (and the same user in another logon session) has a different pipe name, so each gets a window of their own and a launch is never forwarded across users. `CurrentUserOnly` also stops one user's launch connecting to another user's pipe. This matches the MCP pipe, which is scoped the same way. Data folders are per user (`%LOCALAPPDATA%`), so two users never share recovery. **Same user in two logon sessions** (for example a console session and a remote one) still shares the data folder; that case is not guarded and is left for A01 (#14).
- **Opening from the shell:** `Diagram.App.exe <file>...` accepts `.json` (diagrams) and `.vsdx` (opened through import, as Open Visio drawing does). Files that cannot be opened (wrong type, missing, over 20 at once) are listed in one message; the rest still open. Each approved path can be opened by the page once, without a dialog; `host.openJson` / `host.openVsdx` refuse any other path.
- **Open with:** `Diagram.App.exe --register-file-types` lists the app under Open with for `.json` and `.vsdx` for the current user (HKCU only, no admin); `--unregister-file-types` removes it. Nothing is registered unless asked.

## Decision for the owner: ".diagram.json is associated"

Windows keys a file association on the **last extension only**, so `.diagram.json` cannot be claimed on its own: claiming `.json` would take over every JSON file. The app is therefore offered under Open with and never made the default. To get a real double-click association, diagrams would need their own extension (for example `.diagram`), which is a file-format change and is not made here. The installer (W16) is where a default would be set.

## Also fixed on the way

- `host.openJson` / `host.openVsdx` now take the tab they are for (`documentId`, `sessionId`) from the page. Before, the host assumed the tab it had last been told was current, which a tab made a moment earlier is not.
- After opening a diagram file, the tab now marks itself saved at that path. The host's own marking is refused with `session_mismatch` when it arrives before the host has settled the new session, which left the tab named "Untitled N" and showing unsaved changes. Found with `FilesNamedByTheShellOpenAsTabs...`; the host-side cause is not fixed.

## Verification

| Check | Result |
|---|---|
| `dotnet test` Host / Ipc / Mcp / Contracts | 151 / 25 / 14 / 24 passed (6 `LaunchRequestTests`, 8 `InstanceGuardTests` new) |
| `npx vitest run`, `npx playwright test`, `tsc --noEmit` | 397 passed, 111 passed, clean |
| Windows: `ShellOpenTests` (4), BridgeTests, TabTests, Workspace, FileConflict, NewDocument | 36 passed, 1 failed: `BridgeTests.PendingMutationCrashIsUnknown` fails the same way on unmodified `main` (checked) |
| Two real launches of `Diagram.App.exe`, double-click in Explorer, Open with in Explorer | NOT RUN (the dev build would use the owner's real data folder) |
| Second Windows user / second logon session | NOT RUN (A01) |

## Limits

- Not tested end to end with two processes; the forwarding is tested over the real pipe, and opening is tested in the real window.
- A launch that carries no files only brings the window forward.
- Registration is by command line; no installer yet (W16).
