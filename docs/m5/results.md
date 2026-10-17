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
