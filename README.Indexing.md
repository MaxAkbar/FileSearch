# FileSearch Indexed Search

FileSearch supports optional CSharpDB-backed indexing. Live filesystem search remains the default behavior and the correctness baseline. When **Use index** is enabled and the index covers the selected folder/options, searches read pre-extracted lines from the local database instead of reopening every file.

The index can now cover multiple opt-in locations. While the app is open, FileSearch can watch those locations, batch filesystem changes, and update the index in the background without blocking foreground search.

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
- file path, name, extension, size, modified timestamp, status, and error text,
- extracted line number and content,
- pending filesystem changes that need recovery after app restart,
- a CSharpDB full-text index over line content.

## Using Indexed Search

1. Choose a folder in **Look in**.
2. Use **Index > Add current folder to index**.
3. Leave **Use index** enabled in the search chips.
4. Run searches as usual.

If the current search is broader than the indexed profile, FileSearch reports that the index does not cover the search and safely falls back to the live scanner. If the index is stale or missing, FileSearch uses live scan results and schedules background indexing for the covered root.

## Indexed Locations

The sidebar includes an **Indexed locations** view. Use it to add the current folder, rebuild a selected location, or remove a selected location. Removing a location stops its watcher and clears that root from the index.

The **Index** menu includes:

- **Add current folder to index**
- **Pause background indexing**
- **Resume background indexing**
- **Rebuild selected index**
- **Remove selected index**
- **Clear index for current folder**
- **Open index location**

## Background Indexing

Watchers run only while FileSearch is open and only for locations the user explicitly adds. Each watched root uses a `FileSystemWatcher`; events are debounced and coalesced before work reaches the database:

- repeated writes become one file upsert,
- delete events remove stale indexed rows,
- rename events delete the old path and index the new path,
- root refreshes are queued as low-priority background work.

Search always has priority. Foreground indexed search reads the last committed index state while background jobs continue. Search cancellation cancels search only; background indexing has separate pause/resume controls.

While live search scans files, it can enqueue processed files for background indexing. FileSearch does not write index rows inline on the search hot path.

## Resource Management

The background indexer uses one worker by default. Root refreshes wait while a foreground search is active; small single-file updates may still run. Large refreshes use a burst budget so indexing works for a while, rests briefly, and then resumes. This keeps the app responsive during long initial indexes and large folder changes.

## Refresh Behavior

Refreshing an indexed location:

- skips unchanged files by comparing path, size, and modified UTC timestamp,
- re-extracts changed files,
- removes deleted files from the index,
- records failed files without failing the whole refresh.

Incremental watcher updates use the same per-file extraction path as a refresh.

## Query Semantics

Indexed search preserves the existing plain text, regex, and Boolean query behavior. CSharpDB full-text search is used only to find candidate line rows when safe. Every candidate is rechecked with FileSearch's existing query engine before a hit is returned, and highlights are generated the same way as live search.

## Maintenance

Use **Index > Clear index for current folder** to remove the selected folder from the database.

Use **Index > Rebuild selected index** for manual recovery when a location looks stale or unhealthy.

Use **Index > Pause background indexing** when you want indexing to stop temporarily while keeping indexed search available.

Use **Index > Open index location** to inspect or delete the database manually.
