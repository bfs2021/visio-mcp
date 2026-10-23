# Release notes

Newest first. `tools/installer/package.ps1` refuses to build a version that has no section here, and the installer shows the sections newer than the version being replaced.

## 0.2.0 - unreleased

- Install, update and uninstall scripts for a per-user install (`tools/installer`). An update keeps your library, recovery files and settings.
- `import_visio` opens a Visio drawing as a background reference document.
- Help menu with a keyboard shortcut list, About and a command palette (Ctrl+Shift+P).
- Automatic connector routes are kept when you move something unrelated, follow a moved end without rerouting, and a connector that cannot be routed is flagged (`UNRESOLVED_ROUTE`).
- Connectors remember their route (saved in the file), can be frozen or set manual from the Inspector, and Re-route plans an automatic one again. Files saved with this cannot be opened by earlier versions.
- Select an orthogonal connector and drag the handle on any inner run to slide it sideways (snaps to the grid, shape centre-lines, guides and parallel connectors; hold Alt to turn snapping off; Escape cancels). Arrow keys nudge a focused handle, and the Inspector has a position field for each run. DrawScript: `move-segment`.

## 0.1.0

- First version installed by `tools/publish-local.ps1`.
