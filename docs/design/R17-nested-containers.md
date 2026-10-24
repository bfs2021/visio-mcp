# R17 nested containers: design proposal

Status: **approved for implementation** (owner accepted all four recommendations, 2026-10-24)<br>
Issue: [#12 — R17 nested containers](https://github.com/rp2839/visio-mcp/issues/12)<br>
Branch: `claude/r17-nested-containers`<br>
Evidence that motivates it: [R15 Lego comparison](../m5/R15-lego-infrastructure-comparison.md) (four nesting levels drawn as 14 loose rectangles; 29 OVERLAP and 62 CONNECTOR_CROSSES_SHAPE, mostly false), [R09 swim-lane](../m5/R09-swimlane.md) "Not done".

## Summary

A container is an ordinary shape that also has an explicit, ordered list of member elements. Moving the container moves its members, including the members of any containers nested inside it. Resizing it never scales them. A container can optionally grow to keep its members inside. Membership is stored, not guessed from geometry. Engine and MCP edits never change membership as a side effect; only explicit operations do. Layout inspection, the router and the VSDX exporter read the same membership. In Visio it becomes a native container (`User.msvStructureType = "Container"` plus the `Relationships` cell).

## Why not groups

Groups already nest and move as a block, but they are the wrong model here:

| | Group | Container |
|---|---|---|
| Visual | none of its own (derived bounds) | a real shape: outline, fill, heading text |
| Resize | scales every child | changes only the frame; members keep their size and position |
| Selection and editing | a block; children are reached through the group | members stay top-level objects and are selected, edited and connected directly |
| Visio | `Type="Group"`; children scale with the group | native container; members are independent shapes |

The swim-lane component (R09) stays a group in R17. Converting lanes to containers, which Visio's cross-functional lists need, is a later follow-up that can build on this design.

## Canonical contract

`ShapeElement` gains one optional property. Shapes without it behave exactly as they do now. `schemaVersion` stays at 1, following the same optional-field reasoning as R29.

```jsonc
"container": {
  "memberIds": ["<uuid>", ...],          // direct members only, in order; may be empty
  "fit": "none" | "grow" | "fit",        // optional; absent = "none" (decision 1)
  "marginPt": 0..500                      // optional; inner clearance used by grow/fit
}
```

Invariants, validated at every ingress (open, MCP replace, recovery, paste) and by the planner:

1. Members are elements on the same page. Allowed kinds: shape, text, image, group and connector. A member may itself be a container, which gives nesting.
2. **Single direct parent:** an element appears in at most one container's `memberIds`. Containers form an acyclic forest; a container never contains itself, even indirectly.
3. **Group consistency:** a container and its direct members have the same group parent (both top level, or both children of the same group). A group can be a member as a whole; one child of a group cannot be a member on its own. This keeps the group transforms and the container transforms from fighting.
4. Membership is explicit. A member may lie partly or wholly outside its container's bounds. Inspection reports that (see below), but it does not break the document.

Read tools add a derived `containerId` (the direct parent) to each member in `get_objects` and `get_document_summary`. The summary also lists each page's container tree.

## Behaviour

**Move.** Moving a container translates the container and every descendant member by the same delta. For a connector, that means its free ends, waypoints and stored advisory route. Glued ends follow their shapes as usual. A connector whose two glued ends are both inside the moved container (transitively) has its whole stored route translated. It is not replanned, so R19's "keep the valid route" holds and the route keeps its provenance. A connector with one end inside goes through the normal R19/R20 settle: elastic, then repair, with a Frozen route kept and flagged if it cannot follow. Moving a member alone moves only that member, and it stays a member.

**Resize.** Resizing a container changes only its bounds; members are never scaled or moved. This is the main difference from a group, and it matches Visio.

**Fit (settle step).** After a transaction changes a container or any of its descendants, containers with `fit` set are settled from the innermost outwards:
- `grow`: the new bounds are the union of the requested bounds and every member's visual bounds plus `marginPt`. The container expands and never shrinks.
- `fit`: the new bounds are exactly the members' visual bounds plus `marginPt`.
- `none`: the bounds are left as they are.

Settling a nested container can make its parent grow in turn. This runs as part of the same transaction, alongside the join, route and attachment settles. Its changes are in the transaction's `changed` list and undo with it. An explicit resize of a `grow` container to smaller than its contents is therefore pushed back out to fit, as in Visio. To shrink one, set `fit: "none"` first, which can be done in the same transaction.

**Delete.** This follows the existing group rule, so deleting needs an explicit choice. `delete` of a container requires `members: "delete" | "keep"`:
- `delete` removes the whole subtree.
- `keep` removes only the frame. Its members move up into its parent container, or become top level if it had none.

Deleting a member removes it from `memberIds`. An empty container is valid, unlike an empty group.

**Locks.** Moving a container whose subtree includes a locked element is refused, the same as `assertSubtreeEditable` for groups. A locked container's members can still be edited on their own.

**Duplicate, copy and paste.** Duplicating or copying a container includes its subtree, with new ids and the same structure. A duplicated member alone joins its original container. A member pasted on its own arrives at top level, unless the editor pastes it into a container (see the editor section).

**Group and ungroup.** A `group` operation that would separate a container from any of its direct members is refused with `container_split`. Ungrouping keeps all memberships.

**Stacking (R29).** Membership does not restack anything. When a container with a non-transparent fill draws above one of its members (by `(level, zIndex)`), inspection reports `CONTAINER_HIDES_MEMBER`. The editor's "Make container" command places the new frame behind its members.

**Attachments (R13).** An element attached to a connector follows the connector even when it is also a container member. The attachment settle runs after the container translation.

## Operations, MCP and DrawScript

- `createShape.container`: creates a container, optionally with `memberIds`.
- New `contain { container: Target, add?: Target[], remove?: Target[] }`. Adding an element that already belongs to another container moves it, and both containers appear in `changed`. Removing an element that is not a member is refused.
- `set` patch `container: { fit?, marginPt? }` changes the settings. `container: null` disbands the container: the shape stays and its members move up, as with `delete members:"keep"` but keeping the frame. Membership lists change only through `contain`, so there is one way to edit them.
- `move` and `resize` get no new parameters. Their container behaviour follows from the rules above and is described in the MCP tool text.
- DrawScript: `add ... container fit=grow margin=12`, `contain <container> add a b c`, `contain <container> remove d`.

Error codes: `container_cycle`, `container_split`, `container_member_other_page`, `container_delete_requires_choice`, `not_a_member`.

## Layout inspection

Intended containment no longer produces warnings. Real defects are still reported.

- No `OVERLAP` between a container and any of its descendants. Members of the same container that overlap each other, and sibling containers that partly overlap, are still reported.
- No `CONNECTOR_CROSSES_SHAPE` for a connector crossing the boundary of a container that holds one of its ends (transitively). Crossings of containers that hold neither end are reported as they are today, according to the router's policy for that container.
- New `MEMBER_OUTSIDE_CONTAINER` (warning): a member's visual bounds extend past its container's bounds by more than 1 pt.
- New `UNLISTED_INSIDE_CONTAINER` (info): an element lies wholly inside a container's bounds but is not a member of it or its descendants. This helps an agent convert a flat drawing such as Lego into real containers.
- New `CONTAINER_HIDES_MEMBER` (warning), as above.
- The current geometric heuristic (0.98 containment counts as not a collision) stays for shapes that are not declared containers, so existing documents keep the same reports.

## Routing boundary

This section answers the 2026-10-20 comment on #12. R17 provides one read-only helper module, `layout/containers.ts`, with `containerOf`, `isContainer`, `descendantsOf` and `holdsEnd(connector, container)`. The router and R22 use these; R17 changes no routing policy.

- **Default obstacle behaviour.** Without an explicit `routingPolicy`, a declared container is treated the way the current `selectObstacles` treats a guessed container: connectors pass through it. Level-0 and Lego-style documents therefore route as before. R22 (#61) owns making that a visible policy (forbidden, avoid, pass-through, gateway). R23 (#62) still applies first: a container on a lower level is passed through.
- **Connectors when a container moves or resizes.** See Move above. With both ends inside, the route moves rigidly with the container. With one end inside, the R19/R20 rules apply. A resize moves no member and so no glued end; any resulting crossings go through the normal route settle.

## Editor

- Arrange menu:
  - **Make container from selection** creates a frame around the selection's bounds plus a margin, behind the selection, with `fit: "grow"`.
  - **Add to container** and **Remove from container**.
  - **Disband container**.
- Inspector: a Container section with fit, margin and the member count.
- Dragging a selection moves the container's subtree once. Members whose container is also selected are dropped from the drag set, as for groups.
- **Proposed (decision 3):** dropping a shape wholly inside a container adds it, and dragging it fully out removes it, as Visio does. The editor emits explicit `contain` operations in the same transaction and highlights the target container while dragging. Engine and MCP edits never do this implicitly.

## VSDX export and import

- **Container shape:** user cells `msvStructureType = "Container"`, `msvSDContainerResize` (`none`/`grow`/`fit` → 0/1/2) and `msvSDContainerMargin`. Its `Relationships` cell is `DEPENDSON(1, <member sheets>)`.
- **Member:** its `Relationships` cell is `DEPENDSON(4, <container sheets>)`.
- **Nesting, from a real Visio file.** `C:\Devsdxpp-service-reference-architectures.vsdx` was saved by Visio with three nested containers, and it shows the relationships are transitive. The outer container's type-1 list names every shape at every depth. Each member's type-4 list names every enclosing container, for example `DEPENDSON(4, Sheet.1000, Sheet.1010, Sheet.1015)`. The exporter therefore writes the full transitive lists, and the importer takes a container's direct members to be its type-1 list minus the members of the containers nested inside it. In that file `msvSDContainerMargin` is in inches (0.125 and 0.25 appear) and `msvSDContainerResize` takes 0 and 1.
- **Still to verify in Visio (feasibility gate):** every container in that file comes from Visio's container masters, which carry style, heading and category cells. It is not yet known whether Visio treats a plain exported shape that has only these cells as a container. I'll check by opening an exported file through the existing Visio test harness and asking Visio for each container's members. The resize value for `fit` (2) also needs checking.
- If Visio rejects the native form, the fallback exports plain shapes with `AgentContainer` user cells for round trip, plus a `container_not_native` export warning. Nothing would be silently flattened.
- **Import (R15 importer):** a shape with `msvStructureType = "Container"` becomes a container. Its type-1 relationships, minus those owned by nested containers, become `memberIds`. Its resize and margin cells map back to `fit` and `marginPt`.

## Acceptance evidence plan

- **Unit:** each invariant and error code; nested move translating every level, including free and advisory connector geometry; resize leaving members untouched; `grow` and `fit` settling innermost first; delete `keep` and `delete`; contain re-parenting; duplicate, copy and paste; group refusal; undo and redo of each as one step; an unchanged legacy document producing identical reports and bytes.
- **Inspection:** a Lego-shaped fixture showing the false OVERLAP and CROSSES warnings gone and real ones kept (overlapping members, a sibling straddle, a member outside its container).
- **MCP (real server):** build the four-level Lego containers (public cloud > tenant > component frames) from `artifacts/lego-infra/ops.json` plus `contain` operations; move the outer frame; check every level moves and that routes inside are kept.
- **Visio:** export, open in Visio, confirm the native containers move their members and the nesting reads back. Then a Visio round trip.
- **Manual:** marked NOT RUN until the owner checks the editor and Visio.

## Delivery stages

1. Contract, engine (move, resize, fit, delete, contain, duplicate, group rules), undo, MCP and DrawScript.
2. Inspection and the routing helper.
3. Editor commands, inspector and drag behaviour.
4. VSDX export and import with Visio verification; the feasibility check in that section comes first, so the export design can change before code is written.

## Decisions (owner, 2026-10-24)

1. **Default `fit`:** absent, meaning `"none"`, for engine and MCP creation; `"grow"` for the editor's "Make container".
2. **Editor Delete key on a container:** deletes the container with its contents. The engine always requires the explicit `members` choice.
3. **Drag-in and drag-out membership in the editor:** yes, in stage 3, emitted as explicit `contain` operations.
4. **Lego source file:** `C:\Devsdx\Lego Infrastructure - v2.vsdx`. It contains **no** Visio containers: its frames are plain rectangles and its relationship cells are empty. Import of real containers is therefore verified against the Microsoft sample above. The Lego file stays the target for the four-level build and for a Visio export of it with real containers. Neither customer file is copied into the repository.
