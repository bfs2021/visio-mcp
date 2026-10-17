# WA results, 2026-10-17

Environment: Windows 11 Enterprise 10.0.26300; Visio 16.0 build 20430 and Word (Microsoft 365), both driven through COM automation; .NET SDK 10.0.402. Run: `dotnet test tests/Diagram.Windows.Tests --filter OfficeAcceptance`, 1 test, passed in about 24 s. Raw results: `docs/acceptance/evidence/WA-xx/result.json`.

| ID | Result | What was observed |
|---|---|---|
| WA-01 | PASS | Versions recorded. |
| WA-02 | PASS | `export_document` wrote a 7.3 KB .vsdx with no loss reported. Visio opened it without a repair prompt (alerts answered No); all 12 objects have a Visio shape with their UUID. Screenshot `WA-02/visio-open.png`; file `WA-02/exported.vsdx`. |
| WA-03 | PASS | Text, fill colour and position changed in Visio. |
| WA-04 | PASS | After moving ERP up 0.75 in, the CRM to ERP connector still has two connections and ends at the height of ERP's centre; `WA-04/after-move.png` shows all connectors attached. |
| WA-05 | PASS | The picture is a Visio picture shape whose `AgentAssetSha256` equals the host hash. A package with the media swapped (user cells stale) reopened with `picture_replaced`; the picture became a new asset. |
| WA-06 | PASS | The Visio-saved file (`WA-06/visio-saved.vsdx`, sidecar names Visio's version) reopened in the app: all 12 original ids resolved via `get_objects`; text "CRM edited", fill #FFC800, ERP moved 54 pt up, connector ends unchanged. One id was repaired (the duplicate, WA-07). |
| WA-07 | PASS | Visio's Duplicate copies `User.AgentId`. On reopen there are 13 objects, all unique, one new id; the original kept its own. |
| WA-08 | PASS | Group width x1.5 in Visio scaled both members x1.500; the app showed the same ratios. |
| WA-09 | PASS | A shape in two layers keeps both through a Visio save; layer visibility is a real Visio setting. |
| WA-10 | PASS | Word embedded the drawing as `Visio.Drawing.15`; activation exposed the drawing (11 top-level shapes); text edited while embedded persisted after Word saved, the document reopened and the object reactivated. File `WA-10/report.docx`. |
| WA-11 | PASS | A shape dropped from the Basic Shapes stencil opened in the app as an ordinary shape, with an info note `master_inlined`. |
| WA-12 | PASS | Spec steps 10 to 14 each PASS. |

## A defect this found, and its fix
Shapes dropped from a Visio stencil store only PinX and PinY; size, outline and ports live in the master. The importer saw size zero and refused the file ("must have positive size"). It now fills missing cells, sections and rows from the master (child shapes through `MasterShape`) and restates proportional cells for the instance's own size. A real Visio-saved fixture covers it (`tests/fixtures/vsdx/visio-saved/stencil-masters.vsdx`, with sidecar). Line and fill styles still come from the document's style sheets only; theme-based colours from a master are approximated.

## Not observed
- Everything above was driven by automation. Nobody watched Word's in-place editing window: "activates for editing" means the OLE verb opened the embedded drawing and an edit made there was saved back. A person opening the Word file and double-clicking the drawing on another workstation is still a worthwhile spot check.
- Image replacement through Visio's own UI is not available in this Visio build (recorded in M0); replacement was tested at package level.
- Spec steps 1 to 9 are not part of this suite; the logo swap (step 8) belongs to I50.
- The M0 second-Windows-user pipe check remains open.
