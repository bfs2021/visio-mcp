# R20 Route modes and stored routes (stage A)

Issue #59 (with the persistence part of #58). Contract: `docs/design/routing-contract-proposal.md`.

## What this stage delivers

- `ConnectorElement.routing { mode?: "frozen", advisory?: Point[] }`. The mode is stored only when it cannot be derived: no waypoints is **automatic**, waypoints is **manual**, `frozen` is stored.
- The route the router chose is stored in `routing.advisory` for automatic orthogonal connectors (and is the route itself for frozen ones). It is derived data written only by the command engine (`commands/routes.ts` `settleRoutes`), in the same transaction and diff as the edit, so undo and redo restore routes exactly. Loading a file never writes.
- The canvas, the hidden projection, `inspect_layout`, export and the command engine all plan from the stored route (`layout/routePlanner.ts`), so one document gives one geometry on every surface.
- Operations (GUI Inspector, DrawScript, MCP): `routing.mode` on `createConnector` and `set` (`automatic`, `manual`, `frozen`; `guided` is refused with `routing_mode_unavailable`), and `reroute` (all automatic orthogonal connectors on the page, or named ones). DrawScript: `connect ... mode=`, `set <c> mode=`, `reroute [connector...]`.
- `TransactionResult.routeChanges` lists connectors whose route changed or is unresolved, with provenance and reason, in addition to `changed`.
- Frozen: end runs follow a moved end only when that is exact and safe; otherwise the stored route is kept and flagged `unresolved`. A frozen connector refuses waypoint edits (`route_frozen`).

## Evidence

| Check | Result |
|---|---|
| `tests/routeModes.test.ts` (13): stored routes, nudge, undo, idempotence, duplicate, manual, automatic, frozen (follow, obstacle, unresolved), guided refused, reroute | PASS |
| `tests/routePlanner.test.ts` (13), `script.test.ts` route modes | PASS |
| Playwright: Inspector freeze, obstacle ignored, back to automatic, Re-route button | PASS |
| vitest 469, Playwright 141, Host 166, Ipc 33, Visio 156, Contracts 24, Mcp 14, `generate:check` | PASS |

Behaviour changes to note:
- An edit that moves a shape updates the stored route of its connectors, so those connector ids appear in `changed`. Two older browser tests asserted the connector was untouched; they now compare everything except `routing`.
- The first edit after opening a document that has no stored routes writes them for every automatic orthogonal connector (each keeps the route it was drawn with). `routeChanges` stays empty when nothing moved.
- Compatibility: the schema is strict, so an older app version rejects a file that contains `routing`. No schema version bump (as for R16); no versioning policy exists.
- VSDX: the drawn route is exported as geometry as before. Frozen connectors export like automatic ones (no `ConFixedCode`) until R27 verifies a mapping in real Visio.

## Not done (remains on #59, #60, #66)

- Guided mode and segment constraints (R21 #60).
- The Visio mapping of frozen and automatic routes (R27 #66); re-importing a drawing yields manual connectors as before.
- The session-only fallback in the canvas adapter is kept for documents without stored routes.
- Manual owner check of the editor: NOT RUN.
