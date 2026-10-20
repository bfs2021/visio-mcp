# W13 Task 6 report

2026-10-20, branch `codex/w13-preferences`, continuing retained Task 6 changes above base `67739d9`. No push or main merge performed. Controller owns issue coordination, final full Windows/browser suites, independent review, and integration.

## Implementation

- PreferencesModel owns disposable validated draft, busy/errors/conflict state, explicit reload/reset, transport failure retention and successful-save notification. Explicit Reset is the only source of resetInvalid.
- Edit > Preferences exposes display/new-drawing/recovery fields, installed font suggestions and unavailable-font note. Grid conversions keep stored points unchanged while switching display units. Keyboard focus is contained/restored, background controls are inert, lifecycle/tab/gesture actions are guarded.
- Successful Save applies units to open views and chrome theme without canonical edits. Cancel/Escape and successful Save release a pending native close request; failed saves retain the dialog. Self-review found and fixed the successful-Save native close latch omission.
- CSS chrome and canvas ruler tokens support dark/light themes; authored page/shape/text colours and SnapshotRenderer remain unchanged. WPF/WebView background and DWM titlebar theme apply on durable save/startup.
- Long-lived agent views refresh host settings before creating their next drawing; existing documents retain their captured content/defaults.

## Fresh verification

Commands ran in the feature worktree using PowerShell PATH prefix `C:\Program Files\nodejs;C:\Program Files\dotnet;`. npm commands ran from `src/web`, dotnet from repository root.

| Command | Result |
| --- | --- |
| `npm test -- tests/preferencesFlow.test.ts` | PASS, 11 tests |
| `npm test -- --maxWorkers=2` | PASS, 46 files / 450 tests; task-6-unit.log |
| `npm run typecheck` | PASS; repeated after final product change |
| `npm run generate:check` | PASS |
| `npm run build` | PASS; repeated after final product change; existing maxgraph eval and large-chunk warnings |
| `npm run test:browser -- tests/browser/preferences.spec.ts --workers=1` | PASS, final 7/7; task-6-browser.log |
| `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~PreferencesHostTests --no-restore --logger "console;verbosity=normal"` | PASS, 5/5; task-6-native.log |
| `git diff --check` | PASS |

Browser checks cover Save/Cancel/Escape/Reset, invalid fields, installed/unavailable fonts, stale-version explicit Reload, failed-write draft retention, focus wrap/restoration, keyboard/tab/lifecycle blocking, all-tab units and session-only toolbar override. Authored green shape and text produce exactly identical clean doc.render PNG base64 before/after theme/unit Save; cropped page RGBA values, SHA-256 of document JSON, and revision also remain identical. Separate Save test compares whole snapshot and status (including undo/dirty state). Selected dark page-tab contrast is at least 4.5:1.

Native checks cover isolated preferences storage/fonts/stale tag, startup capture, an already-initialized agent's next-document refresh while old document bytes stay unchanged, and Preferences focus/WPF theme across restart. Native run preceded the final frontend close-notification fix; final browser coverage directly verifies that notification. Full native/browser acceptance remains controller-owned.

## Failures and TDD history

- Previous worker reported missing `open` model/dialog and absent Edit menu failures before implementation; these are historical reports, not freshly observed logs. It also reported 11 model tests, initial 3 browser tests, and initial 3 native tests passing. This continuation does not use those reports as fresh evidence.
- Fresh initial browser baseline: 6/6 passed before export and keyboard coverage additions.
- First extended clean-export run: 5 passed / 1 failed because `page-sheet` matched the editor plus hidden export renderer sheets. Scoped selector to the visible canvas, then extended suite passed 7/7.
- One editing command used an incorrect nested src/web path; it changed no file. Corrected the working-directory-relative path.
- Native-close regression was verified RED by temporarily reverting only the notification fix: `npm run test:browser -- tests/browser/preferences.spec.ts --workers=1 -g 'Save applies theme'`, 1 failed, expected cancellation count 1 but received 0 (task-6-close-red.log). Restored fix; final 7/7 GREEN.
- Full unit suite generated PERF timings; restored that unrelated generated file rather than including it.

## Evidence and remaining limits

Screenshots: `task-6-screenshots/light.png`, `dark.png`, `dark-dialog.png`. Regenerated with authored shape/text. Dark editor screenshot freshly visually inspected: readable chrome/rulers/page tab and unchanged white page/green authored shape. Controller previously visually inspected the prior light/dark/dialog screenshots; those observations are historical. Fresh human manual acceptance: NOT RUN (automated browser/native interaction is not human sign-off).

No fresh full-browser/full-Windows-solution run here by controller instruction. Native titlebar appearance and OS-owned dialogs are not pixel-verified; WPF/background/restart are automated assertions. Native Visio colour/export preservation is delegated to existing tests/controller acceptance, not inferred from browser PNG checks. No installer or public MCP tool changes. Review the overall W13 captured-default/storage/recovery contracts in Task 7 before integration.
