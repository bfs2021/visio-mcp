# M2 results so far (I20, I21)

Recorded 2026-10-17, same Windows 11 machine as M1. M2 here means the full editing model (I20) and DrawScript with the transaction
drawer (I21). MCP live control (I30 and later) is not part of this record.

## Test results
| Suite | Command | Result |
|---|---|---|
| Engine, contracts, bridge, canvas logic, scripts | `npm --prefix src/web test` | 216 pass |
| Editor in a real browser (Chromium via Playwright) | `npm --prefix src/web run test:browser` | 16 pass |
| C# DTO parity with the schemas | `dotnet test tests/Diagram.Contracts.Tests` | 24 pass |
| Host services, router, asset and file safety | `dotnet test tests/Diagram.Host.Tests` | 69 pass |
| WPF shell with real WebView2 | `dotnet test tests/Diagram.Windows.Tests` | 10 pass |
| Generated contracts match the schemas | `npm --prefix src/web run generate:check` | up to date |
| Production bundle | `npm --prefix src/web run build` | builds |

Evidence: `docs/m2/evidence/I20/grouped-and-aligned.png` (a group top-aligned with another shape, dashed group frame, Arrange panel) and
`docs/m2/evidence/I21/script-and-transactions.png` (a diagram a script built, with the transaction record and the script text).

## I20: full mutations, groups, arrange
New operations, all in the schema bundle and generated into both languages: `duplicate`, `group`, `ungroup`, `align`, `distribute`, `setGap`,
`reorder`, `addPage`, `reorderPage`, `duplicatePage`, `deletePage`, `addLayer`, `setLayer`, `assignLayers`, `deleteLayer`, `setAsset`,
`removeAsset`, `replaceAsset`. `move`, `resize`, `rotate` and `delete` now accept groups.

- **Groups** hold members in page coordinates. Their bounds are derived (the tight union of members' rotated rectangles, connector fallback
  bounds, waypoints and free ends; never an auto-route) and their own rotation stays 0. A member edit rewrites and reports every ancestor
  whose bounds actually changed; an external shape moving leaves the group's bytes alone. Moving, scaling and turning a group transform
  its members; a turn takes `deltaDeg` and an optional pivot (the default is the group centre), and `angleDeg` on a group is refused with
  the reason `group_rotation_requires_delta`. Non-uniform scaling is allowed only when every member is at a right angle (axes swap at 90/270).
- **Deleting a group** needs an explicit choice (`children: "delete"` or `"ungroup"`); removing a group's last member without one is refused (`empty_group`).
- **Arrange** uses axis-aligned visual bounds, input order only breaks ties, and groups are single targets. Align, distribute (three or more) and set-gap
  change only what has to move; unchanged objects keep their exact bytes. `planArrange` returns the same result as plain `move` operations.
- **Z order**: front and back place the targets beyond the current extremes and touch nothing else; forward and backward swap with the
  neighbour passed, and that neighbour is reported. Groups move as a block and stack with their topmost member.
- **Structure**: pages (add, rename and resize through `setPage`, reorder, duplicate, delete) and layers (add, set, assign, delete) resolve their
  dependencies in the result, and undo restores them exactly, including page order and layer order. Assets can be edited, replaced on named
  images or everywhere, and removed only when nothing references them; removing one and undoing keeps the catalogue order.
- **Duplicate** copies a set (groups bring their subtree), remaps connectors between the copied elements, refuses a connector that points outside
  the set unless `keepExternalLinks` is set, and drops aliases unless `as` (one copy) or `keepAliases` (numeric suffix) is given.
- **Targets** resolve UUID, then page alias, then exact name, in `resolveTarget` and in the planner; an ambiguous name lists its candidates.
- **GUI**: an Arrange menu, an Arrange panel in the inspector, a right-click menu and shortcuts all render one shared action list. Clicking
  a member selects its whole group (Ctrl+click reaches in), a dashed frame marks a selected group, the inspector shows a group's
  geometry and a "Turn by" field, Delete removes a complete group as one object, and Duplicate (Ctrl+D) now goes through the engine so
  groups survive.

## I21: DrawScript and the transaction drawer
- `src/web/src/script/{lexer,parser,compiler}.ts`: a small hand-written lexer and parser with line and column spans, and a typed compiler. No `eval`, network, loops or files.
- Every command family in the source specification compiles: page add/use/rename/size, add shape/text/image, connect, group, set, move, resize,
  rotate, delete, duplicate, align, distribute, set-gap, z, layer add/set/assign, asset import/replace. Extras recorded as decisions: `replace-all`,
  `uuid=` for explicit ids, `fromAlias=`/`fromPort=` for ambiguous dotted aliases, `fromX=`/`fromY=` for free ends.
- Typed values: dimensions need units (mm, cm, in, pt, px), angles need `deg`, colours are quoted `"#RRGGBB"` or `"#RRGGBBAA"` (a bare `#` is a comment), booleans are
  `true`/`false`. `hcenter` is accepted for `center`; `angle=` and `by=` cannot be mixed; a group takes `by=` and a `pivotX=`/`pivotY=` pair; absolute and delta
  moves cannot be mixed. Unknown fields and commands are errors with a position.
- The compiler allocates every identity itself and resolves every target to a UUID (including elements the script created), so compiled operations
  do not depend on the request's page scope. `page use` is compile-time context only: it emits nothing, creates no revision and does not change the GUI's page.
  An absolute `move x= y=` compiles to a `resize` with only x and y, which leaves the size alone.
- Script requests are bounded exactly like apply requests: 1 MiB of source and 1,000 operations are checked before planning, each compiled operation
  is schema-validated, and a planning failure carries the line of the command that caused it (`details.line`, `column`, `command`).
- The engine checks scope, then the duplicate-transaction cache, then the revision, and only then compiles, so a retried script neither allocates
  new ids nor runs twice. Validate plans against the current revision and commits nothing; Run uses the validated revision, so a document that has moved on is refused.
- **Drawer**: View, Script drawer or the toolbar button opens a Script tab (Validate, Run, diagnostics with line and column, result with created/changed/deleted counts) and a
  Transactions tab (revision, source, created/changed/deleted/page/layer/asset ids, and the script text for script transactions). "Undo this transaction" is enabled
  only for the transaction on top of the undo stack; an earlier one explains why it is not.

## Bugs and design decisions found while building
- Group membership lives on the group, so grouping rewrites no member and is reported as one created element.
- The z-order test names suggested reordering shifts intervening objects; "front" and "back" were made to touch only their targets, and only the one-step moves report a neighbour.
- Selecting a group in the canvas is a selection of its members, collapsed by the model, because groups have no cells. The cost is that handles show on each member; moving, copying,
  deleting and arranging work on the group, and the group's own size and turn are in the inspector.
- A one-line shell heredoc kept failing in this environment, so larger files were written with the file tool.

## Known gaps, carried forward
1. DrawScript has no `if`, loops or variables by design. Layer, asset and page references are by name or id; there is no way yet to list prepared assets for the script from the GUI (the host passes `preparedAssetRefs`; I50 adds the catalogue UI).
2. The inspector has no page-tab strip, layer panel or asset catalogue yet (the operations exist; I50 owns the UI).
3. Group handles are per member while selected; there is no single group resize or rotate handle on the canvas. The inspector does both.
4. `hidden` on a group is not offered, and hiding or locking a group does not hide or lock members visually yet. Locking a group does block edits to its members.
5. Copy and paste (as opposed to Duplicate) still flattens groups.
6. Equal-spacing guides while dragging are best effort and not built.
7. The second-Windows-user pipe check from M0 remains open, as agreed.
