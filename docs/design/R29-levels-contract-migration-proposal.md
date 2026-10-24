# R29 levels contract and migration proposal

Status: proposed for owner review before implementation  
Issue: [#69 — R29 levels](https://github.com/rp2839/visio-mcp/issues/69)  
Branch: `codex/r29-levels`

## Decision requested

Adopt document schema version 2 for the canonical level field, with an in-memory version 1 to version 2 migration at every document ingress. The migration adds `level: 0` to every legacy element and does not change revision, geometry, `zIndex`, routes, or the source file until an ordinary save. New and migrated documents use version 2; the schema requires an explicit level on every element kind.

This makes the format change explicit. The current document schema rejects unknown fields and fixes `schemaVersion` at 1, so adding a required field without a version transition would make a saved document claim to be v1 while requiring a newer reader. Older app versions will reject v2 as unsupported; this is consistent with the current strict contract behavior for newer document fields, while giving them an unambiguous version signal.

## Canonical contract

- Add `level`, an integer from -127 through +127, to shape, text, image, connector, and group elements. Canonical v2 documents require it; new elements default to 0.
- Effective front-to-back order is the pair `(level, zIndex)`, compared lexicographically with higher values in front. `zIndex` remains the stable order within a level. Existing reorder operations continue to reorder only within a level.
- A group stores the level of its top member, as specified by the issue owner. Moving a group to a level moves its leaf members as one selection; group levels are then recalculated from those members. Ungrouping preserves each member's level.
- A connector created without an explicit level inherits the start object's stored level; a free start or a start object with no level after normalization means level 0. An explicit caller-supplied level wins. Later source moves do not change the connector's stored level or its glue.
- Junction branches and junction markers inherit the connector level described in the issue. `set` patches may set `level`; the editor exposes a *Move to level* command, while existing front/back commands and `reorder` remain level-local.
- Hopping connectors compare `level` first, then `zIndex`; the higher pair owns the arc. Recalculate hops whenever either route or either ordering value changes, including after undo/redo.

## Migration and validation

1. At document ingress, parse the raw document envelope and inspect its version before strict typed deserialization or canonical validation.
2. For version 1 only, clone the document, add level 0 to each element that lacks a level, set `schemaVersion` to 2, and run the full v2 schema and semantic checks. Preserve all other fields and values. A valid explicit level already present is preserved; malformed or out-of-range values fail v2 validation. Unknown fields remain errors.
3. For version 2, do not default or repair missing/invalid levels; fail validation with an element path. Reject unsupported versions with a clear `unsupported_schema_version` diagnostic.
4. Apply the same normalization before validation for native file open, host/editor document replacement (including MCP), and recovery restore. VSDX import creates canonical v2 documents directly. Do not increment document revision or mark a v1 file dirty merely because it was normalized in memory.
5. Serialize canonical v2 documents. A later normal save of a migrated document writes v2; merely opening or closing without edits does not rewrite the original file.

The TypeScript and .NET boundaries must share the same migration rules and contract fixtures. TypeScript-generated contracts/validators and C# contracts remain generated from the canonical schema; the raw-envelope migration runs before their strict v2 deserializers. Migration tests should assert that non-level content is byte/value-equivalent after normalization, besides the version and added fields.

## Rendering, routing, and Visio boundary

The editor canvas, hit-testing, hidden projection, image/SVG render and native Visio export all sort visible elements by `(level, zIndex)`. Level 0 preserves today's exact ordering. Level ordering is not layer membership and does not affect layer visibility, locking, printing, snapping, or glue.

R29 makes the level available to routing. R23 #62 owns the policy that treats a lower-level shape as pass-through; R29 must not change route planning based on level. This keeps behavior stable for level-0 documents and avoids duplicating R23's obstacle decision.

VSDX export writes shapes in effective order, adds the portable `AgentLevel` user cell, and emits an informational `level_not_native` diagnostic when non-default levels are present. The importer restores a valid cell in range and assigns level 0 when the cell is absent. Invalid cells produce a diagnostic and fall back to 0. Native Visio has no stacking-level primitive, so the file's visible order is preserved while round-trip semantics rely on that user cell.

## Tests and acceptance evidence

- Version 1 fixture with every element kind migrates to v2, defaults all absent levels to 0, and keeps `zIndex`, geometry, route and revision unchanged.
- Version 2 requires level; boundary values -127 and +127 pass, and fractional/out-of-range/missing values fail. Unsupported schema versions fail explicitly.
- Native file open, MCP replacement, recovery restore, and .NET host ingress all use the same migration behavior; normal save writes v2 and no-edit open/close preserves the original bytes.
- Level 0 rendering and routing remain unchanged against the PSGL, Lego and existing image/regression fixtures. Mixed-level ordering agrees in the editor, projection, hit-testing, image export and Visio export.
- Connector creation inheritance, explicit levels, source movement, group move/ungroup, level-local reorder, junction levels, hop tie-breaks, route changes, undo/redo, and VSDX user-cell round trip receive focused tests.

## Implementation coordination

R21 PRs #75 and #76 currently touch contracts, command planning and canvas ordering. After this proposal is reviewed, coordinate with their owner in #69 and base the code phase on the integrated R21 state (or an explicitly agreed shared-file split). Keep the migration/schema step independently reviewable, then implement commands, rendering and Visio round-trip in small commits. No product code is included in this proposal change.
