# R22 Routing Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Let users and agents set explicit hard, soft, and pass-through routing policy while preserving legacy routing for documents without policy fields.

**Architecture:** Extend the document and operation contracts first, then resolve element policies into identified obstacle records consumed by the existing pure route planner. Carry route findings through the shared projection into layout inspection; expose the same fields in the Inspector, DrawScript and MCP set operation; report policies VSDX cannot preserve.

**Tech Stack:** TypeScript, JSON Schema, generated TypeScript/C# contracts, Vitest, Playwright, .NET 10, native VSDX exporter.

**Spec:** docs/superpowers/specs/2026-10-23-r22-routing-policy-design.md

## Global Constraints

- New policy fields are optional; absent fields preserve legacy routing with no load-time writes.
- obstacle is automatic, forbidden, avoid, or passThrough; automatic or absent keeps today’s obstacle heuristic.
- clearancePt is 0–2,000 pt; omission uses the existing 8 pt base clearance, plus half the visible stroke width.
- preferredSide is shape-local and soft for dynamic endpoints; a named static port takes precedence.
- Hidden elements and hidden layers remain ignored; locked elements remain obstacles; no hidden-layer override is added.
- Preserve R19 determinism, bounded routing, stable connector-ID tie breaking, and R20/R21 mode and segment-pin behavior.
- A policy-setting transaction that makes a hard policy impossible is rejected atomically; a later geometry edit that makes an existing route impossible commits with UNRESOLVED_ROUTE and retains the prior route.
- No schema version bump; Visio export reports unsupported policy fields and keeps native geometry editable.

## Review Focus

- Legacy fixtures with absent or automatic policy must keep the same route coordinates and remain clean on load. Test in Task 2 and the R19 regression in Task 8.
- A forbidden obstacle larger than 20% of the page must block a crossing; an impossible later move must retain the previous route and report its blocker. Test in Tasks 3 and 4.
- A policy edit that creates an impossible hard route must fail without changing the revision or stored policy. Test in Task 4.
- Rotated endpoints with preferredSide must use shape-local sides; named static ports must override the preference. Test in Task 3.
- Protected text and connector labels must be avoided by other connectors while a connector ignores its own label. Test in Tasks 2, 3 and 5.

---

### Task 1: Extend the document, operation, and projection contracts

**Files:**
- Modify: contracts/document.schema.json
- Modify: contracts/operations.schema.json
- Modify: contracts/protocol.schema.json
- Generate: src/web/src/contracts/generated.ts
- Generate: src/web/src/model/validators.generated.js
- Generate: src/Diagram.Core.Contracts/Generated/Contracts.g.cs
- Test: src/web/tests/contracts.test.ts
- Test: tests/Diagram.Contracts.Tests/SchemaParityTests.cs

**Interfaces:**
- Produces RoutingPolicy with optional obstacle, clearancePt, and text members; obstacle values are automatic, forbidden, avoid, passThrough, and text values are ignore, avoid.
- Adds optional RoutingPolicy to shape, text, and connector elements; adds optional preferredSide to Endpoint and EndpointInput.
- Adds routeFindings to ConnectorProjection. Each finding has code, severity, message, elementId, and optional blockerIds. Codes are UNRESOLVED_ROUTE, ROUTING_AVOIDANCE_VIOLATION, and ROUTE_SIDE_PREFERENCE_RELAXED.

- [x] **Step 1: Write schema tests** named routingPolicy_accepts_supported_values_and_rejects_unknown_or_out_of_range_values and endpoint_preferred_side_is_shape_local_enum. Assert 0 and 2,000 are accepted for clearancePt, 2,001 is rejected, all four obstacle values round-trip, and an invalid side is rejected.
- [x] **Step 2: Run the tests and verify they fail** with the new fields rejected or missing.

  Run: npm --prefix src/web test -- tests/contracts.test.ts

  Expected: FAIL on the new routingPolicy and preferredSide cases.
- [x] **Step 3: Add the definitions and optional references** to the three source schemas. Add routingPolicy to shape, text, connector, and set-patch schemas; add preferredSide to document and operation endpoint definitions; add routeFindings to ConnectorProjection. Do not make any field required.
- [x] **Step 4: Regenerate and verify parity.**

  Run: npm --prefix src/web run generate

  Run: npm --prefix src/web run generate:check

  Run: dotnet test tests/Diagram.Contracts.Tests/Diagram.Contracts.Tests.csproj --no-restore

  Expected: generation is clean, schema parity passes, and all new validation cases pass.
- [x] **Step 5: Commit** as feat(r22): define routing policy contracts.

### Task 2: Resolve element policy and construct obstacle records

**Files:**
- Create: src/web/src/layout/routingPolicy.ts
- Modify: src/web/src/layout/obstacles.ts
- Test: src/web/tests/routingPolicy.test.ts
- Test: src/web/tests/obstacles.test.ts

**Interfaces:**
- Defines RoutingObstacle with id, rect, policy (forbidden or avoid), clearancePt, and source (element or text).
- Exports effectiveObstaclePolicy(element, endIds, endRects, freePoints, pageArea) and selectObstacles(elements, layers, endIds, endRects, freePoints, pageArea, measureWidth?, connectorId?): RoutingObstacle[]. Text bounds use the shared TextWidthMeasurer; the current connector ID prevents its own label from becoming an obstacle.
- Explicit passThrough is omitted from the result; explicit forbidden and avoid bypass backdrop and containment heuristics. automatic and absent policy use the current heuristic exactly; each selected candidate remains a hard obstacle, matching today.
- Text bounds are selected only when routingPolicy.text is avoid, use the existing text measurement/layout helpers, and are omitted for the connector’s own route.

- [x] **Step 1: Add tests** named legacy_policy_matches_existing_obstacle_selection, automatic_legacy_obstacles_keep_hard_behavior, explicit_forbidden_large_region_is_not_filtered, pass_through_is_omitted, and protected_text_uses_measured_bounds. Assert legacy obstacle IDs match the current results, explicit forbidden keeps a region over 20% of page area, passThrough creates no obstacle record, and text is ignored by default but included when requested.
- [x] **Step 2: Run the focused tests and verify they fail.**

  Run: npm --prefix src/web test -- tests/routingPolicy.test.ts tests/obstacles.test.ts

  Expected: FAIL because structured policy records and explicit override handling do not exist.
- [x] **Step 3: Implement the policy resolver** in routingPolicy.ts and make obstacles.ts return identified records. Preserve the existing hidden, hidden-layer, invisible, containment, lane and 20% rules only for absent/automatic policy.
- [x] **Step 4: Run focused tests and verify they pass.**

  Run: npm --prefix src/web test -- tests/routingPolicy.test.ts tests/obstacles.test.ts

  Expected: PASS with existing obstacle-selection tests updated to assert IDs/records.
- [x] **Step 5: Commit** as feat(r22): resolve explicit obstacle policies.

### Task 3: Route around hard/soft obstacles and honor endpoint preferences

**Files:**
- Modify: src/web/src/layout/obstacleRoute.ts
- Modify: src/web/src/layout/orthogonalRoute.ts
- Modify: src/web/src/layout/routePlanner.ts
- Test: src/web/tests/routePlanner.test.ts
- Test: src/web/tests/routeModes.test.ts

**Interfaces:**
- Search and clearance helpers consume RoutingObstacle records from Task 2. PlanInput carries the shared TextWidthMeasurer and current connector ID to obstacle selection; connector-label bounds are measured from their route and label anchor, excluding the routed connector itself.
- planRoutes returns routeFindings on PlanResult and each finding identifies its connector and any blocking element IDs.
- Preferred side is shape-local, rotated by the endpoint shape’s rotation, and used only for dynamic glue. Static named ports remain authoritative.
- Search first treats forbidden and avoid obstacles as blockers. If no route is found, it retries with avoid obstacles relaxed while forbidden obstacles remain blocking; each phase is capped at the existing BUDGET, so one connector uses at most 2 × BUDGET search expansions.
- Relaxing a soft policy emits ROUTING_AVOIDANCE_VIOLATION. Failing a hard policy emits UNRESOLVED_ROUTE. Falling back from a preferred side emits ROUTE_SIDE_PREFERENCE_RELAXED.

- [x] **Step 1: Add planner tests** named forbidden_obstacle_is_never_crossed, hard_obstacle_failure_names_blocker_and_keeps_previous_route, endpoint_shape_is_crossed_only_by_glue_stubs, clearance_includes_visible_stroke, avoid_is_relaxed_only_after_clear_search_fails, pass_through_does_not_change_route, protected_text_is_avoided, preferred_side_rotates_with_shape, and static_port_overrides_preferred_side. Assert route geometry, provenance, codes, and blocker IDs. Added blocked-side fallback and measured connector-label regressions as additional coverage.
- [x] **Step 2: Run the focused planner tests and verify they fail.**

  Run: npm --prefix src/web test -- tests/routePlanner.test.ts tests/routeModes.test.ts

  Expected: FAIL because route planning currently accepts rectangle-only obstacles and does not return routeFindings.
- [x] **Step 3: Adapt obstacleRoute helpers and routePlanner** to use policy-aware obstacle bounds; attempt the preferred side first, then retry automatic side only when necessary. Keep Frozen, Manual, Guided pins, stored advisories, and joins consistent with R19–R21.
- [x] **Step 4: Run planner and mode tests.**

  Run: npm --prefix src/web test -- tests/routePlanner.test.ts tests/routeModes.test.ts

  Expected: PASS, including the existing 60-shape/60-connector route baseline and the 5-second unit budget.
- [x] **Step 5: Commit** as feat(r22): plan routes with explicit obstacle policy. Verified focused suites (37/37), full web suite (603/603), and typecheck.

### Task 4: Enforce policy edits atomically and expose operation/DrawScript fields

**Files:**
- Modify: src/web/src/commands/planPatch.ts
- Modify: src/web/src/script/compiler.ts
- Modify: contracts/operations.schema.json
- Modify: contracts/fixtures/operation-examples.json
- Test: src/web/tests/transactions.test.ts
- Test: src/web/tests/script.test.ts
- Test: src/web/tests/contracts.test.ts

**Interfaces:**
- MCP callers use the existing set operation with routingPolicy patches and endpoint preferredSide fields; no parallel operation is added.
- DrawScript supports obstacle=, clearance=, textObstacle= for routingPolicy and fromSide=/toSide= on connectors; these compile to the same set patch as MCP.
- Policy-setting operations preflight the resulting plan. If they create a hard unresolved route, reject the whole batch without changing revision, policy, or route. Geometry changes that later invalidate an existing policy retain R19 unresolved behavior.

- [ ] **Step 1: Add tests** named failed_forbidden_policy_edit_is_atomic, set_routing_policy_updates_shape_and_connector, and script_compiles_policy_and_endpoint_side_to_set_operation. Assert a rejected operation preserves revision and object hash; a valid policy changes once and undo restores it; DrawScript produces the same typed set patch as MCP.
- [ ] **Step 2: Run the focused tests and verify they fail.**

  Run: npm --prefix src/web test -- tests/transactions.test.ts tests/script.test.ts tests/contracts.test.ts

  Expected: FAIL on unsupported routingPolicy patches, preferredSide fields, or missing atomic route preflight.
- [ ] **Step 3: Implement validation and preflight** in planPatch.ts. Limit preflight to operations that add or change hard obstacle policy or fixed-port references; do not reject later geometry edits that R19 should commit as unresolved.
- [ ] **Step 4: Add DrawScript mapping and examples** using obstacle=, clearance=, textObstacle=, fromSide=, and toSide=; ensure malformed values are compile errors and operation schema validation rejects invalid payloads.
- [ ] **Step 5: Run focused tests and verify pass.**

  Run: npm --prefix src/web test -- tests/transactions.test.ts tests/script.test.ts tests/contracts.test.ts

  Expected: PASS with atomic rejection and undo coverage.
- [ ] **Step 6: Commit** as feat(r22): expose routing policy operations.

### Task 5: Surface routing findings in projections and layout inspection

**Files:**
- Modify: src/web/src/canvas/MaxGraphAdapter.ts
- Modify: src/web/src/layout/inspectLayout.ts
- Modify: src/web/src/bridge/readTools.ts
- Modify: contracts/protocol.schema.json
- Generate: src/web/src/contracts/generated.ts
- Test: src/web/tests/layout.test.ts
- Test: src/web/tests/browser/routing-policy.spec.ts

**Interfaces:**
- Copy PlanResult.routeFindings into ConnectorProjection.routeFindings for canvas and hidden projection.
- inspect_layout emits UNRESOLVED_ROUTE with blocker IDs, ROUTING_AVOIDANCE_VIOLATION for each soft policy violation, and ROUTE_SIDE_PREFERENCE_RELAXED as info.
- CONNECTOR_CROSSES_SHAPE ignores passThrough elements and does not duplicate a soft-policy violation already represented by ROUTING_AVOIDANCE_VIOLATION.
- MCP inspect_layout returns the same codes and element IDs as the page projection.

- [ ] **Step 1: Add inspector tests** named layout_reports_hard_and_soft_routing_findings and passed_through_regions_are_not_reported_as_crossings. Assert code, severity, connector ID, blocker IDs, and no duplicate crossing for a soft violation.
- [ ] **Step 2: Run the focused tests and verify they fail.**

  Run: npm --prefix src/web test -- tests/layout.test.ts

  Expected: FAIL because route findings are not carried through projections or inspection.
- [ ] **Step 3: Carry routeFindings** from route planning through MaxGraphAdapter and update inspectLayout to apply policy-aware crossing filters.
- [ ] **Step 4: Verify editor and browser behavior.**

  Run: npm --prefix src/web test -- tests/layout.test.ts

  Run: npm --prefix src/web run test:browser -- tests/browser/routing-policy.spec.ts

  Expected: PASS; canvas and MCP inspection show the same connector and blocker identities.
- [ ] **Step 5: Commit** as feat(r22): report route policy findings.

### Task 6: Add Inspector controls for obstacle policy and endpoint sides

**Files:**
- Modify: src/web/src/components/Inspector.tsx
- Modify: src/web/src/components/Inspector.css
- Test: src/web/tests/browser/routing-policy.spec.ts

**Interfaces:**
- Shape/text selection exposes obstacle policy, extra clearance in points, and text avoidance when text exists.
- Connector selection exposes its label text policy and preferred side for each dynamic endpoint; fixed named ports remain selectable through existing controls.
- Controls commit through the existing transactional set operation and display a message when a preferred side is relaxed.

- [ ] **Step 1: Add Playwright tests** named inspector_sets_and_clears_obstacle_policy and inspector_sets_dynamic_endpoint_preferred_side. Assert controls show stored values, changes persist after selection changes, and Undo restores the prior policy.
- [ ] **Step 2: Run the browser tests and verify they fail.**

  Run: npm --prefix src/web run test:browser -- tests/browser/routing-policy.spec.ts

  Expected: FAIL because the Inspector has no R22 controls.
- [ ] **Step 3: Add policy controls** to the Inspector for shape, text, and connector selections, using the contracts and transaction fields from earlier tasks.
- [ ] **Step 4: Run the focused browser tests.**

  Run: npm --prefix src/web run test:browser -- tests/browser/routing-policy.spec.ts

  Expected: PASS, including undo and relaxed-side messaging.
- [ ] **Step 5: Commit** as feat(r22): add routing policy inspector controls.

### Task 7: Report VSDX policy compatibility limits

**Files:**
- Modify: src/Diagram.Visio/NativeVsdxExporter.cs
- Test: tests/Diagram.Visio.Tests/StructuralTests.cs

**Interfaces:**
- Export keeps connectors, shapes, ports, and visible route geometry editable.
- Each unsupported saved obstacle/text/endpoint preference policy emits a stable informational diagnostic code ROUTING_POLICY_NOT_NATIVE with elementId and unsupported field detail.
- Do not claim native Visio routing behavior or rasterize geometry. Import continues using existing migration behavior when policy data is absent.

- [ ] **Step 1: Add test** UnsupportedRoutingPolicyIsReportedWhileGeometryRemainsEditable. Assert the diagnostic code, affected element ID, a VSDX package is produced, and exported native shape/connector geometry remains present.
- [ ] **Step 2: Run the focused Visio test and verify it fails.**

  Run: dotnet test tests/Diagram.Visio.Tests/Diagram.Visio.Tests.csproj --no-restore --filter FullyQualifiedName~UnsupportedRoutingPolicyIsReportedWhileGeometryRemainsEditable

  Expected: FAIL because the exporter currently omits the R22 policy without a finding.
- [ ] **Step 3: Add informational compatibility diagnostics** in NativeVsdxExporter for policy fields that do not have verified native semantics. Do not warn for existing named static ports already carried by the exporter.
- [ ] **Step 4: Run the focused Visio tests.**

  Run: dotnet test tests/Diagram.Visio.Tests/Diagram.Visio.Tests.csproj --no-restore --filter FullyQualifiedName~UnsupportedRoutingPolicyIsReportedWhileGeometryRemainsEditable

  Expected: PASS, with existing R08c and R16 export checks unchanged.
- [ ] **Step 5: Commit** as feat(r22): report unsupported Visio routing policy.

### Task 8: Run integrated regression and record evidence

**Files:**
- Create: docs/m5/R22-routing-policy.md
- Test: existing R19–R21 route fixtures, transaction, browser, contract, MCP, and Visio suites

**Interfaces:**
- Evidence names exact commands and separates automated checks from manual Visio behavior, which R22 does not claim to validate.

- [ ] **Step 1: Run web unit, type, generated-contract, build, browser, and Windows suites.**

  Run: npm --prefix src/web test

  Run: npm --prefix src/web run typecheck

  Run: npm --prefix src/web run generate:check

  Run: npm --prefix src/web run build

  Run: npm --prefix src/web run test:browser

  Run: dotnet test Diagram.Windows.slnx --no-restore

  Expected: all pass; report any existing or environment-only failures separately.
- [ ] **Step 2: Record evidence** in docs/m5/R22-routing-policy.md: policies and precedence, focused route coordinates, route findings, atomic rejection proof, route stability baseline result, VSDX diagnostic, exact commands and counts. Mark manual Visio behavior NOT RUN and explain R27 owns native routing semantics.
- [ ] **Step 3: Commit** as docs(r22): record routing policy verification.
