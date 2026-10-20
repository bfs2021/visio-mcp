# W08 evidence

Final verification on product commit 6eca97a: 424 .NET tests, 393 frontend tests and 108 browser tests passed; typecheck, generated-contract check and web build passed. Intermediate failing runs are deliberately retained and superseded by final results; see the [report](../../docs/m5/W08-editor-export.md).

- Native TRX files exercise the real WPF/WebView2 host, renderer and file/clipboard APIs. `w08-host-red` is the missing-route negative control; initial `w08-host-green` recorded an in-memory JsonValue numeric conversion setup problem, fixed by normal deserialization. `w08-pdf-red` records missing PDF implementation; `w08-pdf-green` records the empty HTTP reason phrase load failure; `w08-pdf-diagnostic` records incorrect global paper sizing. `w08-pdf-css-green` verifies mixed page sizes with the Chromium CSS sizing flag.
- `w08-edge-red` catches rejection of a small selection on a large page; `w08-edge-green` verifies the corrected bounds and clipboard formats. `w08-contention` includes one invalid test injection (missing pageId) and is superseded by `w08-review-fixed`, where all eight targeted native tests pass, including clipboard contention and a successfully committed mid-render edit.
- `export-dialog.png` is captured from real WebView2 after opening File > Export image. `from-editor.png` is produced through that UI and host bridge. `mixed-pages.pdf`, `pdf-page-1.png`, `pdf-page-2.png` and `pages.zip` show the two-page mixed-size export fixture. Final regenerated PDF pages were visually inspected and passed; see pdf-verification.json.
- `frontend-tests.txt`, `browser-tests.txt`, `build.txt`, `dotnet-tests.txt` and final TRX directory record complete checks. Earlier `full-results` is the first full run, including the generic destination-format policy and legacy comparison-render background regressions; final-results contains the superseding successful run.

Manual human acceptance: NOT RUN.
