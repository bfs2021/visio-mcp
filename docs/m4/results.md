# M4 results so far (I40)

Recorded 2026-10-17, same Windows 11 machine, Visio 16.0 build 20430.

## What I40 delivered
- Own VSDX writer and reader (`src/Diagram.Visio`) that emit Visio XML directly: real geometry formulas, text, pictures, connectors with glue, groups, layers, guides, page sheet, and `Agent*` cells that carry identity.
- Guarded import (`PackageGuard`), deterministic identity repair, placeholders for what is not understood, a compatibility report for both directions. Matrix: [docs/vsdx-compatibility.md](../vsdx-compatibility.md).
- Lifecycle: open prepares the file outside the editor queue, stores pictures durably, and replaces the document only if the editor is where the open began. Export is a barrier snapshot, needs agreement when anything is lost, is read back before it is saved, and never marks the document saved.
- App: File > Open Visio drawing, File > Export to Visio with a compatibility dialog ("Export anyway" repeats the export with the chosen path). MCP `export_document` (vsdx, svg, png, jpeg) writes only into the open document's folder or the export folder.

## Test results
| Suite | Result |
|---|---|
| `npm --prefix src/web test` | 249 pass |
| `npm --prefix src/web run test:browser` | 28 pass (2 new: loss confirmation, cancel writes nothing) |
| Contracts / Host / Ipc / Mcp | 24 / 69 / 15 / 11 pass |
| Visio.Tests | 114 pass (includes 10 hostile packages, 4 real-Visio checks) |
| Windows.Tests | 20 pass (new: `export_document` to approved folder; outside path, traversal, bad extension, bad format refused; revision unchanged) |

## Real Visio evidence
`docs/m4/evidence/I40/visio-com.json`: opens without repair (12 shapes, 4 connectors, 1 group, 1 picture, 2 layers); static glue follows a move and dynamic glue reroutes; group resize scales members; Visio SaveAs round-trips 14 elements with identical UUIDs, bounds within 0.01 pt; layer hide/lock/no-print honoured.

## Known gaps
1. Files from tools other than this app and Visio's own save have no corpus yet; I41 owns that.
2. SVG pictures export only when the editor can rasterise them.
3. Curves, waypoints, cylinder and cloud are approximated in Visio (reported, needs agreement).
4. Word activation of embedded drawings is I41.
5. The second-Windows-user pipe check from M0 remains open.
