# FileSearch Indexed Search

FileSearch supports optional CSharpDB-backed indexing. Live filesystem search remains the default behavior and the correctness baseline. When **Use index** is enabled and the index covers the selected folder/options, searches read metadata and pre-extracted lines from the local database instead of reopening every file.

The index can cover multiple opt-in locations. FileSearch can update them from the main GUI process while the window is open or from the separate tray indexer process when background indexing is enabled.

## Storage

The index database is stored at:

```text
%LocalAppData%\FileSearch\Index\filesearch.db
```

The Core project uses `CSharpDB.Engine` directly. The current indexed-search store references `CSharpDB.Engine` 4.0.2.

CSharpDB 4.0.2 supplies bounded, chunked full-text postings for hot terms and index-planned `DELETE`/`UPDATE` predicates. FileSearch keeps its app-level trigram index because it provides the required mid-token substring behavior, while changed-file updates now use the indexed mutation path to remove the superseded file version and all of its owned rows in one transaction. The new version is published first, so a cleanup failure cannot erase the last searchable copy. This integration does not change the FileSearch schema version and does not require an existing index to be rebuilt.

Smart Search stores semantic vectors beside the lexical database:

```text
%LocalAppData%\FileSearch\Index\filesearch.vectors.json
%LocalAppData%\FileSearch\Index\filesearch.vectors.segments\
```

The vector manifest uses immutable root snapshots and small per-file overlay segments. A root rebuild embeds chunks across files in token-budgeted ONNX batches and publishes one manifest update; watcher changes replace only the affected file overlay. Segment payloads use symmetric int8 storage, while retrieval remains an exhaustive cosine-ranked scan. The current 200,000-vector benchmark remains below the exact-search gate, so FileSearch does not ship an HNSW native dependency.

Vector formats 1 and 2 remain readable and migrate to format 3 on the first semantic mutation. Older vectors without root ownership are intentionally excluded from root-scoped Smart Search and discarded during migration; roots with incomplete semantic coverage automatically queue a semantic-only rebuild after startup catch-up. The lexical CSharpDB index does not need to be rebuilt for this vector-store migration.

## What Gets Indexed

Each indexed location stores the root folder plus the recursive, hidden-file, document-extraction, image-OCR, unknown-file-type, watcher, and basic stats settings used for that location. A build intentionally ignores transient search filters such as file name pattern, size, and modified date so later searches can narrow against the same folder index.

The database stores:

- indexed roots and the index coverage profile,
- per-volume strategy and journal checkpoint metadata,
- file path, name, extension, size, modified timestamp, file identity, last observed USN, extractor metadata, status, and error text,
- extracted line number, content, and optional source-anchor metadata,
- pending filesystem changes that need recovery after app restart,
- failed/skipped extraction records and archive member skip reasons,
- extracted line content plus per-file trigram postings used to bound substring and regex candidate sets.

When Image OCR is enabled for an indexed location, FileSearch uses Windows OCR to extract searchable lines from PNG, JPEG, BMP, and TIFF images, PDF pages that do not expose native PDF text, and embedded images in Office, OpenDocument, EPUB, email, and ZIP/archive files. OCR output is stored as text lines in the same local index, with the OCR line's image, PDF page, or embedded-member bounding region stored as source-anchor metadata. Result rows and exports can show the region label, and preview panes can render standalone image files or PDF pages with the OCR region highlighted. Opening the result still opens the original file in the default app.

## Using Indexed Search

1. Choose a folder in **Look in**.
2. Use **Index > Manage indexed locations...**.
3. Add the current folder or choose another folder from the dialog.
4. Leave **Use index** enabled in the search chips.
5. Run searches as usual.

If the current search is broader than the indexed profile, FileSearch reports that the index does not cover the search and safely falls back to the live scanner. If the index is stale or missing, FileSearch uses live scan results and schedules background indexing for the covered root.

Literal and Boolean file-name searches use an in-memory trigram index built from the stored file metadata, including matches in the middle of a name. File-name query forms that cannot be narrowed safely, such as regular expressions, fall back to the live scanner. Folder-name and combined file/folder-name searches also remain live-scanner operations so their results represent actual folders rather than files stored beneath matching paths.

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
After a replacement is fully written, FileSearch atomically removes the superseded version's metadata tokens, extraction issues, trigrams, lines, content units, and file row through `file_id` indexes. Exact file deletes publish a tombstone first and then use the same cleanup transaction.

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

## Drive File-Name Index

Besides the per-folder content index, FileSearch can keep a **whole-drive file and folder name index** for local NTFS drives, the same idea as Everything's MFT index. It holds names only (no contents) and answers filename searches across millions of entries in tens of milliseconds.

Turn it on per drive in **Settings > Drive file-name index**, or from the CLI with `volumes build C:`.

### Building

- **Master file table (fast).** FileSearch enumerates the NTFS master file table with `FSCTL_ENUM_USN_DATA`. Windows only allows that for administrators, so an unelevated app starts `FileSearch.Indexer.exe --scan-volume` through a UAC prompt. The elevated helper writes one snapshot file and exits; nothing else runs elevated. Declining the prompt falls back to the folder scan.
- **Folder scan (no elevation).** FileSearch walks every folder the user can open and reads each entry's NTFS file ID with `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`. This produces the same file-ID-keyed index, but it is slower and skips folders the user cannot read.

**Use fast administrator scan** chooses between them. Drives enabled in settings that have no index yet are built automatically with the folder scan at startup; FileSearch never shows a UAC prompt without a click.

### Staying current

The index is keyed by NTFS file reference numbers, so the change journal can update it in place. The GUI reads the journal unprivileged (`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`) about once a second. Those records carry file IDs, parent IDs, reasons, and attributes but no names, so the name of each created or renamed file is looked up with `OpenFileById`. A folder rename or move updates every path beneath it at no cost, because paths are rebuilt from parent links.

Changes are merged per file before they are applied, so a build that creates and deletes thousands of temporary files costs almost nothing. Searches from the main window always replay the journal first, so a file created a moment earlier is found.

If the journal was recreated, or has wrapped past the saved checkpoint, the drive is marked **needs rebuild** and rebuilt automatically with the folder scan.

### Storage

```text
%LocalAppData%\FileSearch\Index\Volumes\Volume{GUID}.fsvol
```

Each snapshot is a Brotli-compressed file holding every entry's record number, parent, attributes, and name, plus the journal ID and checkpoint it matches. The GUI rewrites a changed snapshot every five minutes and on exit. On the next start it loads the snapshot (about a second for 5 million entries) and replays the journal from the checkpoint.

In memory, names are stored once in a shared pool, one byte per character for ASCII names, and every other column is an array indexed by record number. That works out to about 38 bytes per entry.

### Where it is used

- **Quick Search.** The *Entire machine* scope searches the drive index of every indexed drive and is complete instead of time-boxed. Other scopes use it for any root on an indexed drive. Every term must appear in the entry's name or one of its folders, at least one term must appear in the entry's own name, and a term containing `\` must appear in the full path. Results are ranked by name match, then shallower paths.
- **Main window and CLI name searches.** File-name, folder-name, and file-and-folder-name searches with **Use index** on (or `--index` in the CLI) are answered from the drive index whenever every root is on an indexed drive. Results are designed to equal the live walk: the same hidden, system, and excluded folders are skipped, the same filters apply, and matching and result formatting share the live searcher's code. The integration tests compare both routes on a real folder.
- **CLI.** `volumes list`, `volumes build DRIVE [--admin|--walk]`, `volumes remove DRIVE`, and `volumes search TEXT [--drive DRIVE] [--path FOLDER] [--files|--folders] [--json|--jsonl|--csv|--markdown]`.

The CLI never tracks drives live and never writes snapshots except from `volumes build`. It loads a snapshot the first time a search needs it and replays the journal in memory. The MCP server's tools do not request index use for name searches, so they do not load drive indexes.

### Limits

- **Folder links.** The live file walker follows junctions and directory symlinks, but the index stores their contents under the link target. A file-name search that could reach such a link therefore falls back to the live walk. Folder-name searches never enter reparse points, so they stay on the index. OneDrive placeholder folders are real folders and are fully indexed.
- **Hard links.** A file with several hard links is indexed under one of its names.
- **Unreadable folders.** After an administrator scan, changes inside folders the user cannot open are only partly tracked unprivileged: deletes and moves apply, but new names cannot be read.
- **Supported drives.** Only local fixed NTFS drives can be indexed. A drive without a change journal can be indexed but does not update live.
- **Store package.** The Store (MSIX) package may not be allowed to elevate the helper; there the folder scan is used.

### Measured

These numbers come from a developer workstation with 5.24 million entries on C:, measured with `dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks -- volume --drive C: --method walk --parity-scope <folder>`:

| Metric | Value |
| --- | ---: |
| Master file table build from the CLI (`volumes build C: --admin`), end to end including the UAC prompt, snapshot write, and load | 15.7 s, 5.72M entries |
| Folder scan, cold / warm file cache | 127 s / 21 s, 5.24M entries |
| Memory | 188 MiB (38 bytes/entry) |
| Snapshot size, write / load | 64.5 MiB, 1.1 s / 1.1 s |
| Ranked query over the whole drive, P50 | 19–84 ms (84 ms for a one-letter query with 1.7M matches) |
| Journal catch-up after 2,000 new files | 178 ms |
| Main-window name search vs live walk, same results | 0.76 s vs 5.2 s (6,300 hits); 1.1 s vs 8.5 s (19,193 folders) |

The master file table finds more entries than the folder scan (5.72M vs 5.24M here) because it includes folders the user cannot open. The benchmark's other rows used the folder scan; run it with `--method mft` from an elevated terminal to measure the scan alone.

## Query Semantics

Indexed search preserves the existing plain text, regex, and Boolean query behavior. The index uses app-level trigram postings to find candidate files when a query has required literals, and falls back to scanning indexed line rows for patterns that cannot be safely bounded. Every candidate is rechecked with FileSearch's existing query engine before a hit is returned, and highlights are generated the same way as live search.

## Maintenance

Use **Index > Manage indexed locations...** to remove a selected folder from the database.

Use **Rebuild selected** or health-tab validation in the indexed locations dialog for manual recovery when a location looks stale or unhealthy.

Use **Index > Pause background indexing** when you want indexing to stop temporarily while keeping indexed search available. Pause stops the indexing queue from making progress but does not remove watchers, settings, or index data. Resume continues queued work.

Use the tray or GUI **Exit** command when you want the background indexer to shut down. Closing the GUI window may hand off to the tray indexer if **Keep index updated after closing window** is enabled.

The CLI can inspect and refresh the same CSharpDB database with `index locations`, `index stats`, `index build`, `index rebuild`, and `index clear`.

Use **Index > Open index location** to inspect or delete the database manually.
