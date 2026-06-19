# FileSearch Indexed Search

FileSearch supports optional CSharpDB-backed indexing. Live filesystem search remains the default behavior and the correctness baseline. When **Use index** is enabled and the index covers the selected folder/options, searches read metadata and pre-extracted lines from the local database instead of reopening every file.

The index can cover multiple opt-in locations. FileSearch can update them from the main GUI process while the window is open or from the separate tray indexer process when background indexing is enabled.

## Storage

The index database is stored at:

```text
%LocalAppData%\FileSearch\Index\filesearch.db
```

The Core project uses `CSharpDB.Engine` directly. The requested `CSharpDB` 3.9.0 meta-package currently pulls missing NuGet dependencies (`CSharpDB.CodeModules` and `CSharpDB.ImportExport`), so the implementation references the newest buildable engine package available in this environment: `CSharpDB.Engine` 3.7.0.

## What Gets Indexed

Each indexed location stores the root folder plus the recursive, hidden-file, document-extraction, unknown-file-type, watcher, and basic stats settings used for that location. A build intentionally ignores transient search filters such as file name pattern, size, and modified date so later searches can narrow against the same folder index.

The database stores:

- indexed roots and the index coverage profile,
- per-volume strategy and journal checkpoint metadata,
- file path, name, extension, size, modified timestamp, file identity, last observed USN, extractor metadata, status, and error text,
- extracted line number and content,
- pending filesystem changes that need recovery after app restart,
- failed/skipped extraction records and archive member skip reasons,
- a CSharpDB full-text index over line content.

## Using Indexed Search

1. Choose a folder in **Look in**.
2. Use **Index > Manage indexed locations...**.
3. Add the current folder or choose another folder from the dialog.
4. Leave **Use index** enabled in the search chips.
5. Run searches as usual.

If the current search is broader than the indexed profile, FileSearch reports that the index does not cover the search and safely falls back to the live scanner. If the index is stale or missing, FileSearch uses live scan results and schedules background indexing for the covered root.

## Indexed Locations

The sidebar includes an **Indexed locations** view with a compact list and a **Manage locations** action. The management dialog shows every indexed root, its stats, watcher state, and indexing options. Use it to add the current folder, add another folder, rebuild a selected location, or remove a selected location. Removing a location stops its watcher and clears that root from the index.

Each indexed location shows a runtime status badge: **Indexing now**, **Queued**, **Paused**, or **Ready**. A separate **Watching changes** badge indicates that a live watcher is active in the process currently owning indexing. The status bar also shows the active indexed folder when a background job is running.

The **Index** menu includes:

- **Manage indexed locations...**
- **Index health**
- **Regex tester**
- **Pause background indexing**
- **Resume background indexing**
- **Open index location**

## GUI And Tray Indexer Lifecycle

There are two indexing owners:

- `FileSearch.Gui.exe` owns indexing while the visible search UI is running and background indexing is not handed off.
- `FileSearch.Indexer.exe` is a per-user tray indexer. It owns the same settings and database when **Keep index updated after closing window** or **Start background indexer when Windows starts** is enabled.

Closing the main window behaves differently based on settings:

- If **Keep index updated after closing window** is disabled, closing exits the GUI and no background worker is kept alive.
- If **Keep index updated after closing window** is enabled, closing the GUI starts or keeps the tray indexer running, then exits the GUI.
- The tray icon's **Exit** command shuts down the tray indexer. The GUI **Exit** path is an explicit shutdown path and does not hide the app to the tray.

When **Start background indexer when Windows starts** is enabled, FileSearch registers the current user's Windows startup entry for `FileSearch.Indexer.exe --background`. This does not install a Windows Service and does not require elevation. Startup indexing begins after the user signs in.

Only one user-session indexer should own the database at a time. The GUI and worker use current-user named-pipe IPC and a worker single-instance guard so a second worker launch forwards its intent and exits.

## Live Watchers

Watchers run only while either the GUI indexing owner or the tray indexer is running, and only for locations the user explicitly adds with watcher support enabled. Each watched root uses a `FileSystemWatcher`; events are debounced and coalesced before work reaches the database:

- repeated writes become one file upsert,
- delete events remove stale indexed rows,
- rename events delete the old path and index the new path,
- root refreshes are queued as low-priority background work.

Search always has priority. Foreground indexed search reads the last committed index state while background jobs continue. Search cancellation cancels search only; background indexing has separate pause/resume controls.

While live search scans files, it can enqueue processed files for background indexing. FileSearch does not write index rows inline on the search hot path.

## Startup Catch-Up And Filesystem Strategy

On startup, the active indexing owner starts watchers, loads persisted pending changes, then chooses a catch-up strategy per root:

- **Local NTFS fixed drives** use USN Change Journal replay when a valid volume checkpoint exists.
- **Local FAT/exFAT, ReFS, cloud-backed folders, and removable drives** use snapshot scans plus watcher updates when available.
- **Network shares and mapped network drives** use scheduled snapshot scans; watcher events are best effort and are not treated as the correctness baseline.
- **Unavailable removable or disconnected roots** keep cached index entries and show as offline until the folder is reachable again.

Durable USN replay is currently enabled only for local NTFS volumes. ReFS is intentionally routed to snapshot scans because ReFS commonly uses 128-bit file identifiers and FileSearch's current resolver persists and resolves 64-bit NTFS identities. ReFS USN replay is deferred until 128-bit identity storage, `ExtendedFileIdType`, and integration tests are in place.

During a full refresh or validation scan, FileSearch records a conservative volume checkpoint: volume identity, filesystem, journal ID, last committed USN, last check time, strategy, and health. On a later startup, FileSearch replays changes from that checkpoint only if the journal ID still matches and the saved USN is still retained by the journal.

USN replay falls back to a snapshot scan when it cannot prove safety. Common fallback reasons include:

- unsupported filesystem or remote root,
- missing, incomplete, stale, or newer-than-journal checkpoint,
- journal recreated or journal ID mismatch,
- checkpoint older than `FirstUsn`,
- access denied or transient failure while resolving a file ID,
- changed directory path cannot be resolved,
- explicit file delete for a known file, because V1 does not fully track hard-link path identity.

Hard links are not treated as fully supported by USN replay yet. When a delete could remove one hard-link path while another path survives, FileSearch falls back to root validation instead of deleting by file ID and advancing the checkpoint.

Snapshot scans remain the correctness fallback. They walk the root, skip unchanged files, update changed files, remove missing files, and refresh checkpoint/health metadata when possible.

## Resource Management

The background indexer uses one worker by default. Root refreshes wait while a foreground search is active; small single-file updates may still run. Large refreshes use a burst budget so indexing works for a while, rests briefly, and then resumes. This keeps the app responsive during long initial indexes and large folder changes.

Settings include options to pause indexing on battery, limit CPU, and add disk pauses between indexed files. Lower resource profiles trade catch-up speed for less foreground interference.

## Refresh Behavior

Refreshing an indexed location:

- skips unchanged files by comparing path, size, and modified UTC timestamp,
- re-extracts changed files,
- removes deleted files from the index,
- records failed files without failing the whole refresh.

Incremental watcher updates use the same per-file extraction path as a refresh.

## Index Health

Use the **Index health** tab in the indexed locations window to inspect whether each root is current. The health table shows root, status, files indexed, pending files, failed files, last successful scan, last USN checkpoint, last watcher event, last full validation, journal status, extractor failures, queue depth, estimated catch-up time, and selected strategy.

Health statuses mean:

- **Healthy**: no queued catch-up work and the latest diagnostics look current.
- **Watching**: a live watcher is active and no queued catch-up work is pending.
- **Catching up**: the root is actively being refreshed or has queued work.
- **Paused**: indexing is paused while work is active or queued.
- **Needs full scan**: journal replay or validation detected a condition that requires a snapshot scan.
- **Journal unavailable**: the root cannot currently use the USN journal.
- **Journal expired**: the saved checkpoint is older than the retained journal range.
- **Access denied**: a root, watcher, journal, or path resolution operation was blocked.
- **Offline**: the folder is not reachable; cached index entries are retained.
- **Too many extractor failures**: failed files exceed the current warning threshold.

## Query Semantics

Indexed search preserves the existing plain text, regex, and Boolean query behavior. CSharpDB full-text search is used only to find candidate line rows when safe. Every candidate is rechecked with FileSearch's existing query engine before a hit is returned, and highlights are generated the same way as live search.

## Maintenance

Use **Index > Manage indexed locations...** to remove a selected folder from the database.

Use **Rebuild selected** or health-tab validation in the indexed locations dialog for manual recovery when a location looks stale or unhealthy.

Use **Index > Pause background indexing** when you want indexing to stop temporarily while keeping indexed search available. Pause stops the indexing queue from making progress but does not remove watchers, settings, or index data. Resume continues queued work.

Use the tray or GUI **Exit** command when you want the background indexer to shut down. Closing the GUI window may hand off to the tray indexer if **Keep index updated after closing window** is enabled.

The CLI can inspect and refresh the same CSharpDB database with `index locations`, `index stats`, `index build`, `index rebuild`, and `index clear`.

Use **Index > Open index location** to inspect or delete the database manually.
