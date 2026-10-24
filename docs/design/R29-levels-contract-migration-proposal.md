# R29 levels contract and migration proposal

Status: approved for implementation<br>
Issue: [#69 — R29 levels](https://github.com/rp2839/visio-mcp/issues/69)<br>
Branch: `codex/r29-levels`

## Decisions requested

1. Keep canonical `schemaVersion` at 1 and make `level` optional with semantic default 0, rather than forcing every saved document to version 2. This avoids rewriting otherwise unchanged files and preserves compatibility for older strict readers when no non-default level is used. A file with a nonzero level necessarily contains a newer field and older strict readers will reject it, as they do for other newer fields. A later version bump can introduce the shared migration mechanism when a change actually requires an incompatible document-wide interpretation.
2. Move an object to the top of a target level by assigning `max(page.zIndex) + 1`; `zIndex` remains page-global and unique. Reorder continues to act only within the selected level, with page-wide renumbering in effective order as needed to preserve uniqueness.
3. Move a group by the level offset from its current top member, applying that offset to all leaves. This preserves intentional depth differences inside a group; reject the atomic operation if any leaf would exceed -127..+127. A group’s top member is the leaf with the greatest `(level, zIndex)` pair, and the group takes that leaf’s level and zIndex.
4. On VSDX import, preserve `AgentLevel` as authoritative. If Visio’s stored page order conflicts with the effective order implied by levels, report an informational `level_order_conflict`; same-level order follows the Visio file. This preserves round-trip level and routing/hop semantics while making a manual cross-level reorder visible to the user.
5. Keep `AgentSchemaVersion` tied to the document schema version; do not bump it for levels. `AgentLevel` is optional, and a missing cell means level 0.
6. The app’s canonical writes omit level 0: creating with level 0 omits the field, and setting an element to 0 removes it. A supplied explicit `level: 0` remains valid at ingress for compatibility, but command planning always emits the single canonical form.

The first choice is deliberately conservative about document compatibility. A schema v2 bump would identify unsupported files more clearly, but the current strict v1 contract already rejects unknown fields. The bump would also make every subsequently saved document, including documents whose elements all remain at level 0, unreadable to older installations. Optional `level` plus default-0 semantics localizes that compatibility break to drawings that actually use non-default depth and avoids a broad document rewrite.

## Canonical level contract

- `level` is an integer from -127 through +127, higher values are in front. It is distinct from layer membership. The field is optional in the v1 JSON representation; absence means 0. New objects default to 0. Canonical command writes omit level 0: `set level=0` removes the field, and setting an already-absent level to zero produces no element change. Ingress accepts an explicit zero for compatibility but the planner never writes it. Public read summaries return the effective level, including 0 for legacy elements.
- Effective front-to-back order for drawable leaf elements is `(level ?? 0, zIndex)`, lexicographically. Leaf `zIndex` values remain unique per page and determine order within a level. Groups are aggregate elements, not separate stacking slots: their level and zIndex mirror their top leaf. Send backward/forward/to back/to front and the existing `reorder` operation act within one level; page-wide renumbering follows effective order when needed to retain unique leaf zIndex values. Moving a leaf to a level places it at that level’s top using the next page-global zIndex.
- Groups take the greatest `(level, zIndex)` pair among their leaves. Moving a group applies the same level delta to every leaf, preserving internal level offsets and zIndex ordering. If the shift would exceed the level range, fail atomically. Ungrouping preserves each member’s level.
- A connector created without an explicit level inherits the stored level of its start object. If the start is another connector (including an R16 joined trunk), inherit that connector’s level; a free start has level 0. Explicit operation or DrawScript level takes precedence. Later source moves do not alter the connector’s level or glue.
- Branch connectors inherit the connector they join. The current editor has no separate junction-marker object or projection primitive; if one is added later, it must draw at its trunk connector’s level.
- A `set` patch can assign level. The editor exposes level assignment in Arrange. DrawScript `add`/`connect` and `set` accept `level=`. MCP create operations accept an optional level; `get_objects` and document summaries expose effective level.
- Copy/paste and duplicate preserve element levels and group offsets. Component-library recipes and hand-built icon-preview elements default to level 0 unless a level is explicitly part of the component. Clipboard payloads retain nonzero levels.
- Hopping connectors compare level first and zIndex second; the higher pair owns the arc. Pass the two values to the hop renderer and compare them lexicographically rather than packing them into one number, avoiding collisions or precision loss for large zIndex values. Recompute hops whenever either route or either ordering value changes, including after undo/redo.

## Compatibility and validation

There is no document-wide migration in this design; absence-as-zero is the compatibility migration rule. A v1 document without `level` remains valid and renders at level 0. At each ingress, missing means 0; any supplied value must be an integer in range. V1’s `additionalProperties: false` contract continues to reject unknown fields. New saves need not add `level: 0` to every element; they write the property when it is nonzero. Clearing an element back to 0 may omit the property.

The document schema and generated TS/.NET contracts add an optional bounded field to each of the five element types. Operations add the field to shape/text/image/connector creation and `ElementPatch`. All validation uses the same -127..+127 bounds. This keeps the current schema version while making nonzero level explicit in saved data and APIs.

The new field must be supported or defaulted consistently at every boundary:

- Native file open, MCP/editor document replacement, and recovery restore treat an absent level as 0 and validate a supplied level before strict deserialization.
- New document creation, `planPatch` create/duplicate/group/ungroup/reorder, paste and clipboard compilation preserve level semantics; UI-only defaults and hand-built elements in the component library and `icons/preview.ts` use level 0.
- DrawScript add/set and MCP create/set accept level; `readTools.ts` returns it. Contract examples and fixtures cover both omission (0) and nonzero values.
- The maxGraph editor, hit-testing, hidden projection, canvas snapshot, SVG/PNG/JPEG rendering and native Visio export use the same effective order `(level, zIndex)`.

## Routing and Visio boundary

R29 makes levels available to routing but does not change route planning. R23 #62 owns the policy that allows a connector to pass through a lower-level shape. Level 0 therefore retains current routing behavior, and R29 does not duplicate the obstacle decision.

VSDX export writes shapes in effective order and adds the `AgentLevel` user cell only for nonzero levels. `AgentSchemaVersion` continues to reflect document schema version 1. Import treats a missing `AgentLevel` cell as level 0; a valid in-range cell restores its level, while an invalid supplied cell falls back to 0 with a diagnostic. If file order conflicts with restored levels, level wins and the importer reports `level_order_conflict`. Non-default levels also emit `level_not_native` because Visio has no native level primitive; the user cell preserves semantics on a round trip.

Mixed-level groups can be interleaved with elements outside the group in effective order. The current exporter’s group interleaving check remains authoritative: report its existing limitation rather than silently flattening or changing the group’s levels.

## Focused tests and acceptance evidence

- Legacy v1 documents with every element kind and no level validate and render as level 0; opening/saving without non-default levels does not add fields or change the schema version. Omitted, -127, 0 and +127 levels are accepted at ingress; fractional and out-of-range levels fail. Planner writes omit level 0, and setting a level-absent object back to 0 leaves the object and document bytes unchanged.
- Moving an element to a populated level gives it page-global `max(zIndex)+1`; same-level reorder changes order only within that level and retains page-global zIndex uniqueness.
- Moving a mixed-level group preserves level offsets and descendants, recomputes the group from its top `(level, zIndex)` leaf, and atomically rejects a boundary overflow. Ungroup/copy/paste/duplicate preserve the levels.
- VSDX import/export covers the existing `AgentSchemaVersion=1`, missing/valid/invalid `AgentLevel`, same-level file order, and cross-level file-order conflict with `level_order_conflict`; `level_not_native` appears for non-default levels.
- Connectors inherit from shape, text, image and connector starts; explicit levels win; moving the start later leaves its connector unchanged; joined branches and derived junction markers use the trunk level.
- Hop tests cover greater level, equal-level zIndex tie-break, changed route, level/reorder change and undo/redo. Editor, projection, hit-testing, SVG/PNG and VSDX agree on mixed-level order.
- Level-0 behavior remains unchanged against pinned R05 `ReferenceFixtureRegressionTests`, Visio `LegoJunctionTests`, PSGL and existing route fixtures. Run the Windows regression suite and Visio suite; report `VisioAcceptanceTests` separately because it depends on a real Visio installation and has known glue-test flakiness. R23 pass-through policy tests remain in #62.

## Implementation coordination

The updated `main` at `36ad4e6` includes R21 stage 2 and stage 3 code; PR #75 is merged and the #76 changes are in the main history. The R29 branch is based on that main state. Contracts, commands, clipboard, rendering and hop behavior are one deliverable and must not be integrated separately: accepting a nonzero level before the editor honors it would persist a setting with no visible effect. VSDX round-trip follows as a separate deliverable.
