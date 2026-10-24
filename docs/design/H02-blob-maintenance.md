# H02: reclaiming unused picture bytes

Issue: [#20](https://github.com/rp2839/visio-mcp/issues/20). Code: `BlobMaintenance` (`src/Diagram.Host.Core/BlobMaintenance.cs`), `BlobStore` and `BlobStoreLease` (`Storage.cs`), `BlobGc` (`Recovery.cs`), wired in `MainWindow`.

## Trigger and retention

| Setting | Value | Why |
|---|---|---|
| First pass | 10 minutes after the window starts | Never competes with opening documents, recovery offers or a restore. |
| Interval | At most once per 24 hours, kept across restarts (`blob-maintenance.json` beside the picture folder) | Frequent restarts do not repeat a pass. A failed or skipped pass keeps the last collection time, so it is tried again 10 minutes after the next start. |
| Grace | 7 days | A picture stored **or touched** within the last week is never removed, whatever references it. |

## What keeps a picture

A picture is removed only if it is older than the grace period **and** none of these name it:

- every open document, through `doc.retainedBlobs` (its assets plus undo and redo history). If any open document does not answer, nothing is removed.
- every recovery session on disk: checkpoints and journal records.
- every library entry: the current version and every earlier one (`History`).
- every unexpired preparation in `AssetPreparer` (an upload an agent or the page has prepared but not yet placed; they live 10 minutes).

Saved `.diagram.json` and `.vsdx` files carry their own picture bytes, so a closed, saved document does not need the folder.

## Races fixed on the way

- **Re-uploading an old orphan.** `PutDurableAsync` skipped the write for a picture already present and left its old time, so a collection could delete it between preparing and placing it. Storing an existing picture now touches it.
- **Touched between listing and deleting.** `BlobGc` deletes through `DeleteIfIdleAsync`, which rechecks the time under the same lock that `PutDurableAsync` takes, so a picture handed out again during a pass is kept.
- **Another process.** `InstanceGuard` allows one window per user and logon session, so the same user signed in twice runs two processes over one folder, and neither can see the other's documents. Each process holds a lease file in `blobs/.holders` (open with no sharing, deleted on close). A pass runs only when this process holds the only live lease; a lease left by a crashed process is deleted when found.

## Limits

- A process of an older build (no lease) is not detected.
- An interrupted pass stops between files; whatever it deleted was unreferenced, and the next pass finishes the rest. It does not resume immediately; it waits for the next interval.
- The in-app clipboard holds asset ids, not bytes, and a paste reuses an asset the target document already holds, so it needs no pin of its own.

## Evidence

- `tests/Diagram.Host.Tests/BlobMaintenanceTests.cs`: touch on re-upload, recheck under the lock, freed bytes, interrupted then retried collection, leases (live, released, abandoned), schedule maths, outcomes of a pass.
- `tests/Diagram.Host.Tests/MultiDocumentTests.cs` (`AMaintenancePassKeepsEveryHolderAndReclaimsTheRest`): two open documents, a library entry with two versions, an old orphan uploaded again, through the real `RecoveryManager`.
- `tests/Diagram.Windows.Tests/BlobMaintenanceLiveTests.cs`: the window's own pass against the live editor and MCP; result in [evidence/H02/maintenance-live.json](../acceptance/evidence/H02/maintenance-live.json) (2 old orphans removed, 60,035 bytes; a picture only the page holds, a library-only picture and a recent upload kept; a second pass removes nothing).
