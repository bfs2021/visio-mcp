# W16: per-user install, update and uninstall scripts (#38)

Branch `claude/w16-install-scripts`. Scope chosen by the owner: scripts and docs first. A signed installer is **not** delivered here.

| Acceptance criterion | State |
|---|---|
| Signed installer (MSIX or MSI) installs the app and companion per user, Start menu entry | **Not met.** Per-user install and Start menu entry are done by script; nothing is signed and there is no MSIX/MSI (no certificate, WiX or Windows SDK here). |
| In-place update keeps user data and says what changed | Done, tested: data lives outside the install, release notes since the old version are printed |
| Uninstall removes the program and offers keep or delete data | Done, tested: asks interactively, `-KeepData` / `-DeleteData`, keeps data when nobody can be asked |
| Document how MCP clients are registered, ideally by a command | Done: `register-mcp.ps1 -Client claude|codex|print`, documented in `docs/install.md`. Not an in-app command. |

## Verification

| Check | Result |
|---|---|
| `tools/installer/test-installer.ps1` | 40 checks passed (scratch folders and registry key) |
| `package.ps1 -Version 0.2.0` | built a 3 MB zip; installed from it into scratch folders; app reports 0.2.0, companion 0.2.0.0, web files present; uninstalled cleanly |
| `package.ps1 -Version 9.9.9` | refused: no release notes section |
| Real `claude mcp add` / `codex mcp add` | NOT RUN (dry run only; this would change the owner's agent configuration) |
| Install on a clean machine, SmartScreen/execution-policy behaviour of a downloaded zip | NOT RUN |
| Signing | NOT RUN (no certificate) |

## Notes

- Open with entries are removed by uninstall under the key `Applications\Diagram.App.exe`. PR #53 (W15) registers them under `Agentic Diagram.exe`, which Windows would not match to the program; that is corrected on the W15 branch, and the two must merge together for Open with removal to be complete.
- `-OpenWith` calls `Diagram.App.exe --register-file-types`, which exists only once W15 (#37) is merged; do not use it with earlier builds (they would just start the app).
