# R17 nested containers: implementation and evidence

Issue #12. Design: [docs/design/R17-nested-containers.md](../design/R17-nested-containers.md) (approved 2026-10-24). Branch `claude/r17-nested-containers`.

## Stage 1: contract, engine, MCP and DrawScript

**What it delivers:**
- **Contract:** an optional `ShapeElement.container { memberIds, fit?, marginPt? }`. `schemaVersion` stays at 1. Operations gain `createShape.container`, the `contain` operation, the `set` patch `container` (with `null` to disband) and `delete.members`. The TypeScript, C# and validators are regenerated.
- **Engine (`commands/planPatch.ts`, helpers in `layout/containers.ts`):**
  - Moving or arranging a container carries every member at every depth. A connector glued at both ends inside moves whole with its stored route, without being planned again.
  - Resizing a container never touches its members.
  - Containers with `grow` or `fit` settle innermost first, in the same transaction.
  - Deleting a container with members needs `members: delete | keep`.
  - `contain` re-parents an element when adding it, and moves it up a level when removing it.
  - Duplicate and duplicate-page keep the structure. A duplicated member joins its container.
  - Rotating a container with members is refused, as is moving one when anything inside it is locked.
  - The final validation refuses a member on another page, a member of two containers, cycles, and a container in a different group from its members.
- **Ingress:** documents that are opened, replaced or recovered are checked against the same rules.
- **Reads:** `get_document_summary` and `get_objects` add a derived `containerId` to members. The summary shows a container's members and settings.
- **Clipboard:** copying a container copies what is inside it, and paste restores the membership.
- **DrawScript:** `container=` and `margin=` on `add shape` and `set`, the `contain … add|remove|disband` command, and `delete … members=`.
- **MCP:** operation guidance and the operation example.
- **VSDX:** an interim `container_not_native` export warning until stage 4.

**Verification (fresh, on this branch):**
| Check | Result |
|---|---|
| `npx vitest run` (web) | 668/668 pass, including `tests/containers.test.ts` (20) |
| Mutation check: break member carrying, then the fit settle | 6 and 2 container tests fail respectively, then pass again when restored |
| `npx tsc --noEmit`, `npm run generate:check`, `vite build` | pass |
| `npx playwright test` (browser) | 188/188 pass |
| `dotnet test Diagram.Portable.slnx` | 490/490 pass (contracts 50, host 224, IPC 33, MCP 14, Visio 169) |
| Manual editor or Visio check | NOT RUN: there is no editor UI until stage 3, and no native export until stage 4 |

## Stage 2: layout inspection and routing

**What it delivers (`layout/inspectLayout.ts`, `layout/routingPolicy.ts`):**
- **`OVERLAP`:** no longer reported between a container and anything inside it at any depth. Overlapping members and partly overlapping sibling containers are still reported.
- **`CONNECTOR_CROSSES_SHAPE`:** no longer reported for a container that holds the connector, one of its glued ends, or one of its free end points. Lines through unrelated containers are still reported.
- **New findings:**
  - `MEMBER_OUTSIDE_CONTAINER` (warning, more than 2 pt of overhang);
  - `UNLISTED_INSIDE_CONTAINER` (info, against the smallest container; free lines count, glued connectors do not);
  - `CONTAINER_HIDES_MEMBER` (warning, when a filled container is drawn in front of its own member).
- **Routing:** the legacy obstacle guess skips a declared container that has members, so a container whose only member is a text box is passed through as a guessed one is. An explicit `routingPolicy` still decides first. R22 (#61) owns the visible policy.

**Lego acceptance (`tests/legoContainers.test.ts`, on the R15 reproduction in `artifacts/lego-infra/ops.json`):**
- **Method:** the 24 shapes that wholly hold another shape are declared containers. The inspector's own `UNLISTED_INSIDE_CONTAINER` findings fill them; labels that straddle a frame edge go to the frame they mostly sit in.
- **Structure:** 132 members, nested four deep (public cloud > tenant > component > contents). Moving the outermost frame carries 102 elements, including 40 free lines, by exactly the same amount.
- **`CONNECTOR_CROSSES_SHAPE`:** 62 → 28. No remaining crossing involves a container that a line starts or ends inside. The 28 left are free polylines through ordinary shapes, which is the R12 free-line limitation, not containment.
- **`OVERLAP`:** 29 → 25, plus 3 `MEMBER_OUTSIDE_CONTAINER`. Most of the 25 are real geometry that containment cannot express. For example, the "AWS to Corona Site VPN" box spans two frames, and an element belongs to one container. Labels also overlap icons. These are reported on purpose.

**Verification (fresh):**
| Check | Result |
|---|---|
| `npx vitest run` (web) | 676/676 pass (`containerInspection.test.ts` 7, `legoContainers.test.ts` 1, `containers.test.ts` 20) |
| Mutation check: disable the overlap skip, the crossing skip, then the router pass-through | 1 inspection test fails for each, then passes again when restored |
| `npx tsc --noEmit` | pass |
| `npx playwright test` | 188/188 pass |
| .NET | not re-run: stage 2 changes no C# |

## Remaining stages

3. Editor commands, inspector and drag-in/drag-out.
4. Native VSDX export and import, after the Visio feasibility check; the four-level Lego build.
