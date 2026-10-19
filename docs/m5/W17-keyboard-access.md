# W17: keyboard-only and screen-reader operation of the canvas (#39)

Branch `claude/w17-keyboard-access`.

## What changed

- **Moving between objects.** The canvas has an invisible twin, a list of the page's objects (`ObjectList`), that takes keyboard focus; a blue ring is drawn round the canvas while it has focus, and a click on the canvas hands focus to it. With focus there:
  - Tab / Shift+Tab select the next / previous object, in reading order (top to bottom, then left to right; a connector sorts by the box between its ends). At either end Tab moves on to the next control, so the keyboard is never trapped.
  - Ctrl+Arrow selects the nearest object in that direction (within a 90-degree cone; none says so in the status bar). Home / End select the first / last.
  - Arrow keys nudge the selection by **one grid step** (5 mm by default); Shift+Arrow nudges 1 pt. This changes the earlier 1 pt / 10 pt.
  - Enter or F2 edits the text of the one selected shape or text box. Esc clears the selection.
  - Groups are one stop; their members are not visited separately. Hidden objects are skipped.
- **Names, roles and positions.** Each object is a list item whose accessible name reads like `api, shape, at x 28.2 y 14.1 mm, 28.2 by 14.1 mm` (connectors: `from api to db, connector`), with position in set (`2 of 7`), selected state, and `aria-activedescendant` on the list for the object the keyboard is on. Measured in the real window through UI Automation (see below).
- **Connectors without a pointer.** Edit > Connect objects... opens a dialog with From and To lists (prefilled from the selection); Connect makes the same connector as dragging in connector mode and selects it. Choosing the same object twice is refused with a message; Esc cancels.

## Verification

| Check | Result |
|---|---|
| `npx vitest run` | 406 passed (9 new in `tests/objectNav.test.ts`: order, groups, steps, directional jump, names) |
| `npx playwright test` | 117 passed; `tests/browser/keyboard.spec.ts` (6) covers Tab, Ctrl+Arrow, nudge step, Enter edit, accessibility roles and names, connect dialog |
| `UiAutomationTests` (real window, `System.Windows.Automation`) | 1 passed: both shapes appear as ListItem elements with name, role, position and size |
| Windows: UiAutomation, TabTests, save and open | passed |
| Screen reader (Narrator / NVDA) walkthrough, high-contrast and zoom checks | NOT RUN: needs a person at the machine |
| Creating shapes and moving them to other pages from the keyboard | not covered; not in the acceptance criteria |

## Limits and overlaps

- The list holds every object on the page; a very large page makes a long (hidden) list. Not measured beyond the Lego-sized pages.
- Keys only work while the object list has focus (after a click or a Tab into the canvas). The shape palette still needs a pointer to place new shapes.
- Overlaps: #34 (W12, PR #52) also edits `Toolbar.tsx` and `App.tsx`; the Connect menu item here is a one-line entry that moves into W12's command list when both merge. #32 (W10) and #33 (W11) by Codex touch canvas keys.
- Dependency A03 (#16) is an acceptance expansion, not a blocker.
