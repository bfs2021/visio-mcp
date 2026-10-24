# R23 routing over shapes on a lower level

Issue #62. Builds on R22 #61 (routing policy and precedence) and R29 #69 (stacking levels).

## The rule

The owner's decision on #62: "behind" is the stacking order, and with R29 that order is the **level** first. Layers keep their flags only; there is no layer order and no layer routing role.

- A shape, text box or picture whose level is **lower** than the connector's level is behind the connector. The connector is routed over it, not around it, and `inspect_layout` does not count it as a crossing.
- A shape on the **same or a higher** level is an obstacle exactly as before (the R08b and R22 rules decide which). Every element is at level 0 unless someone sets a level, so existing documents route as they did.
- An explicit per-element `routingPolicy.obstacle` beats the level rule (R22 precedence): `forbidden` and `avoid` are still avoided below the connector, `passThrough` is passed through above it. `automatic`, or no policy, uses the level rule.
- Protected text (`routingPolicy.text: "avoid"`) is protected whatever its level.
- A connector placed on a low level keeps avoiding the shapes in front of it (issue item 5). Running it under a shape would hide it, which is rarely wanted.
- A connector's level is inherited from its start object when it is created (R29), so a connector between two shapes on level 0 is on level 0, and a frame or background colour sent to level -1 stops blocking it.
- The legacy container rule is unchanged: a shape that holds a smaller shape is a container, not an obstacle, even if the shape inside is on a lower level.

`passesBehind(element, connectorLevel)` in `src/web/src/layout/routingPolicy.ts` is the single definition; the route planner (through `selectObstacles`) and `inspect_layout` both call it.

## What changes on screen and for agents

- Sending a frame back a level (Arrange, Move to level; DrawScript `set frame level=-1`; MCP `set` with `patch.level`) straightens a connector that was detouring around it. The planner's existing "the obstacle that forced a detour is gone" rule does the straightening; bringing the frame forward again, or undo, restores the detour.
- `inspect_layout` reports an info finding `CONNECTOR_PASSES_OVER_SHAPE` with `passedOverElementIds`, `rule: "lower_level"`, `connectorLevel` and the level of each shape passed over, so the behaviour is explainable. A lower-level shape never appears in `CONNECTOR_CROSSES_SHAPE`.
- The MCP operation guide (`editing`) and the `inspect_layout` tool description state the rule.

## Save, reload and Visio

R23 adds no stored field. Levels are saved and survive the VSDX round trip through R29's user cells; routing policies remain reported as `ROUTING_POLICY_NOT_NATIVE` (R22). Visio itself has no notion of routing over a lower level, so a connector re-routed inside Visio may avoid the shape again; that is part of R27.

## Verification

Full run on the branch (main afd6dcf plus R23): web unit 641, typecheck clean, browser 188, Contracts 50, Ipc 33, Host 214, Visio 168 (including `LegoJunctionTests`), Mcp 14, Windows 180 passed (including `ReferenceFixtureRegressionTests`) with 13 opt-in probes skipped. The reference test was run with the `.gitattributes` line-ending fix from PR #87 applied locally; without it that test fails on main on a CRLF checkout, unrelated to R23.

Focused tests:

| Test | What it shows |
|---|---|
| `routingPolicy.test.ts` (6 new) | lower level passed through; same and higher level kept; a low connector still avoids; explicit `forbidden`/`avoid` beat the rule, `automatic` does not, `passThrough` above still passes; protected text kept; container rule unchanged; no connector, no rule |
| `routePlanner.test.ts`, "route planner with levels (R23)" (6) | a 200 x 200 frame between two ends: detour on the same level; exact straight route `(100,200)-(500,200)` with the frame at -1 or the connector at +1; a kept detour straightens when the frame is sent back and detours again when it comes forward; `forbidden` at -1 still detours; DrawScript `set frame level=-1` straightens it |
| `layout.test.ts`, "a shape on a lower level is reported as passed over" | the lower shape is in `CONNECTOR_PASSES_OVER_SHAPE` with both levels; the same-level wall stays in `CONNECTOR_CROSSES_SHAPE` |
| `tests/browser/routing-levels.spec.ts` | in the real editor: Arrange, Move to level -1 on the frame straightens the committed route to `(120,300)-(700,300)`, `doc.inspectLayout` lists the pass-over and no crossing, and Ctrl+Z restores the detour |
| `ReferenceFixtureRegressionTests`, `LegoJunctionTests` | the PSGL and Lego fixtures (all level 0) route exactly as before |

## Limits

- Only levels decide "behind". Two shapes on the same level are not ordered for routing by `zIndex`; the owner's proposal on #62 deliberately keeps them obstacles, because connectors are usually created last and would otherwise pass over almost everything.
- There is no warning when a connector is put on a lower level than the shapes around it. It is drawn behind them, but because the router still avoids them it stays visible.
- Visio does not keep the behaviour on its own re-route (above).
