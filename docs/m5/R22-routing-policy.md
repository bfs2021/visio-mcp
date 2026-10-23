# R22 routing policy

Issue #61. Design: [R22 routing policy design](../superpowers/specs/2026-10-23-r22-routing-policy-design.md). Implementation plan: [R22 implementation plan](../superpowers/plans/2026-10-23-r22-routing-policy.md).

## Behavior delivered

- `routingPolicy` is optional on shapes, text and connectors. An absent or `automatic` obstacle policy preserves the existing obstacle heuristic; `forbidden` is hard, `avoid` is relaxed only after a clear route cannot be found, and `passThrough` contributes no obstacle. `clearancePt` adjusts obstacle clearance, including visible half-stroke. Text is an obstacle only when `text: "avoid"` requests it, and its bounds use measured text layout.
- Dynamic endpoints may save a shape-local `preferredSide`. It is advisory: if no clear route is possible from that side, the planner retries automatically and reports `ROUTE_SIDE_PREFERENCE_RELAXED`. Static named ports continue to take precedence.
- Policy edits use the normal atomic transaction path. A new hard policy that makes an existing connector unrouteable is refused without changing the revision or document. A later geometry change that makes a route unrouteable retains the prior route and reports `UNRESOLVED_ROUTE` with blocker IDs. Soft crossings are reported as `ROUTING_AVOIDANCE_VIOLATION`.
- The Inspector exposes obstacle policy and clearance on shapes/text, text avoidance when text is present, connector-label text policy, and preferred sides for dynamic endpoints. Changes survive selection changes and undo normally.
- The native VSDX exporter retains editable shapes, connectors, ports and visible route geometry. It emits informational `ROUTING_POLICY_NOT_NATIVE` diagnostics for each saved policy field Visio does not preserve, with the affected element ID and unsupported field in `Action`. Dynamic endpoint preferences are reported; named static ports remain exported without a policy warning.

## Verification

| Check | Result |
|---|---|
| `npm --prefix src/web test` | PASS — 608 tests |
| `npm --prefix src/web run typecheck` | PASS |
| `npm --prefix src/web run generate:check` | PASS |
| `npm --prefix src/web run build` | PASS — Vite emitted existing dependency `eval` and large-chunk warnings |
| `npm --prefix src/web run test:browser` | PASS — 180 tests, including all 3 `routing-policy.spec.ts` cases |
| `dotnet test tests/Diagram.Visio.Tests/Diagram.Visio.Tests.csproj --no-restore` | PASS — 163 tests |
| `dotnet test Diagram.Windows.slnx --no-restore` with `C:\Program Files\dotnet` on `PATH` | PASS — 593 tests total: Contracts 50, Host 202, Visio 163, IPC 33, Windows 131, MCP 14 |

The focused geometry checks include a 600 pt straight route between shapes at `(0,100)` and `(700,100)`, with east/west ports at `y=130`; adding a forbidden wall covering the page retains the exact prior points and reports the wall ID. A full-page `avoid` obstacle produces `ROUTING_AVOIDANCE_VIOLATION` only after clear search fails. A preferred east side blocked by the wall at `(110,80,40,160)` falls back to the automatic south side and reports the relaxed preference. The small stability case keeps its two routes exactly after an unrelated 1 pt nudge and reports no route changes. The 60-shape/60-connector stress baseline regression also passes; its coordinates were intentionally updated in Task 3 for visible stroke-aware clearance.

`failed_forbidden_policy_edit_is_atomic` proves a rejected edit leaves the revision and exact element hashes unchanged. `inspector_sets_and_clears_obstacle_policy` and `inspector_sets_dynamic_endpoint_preferred_side` verify persisted Inspector values, reselection and undo. `UnsupportedRoutingPolicyIsReportedWhileGeometryRemainsEditable` verifies five field-specific informational diagnostics, a native shape outline, and all four connector path rows in the emitted package.

## Limits and acceptance

The VSDX compatibility test inspects the produced package structure. The complete automated Windows suite also passed its installed-Visio integration tests, but this does not constitute human acceptance. Manual owner review of routing appearance and behavior in Visio is **NOT RUN**. Native Visio policy semantics remain deferred to R27; this implementation does not claim that Visio will enforce the editor's obstacle or endpoint preferences.

The first parallel integration attempt was discarded: the Windows process lacked `dotnet` on its inherited `PATH` and interrupted the browser dev server. Final browser and .NET runs were performed separately; the browser suite passed 180/180, and the solution passed 593/593 with the SDK directory on `PATH`. Test-generated tracked acceptance/demo/performance outputs were restored after verification.
