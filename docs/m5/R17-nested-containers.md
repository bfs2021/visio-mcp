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

## Remaining stages

2. Layout inspection (no false OVERLAP or CROSSES; `MEMBER_OUTSIDE_CONTAINER`, `UNLISTED_INSIDE_CONTAINER`, `CONTAINER_HIDES_MEMBER`) and routing reading the helper module.
3. Editor commands, inspector and drag-in/drag-out.
4. Native VSDX export and import, after the Visio feasibility check; the four-level Lego build.
