# M1 results (I10, I11, I12)

Recorded 2026-10-17, Windows 11, .NET SDK 10.0.402, Node 24.14.1, WebView2 runtime 155.0.4283.60.
M1 is a usable manual editor: a WPF shell hosting a React and maxGraph canvas, one-page editing with undo/redo and JSON save/open,
all of it going through one canonical command engine. No agent or MCP tooling is wired to the app yet (I30).

## Test results
| Suite | Command | Result |
|---|---|---|
| Engine, contracts, bridge, canvas logic | `npm --prefix src/web test` | 115 pass |
| Editor in a real browser (Chromium via Playwright) | `npm --prefix src/web run test:browser` | 9 pass |
| C# DTO parity with the schemas | `dotnet test tests/Diagram.Contracts.Tests` | 24 pass |
| Host services, router, asset and file safety | `dotnet test tests/Diagram.Host.Tests` | 69 pass |
| WPF shell with real WebView2 | `dotnet test tests/Diagram.Windows.Tests` | 10 pass |
| Generated contracts match the schemas | `npm --prefix src/web run generate:check` | up to date |
| Production bundle | `npm --prefix src/web run build` | builds; contains no test window |

Run order for a clean checkout: `npm --prefix src/web ci`, `npm --prefix src/web run build`, then the commands above. `dotnet run --project src/Diagram.App` opens the editor (it needs the built `src/web/dist`).
Evidence: `docs/m1/evidence/I12/editor.png` (a hand-built diagram in the browser) and `docs/m1/evidence/I11/editor-in-webview2.png` (a change applied over the bridge, shown in the real shell).

## I10: canonical engine
- One schema bundle (`contracts/*.schema.json`) generates the TypeScript DTOs, the C# DTOs and precompiled validators (`tools/generate-contracts.mjs`). 24 C# tests round-trip a sample of every shared DTO, so both languages describe one wire format.
- `CommandEngine` is the only writer. Every request goes through one queue: scope, then the duplicate-transaction cache, then the base revision, then an all-or-nothing plan and one commit.
- Plans produce resolved forward and inverse diffs. Undo and redo replay them as new revisions and restore exact serialization. Unchanged elements keep object identity.
- Operations cover create shape/text/image/connector, set, move, resize, rotate, delete, registerAsset and setPage. Aliases resolve inside a batch, locks are enforced, and deleting a connected shape is a `dependency_conflict` unless its connectors go too.
- Bounded result cache (256 entries, 16 MiB), bounded history (1,000), a committed-event stream with sequence numbers, and queue barriers for snapshot, replace and mark-saved.

## I11: shell, assets, files
- The page is served from a fixed virtual origin with a strict CSP and no `unsafe-eval`. Child frames, other origins, navigation, new windows and remote fetches are all refused. Validation runs from precompiled code, so the page needs no eval.
- `BridgeRouter` uses host-generated correlation ids, settles pending calls as `timeout_unknown` (outcome unknown) when the renderer dies, and never retries.
- JSON save and open: atomic same-directory writes. A save marks only the revision it wrote, and only for the session it snapshotted. A late open cannot replace newer edits or a newer session. Limits (64 MiB, depth 128, 1 MiB strings), SVG allowlist sanitising before hashing, raster header budgets before anything is stored, and device/UNC/stream path refusal.
- Real-WebView2 testing found three bugs the unit tests could not: a CommonJS import that broke in the bundle, a UI-thread deadlock when serving assets, and an invalid CSP test. All fixed.

## I12: editor
- Menu bar, toolbar, shapes panel with three libraries, inspector (geometry in mm/cm/in/pt/px, style, text, connector, picture, layers), unit-labelled rulers, a finite A4 page with a dot grid, pointer-centred zoom, and a status bar.
- Move, resize, rotate, connector bends and in-place text editing become one transaction each, planned from the snapshot the gesture started from. A gesture that finds the document changed under it is discarded with a message, never applied over newer state.
- maxGraph's own edit paths are replaced by ones that emit intents, so the canvas changes only when the engine commits. Snapping (absolute grid coordinates) happens in the planner, so the committed value is the snapped one.
- Copy, paste and duplicate regenerate identities, make aliases unique and re-attach copied connectors. Pictures (PNG, JPEG, BMP, sanitised SVG) become independent image objects with the hash of their exact bytes.
- Looking at a screenshot caught a bug the passing tests had missed: maxGraph's svg painted underneath the page sheet, so shapes were invisible. The tests now check what is under a shape's centre, not only the document.

## Known gaps, carried forward
1. Static-port hover handles are not built; connectors use dynamic glue (D3 asks for hover handles on static ports).
2. Only one page is shown; page tabs, layer management UI and z-order arrive with I20 and I50. Groups can exist in a loaded document but cannot be created or edited yet.
3. Pan by space-drag or middle button is implemented but not covered by a test.
4. Recovery journal, crash restore and the admission gate for agent calls are I30 and I51. The gesture interlock callback (`onGestureState`) is in place and tested.
5. Orthogonal connectors use maxGraph's elbow style with one bend handle; waypoints are stored but multi-segment editing is not offered.
6. A maxGraph 0.24.0 defect is worked around (Escape during a rotate drag); the gesture hooks depend on handler internals, so keep the version pinned.
7. The second-Windows-user pipe check from M0 stays open, as agreed.
