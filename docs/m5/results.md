# M5 results so far (I50)

Recorded 2026-10-17, same Windows 11 machine.

## What I50 delivered
- **Library (host):** `AssetLibrary` is a per-user catalogue (`library.json`, written atomically) over the content-addressed blob store. An entry has a stable id `asset:<slug>`, name, tags, MIME type, size, version and the hashes of every version it has had. Bytes are stored durably before the catalogue names them; an entry whose bytes are gone is not offered. A different picture imported under a taken name gets `asset:<slug>~<full sha-256>` and the result says the name was taken. Removing an entry leaves the bytes (documents and recovery may pin them).
- **Resolver (host):** `AssetResolver` turns a library id, and optionally a version hash, into an approved preparation, the same shape as a fresh upload. Only library scope is resolved by the host; a document's own pictures are already in the document.
- **Document side (page):** `assetVersions.ts` pins a prepared picture without touching what is there: new id, same bytes (nothing to do), or same id with different bytes (a separate `asset:<slug>~<sha>` version; the old one and the images using it are unchanged). Single-image replacement changes exactly one image; global replacement names every affected image; an asset in use cannot be deleted. The DrawScript `asset import` command uses the same rule, and later commands in the script mean the version it registered.
- **Agent path:** `list_assets` returns document and library entries with scope and version; a document entry wins an exact id match and says `newerInLibrary` when the library moved on. `execute_script` takes `libraryAssets` (`asset:logo` or `asset:logo@<sha256>`); the host resolves them and a client can never supply preparations itself (any `preparedAssetRefs` from a client is discarded). Retrying the same transaction hashes the same even though the host issues a fresh preparation id each time.
- **Editor panels:** page tabs (add, rename by double click, reorder, duplicate, delete, size presets and custom size), layers panel (add, rename, visible, locked, print, membership of the selection, delete), pictures panel (library and document lists with thumbnails, add to library, tags, rename, place, drag onto the page, use for the selected picture, replace everywhere, replace library source, copy ID, delete).
- **Rules the tests check, not just flags:** a hidden object cannot be picked, even by Select all, and is let go when its layer is hidden; objects on a locked layer refuse nudge, inspector edits and drag; a new object starts on no layer; deleting a layer removes only the membership.

## Defects found on the way (fixed)
- Derived connector routes were computed for page 1 only, so connectors on later pages had no route in layout inspection, renders or export. The projector now projects every page.
- A script transaction retry would have conflicted once library pictures were involved, because the host's preparation id is new each time. The transaction hash now covers only the approved descriptors.

## Test results
| Suite | Result |
|---|---|
| `npm --prefix src/web test` | 257 pass (new: assetVersions 7, script versioning 1) |
| `npm --prefix src/web run test:browser` | 36 pass (new: pages-layers-assets 8) |
| Contracts / Host / Ipc / Mcp / Visio | 24 / 77 / 15 / 11 / 115 pass (Host: AssetLibraryTests 8) |
| Windows.Tests | 23 pass (new: library on the real stack, 2). A first full run showed two unrelated heavy tests failing (the stale-revision live test and the Office suite); both passed alone and in the next full run. Cause not found; treat as timing under load. |

## Known gaps
1. The tests drive the panels with a mocked host in the browser and the real host through the app and an MCP client; no one used the library import dialog by hand.
2. Library picture thumbnails are served by hash like document pictures; there is no size or storage management of the library, and no search box in the panel (the MCP tool filters by text and tag).
3. New objects start on no layer; there is no "active layer" for new objects yet.
4. Global replacement offers one confirmation-free button; the status line reports how many pictures changed, and Undo reverts it as one transaction.
5. The M0 second-Windows-user pipe check remains open.

---

# I51: durable recovery, export path policy and boundary hardening

Recorded 2026-10-18.

## Recovery
- **What is recorded.** The editor already emits every committed change as a resolved diff (generated ids, undo and redo included). The host journals those, in order, per session, in `recovery/<session>/journal.log`: one line per record with a checksum. The host never interprets or replays them.
- **When a record counts as saved.** `RecoveryJournal.AppendDurableAsync` answers only after the record is on disk (batched, never more than 250 ms). It refuses a record that names a picture whose bytes are not stored, one that leaves a gap in the revision chain, and anything after a write failure until a checkpoint rebases the journal. The status bar shows the newest revision that is on disk ("Protected to revision N") and says so when recovery is behind.
- **Checkpoints.** Every 50 commits, every 60 s while there are unsaved commits, and at save, the host asks the editor for the committed state at one revision, writes it as a new immutable generation, then replaces `manifest.json` atomically. The previous generation stays until the new manifest is durable. Commits that arrive while a checkpoint is being written stay in the journal (`CheckpointDoesNotLoseTailRPlus1`). A crash before the manifest leaves the old generation current; after it, the new one.
- **Reading it back.** A cut-off last record is dropped silently; a damaged record with good ones after it stops recovery at the last good revision and the loss is reported; a record that does not continue the checkpoint is reported, not applied.
- **Restoring.** On start the page asks whether unfinished, unsaved work exists. The person can recover, defer or discard. The page (not the host) rebuilds the document from the checkpoint plus the records with `restoreRecovery`, validating every step into a candidate, and only then replaces the document, which starts a fresh session with its own checkpoint. Recovered work stays marked unsaved, the original path is kept, and the old session is removed only after the new one is safe. After a renderer crash the same thing happens at once, with a visible report of what was and was not recovered. If the person edited while the question was open, the restore is refused rather than discarding that work.
- **A clean close** removes only a session with nothing unsaved. A crash after a save does not offer the saved work again.
- **Blob garbage collection** (`BlobGc`, `RecoveryManager.CollectGarbageAsync`) keeps everything that a document, the library, undo or redo history, a retained checkpoint or a journal record names, and anything newer than a grace period. Without the editor's answer it removes nothing.

## Export paths
`ExportPathPolicy` replaces the fail-closed check from I40. Exports go to the open document's folder or the export folder with a generated, unique name. A path elsewhere, or one that would replace a file, needs a yes from a window showing who asked, the full path and the action; no answer within 60 s is a no, and an answer given after the document changed is refused. Network shares, device and extended paths, alternate data streams, reserved names and links that lead out of the approved folder are refused even with consent. The destination is checked again right before the write.

## Boundaries
Static guards fail the build if code starts a program, opens its own network connection, writes anything but the shim's fixed startup line to a log, or lets the page open windows, run text as code, render links or markup from content, or fetch anything except approved pictures from its own origin. Pipe frames nested deeper than 64 levels, and every malformed frame, are refused as clean frame errors. The package, XML, SVG and raster limits from I11 and I40 were left as they are: no test found a gap.

## Test results
| Suite | Result |
|---|---|
| `npm --prefix src/web test` | 284 pass (recovery 7, boundary 4) |
| `npm --prefix src/web run test:browser` | 42 pass (recovery dialog 4) |
| Contracts / Host / Ipc / Mcp / Visio | 24 / 119 / 17 / 13 / 115 pass (Host: RecoveryTests 13, ExportPathTests 26, BoundaryGuardTests 3) |
| Windows.Tests | 30 pass (CrashRecoveryTests 3: renderer killed mid-session and restored; application restarted and offered its work; discard and clean close) |

## Found while building
- Restoring used the editor's revision at the moment of the click as the base, so edits made while the question was open would have been thrown away. The base is now the revision when the offer was made (browser test).
- Candidates were looked up excluding the session the host was still following, so right after a crash the dead session excluded itself. They now exclude the editor's announced session.
- The Windows suite had grown to the point where parallel WebView2 windows starved each other (requests timing out in unrelated tests, passing alone). Tests there now run one at a time.
- Codex's R02 evidence test re-exported to the same file name; under the new rule that needs a yes, so the test deletes its previous file first.

## Known gaps
1. The consent window and the recovery dialog were exercised through automation (the browser test with a mocked host, and clicks inside the real page); the WPF consent window itself has not been clicked by a person.
2. Recovery covers the document. Undo history does not survive a crash, as designed.
3. A recovered session that was not edited since restoring is offered again after another crash (it cannot tell it was restored).
4. Library picture bytes are not garbage-collected automatically; `CollectGarbageAsync` exists and is tested but nothing calls it on a schedule yet.
5. The M0 second-Windows-user pipe check remains open.
