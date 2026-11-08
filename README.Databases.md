# SQLite and CSharpDB search

FileSearch searches scalar values in ordinary SQL tables inside local SQLite and CSharpDB files. The engine is detected from the file header. Supported extensions are `.db`, `.sqlite`, `.sqlite3`, `.db3`, `.csharpdb`, `.csdb`, and `.cdb`.

## Searching

1. Close the database's application cleanly or choose a stable backup, including its WAL file when present.
2. Choose the containing folder and the **Database values** starting setup, or select **Contents** and **Document & database text** yourself.
3. Enter a value to find. For a Unified query, use `type:database needle`. **File pattern**, such as `*.sqlite;*.db;*.csdb`, is a separate filter that limits which files are searched; a bare `*.db` term in a Unified content query is a content pattern, not a database-file filter.
4. Refresh the folder's content index to add database contents to existing indexes and Quick Search.

Results preserve the database path, table, row number, column, available record key, and a source fingerprint. Text and numeric values are searched; NULL and binary values are skipped. No user-entered SQL is executed.

The main window explains the active matching mode and scope below the query. Its search route details report whether the request used the index or a live scan, including fallback reasons. An indexed location alone does not guarantee coverage for the current extraction options; refresh incomplete or outdated content indexes as needed. Ordinary database text search works without a Smart Search model or PowerToys.

### How words are combined

Each populated scalar cell is searched separately. Main-window Unified and Boolean modes use implicit **AND** between adjacent terms, support explicit **OR**, and treat a quoted phrase as one term:

| Query | Unified or Boolean | Plain text / Quick Search content |
| --- | --- | --- |
| `john smith` | Both terms must occur in the same cell, in either order. | The literal phrase `john smith`. |
| `john OR smith` | Either term can occur in a cell. | The literal text `john OR smith`, including `OR`. |
| `"john smith"` | The phrase `john smith` in that order. | The quote characters are part of the literal text. |

A record with `john` in FirstName and `smith` in LastName does not satisfy a two-term AND query unless one cell contains both. These searches do not combine values across columns or rows. **Match case** controls case sensitivity in the main window; Quick Search content matching is case-insensitive.

**Filter current results** searches only returned names, paths, and matching text. Use **Find in table** in a database's File preview to search other values in the selected table, including rows beyond the loaded page. Change the main query and run it again when you want to search across files or databases.

```powershell
.\FileSearch.Cli.exe search "type:database invoice" --path "C:\Data" --mode unified --json
.\FileSearch.Cli.exe index build "C:\Data" --json
.\FileSearch.Cli.exe search "type:database invoice" --path "C:\Data" --mode unified --index --json
```

The CLI and desktop share the extractor and limits. CLI JSON and JSON Lines include a `Database` location object; desktop exports preserve it in source anchors/locators. CLI CSV adds database engine/table/row/column/key/fingerprint columns, and Markdown includes readable database locations.

## Table preview

**File preview** in the main window and Quick Search opens a read-only grid at the matching table, row, and column. Filename-only results start at the first supported table. The whole supported record is available around a matching value. **Record key** shows SQLite rowid or primary-key values when available; the row number is a position in this snapshot, not a permanent identifier.

**Open** and **Open at line** also use the built-in database viewer in a separate window, preserving a matching record when available. This works for filename searches such as `*.db` and does not require a default Windows database application. Unsupported or unreadable databases show a preview error.

Use the table selector, **Go to row**, and page buttons to navigate 250-row pages. Columns scroll horizontally. **Expand** retains the table, row, column, available record key, and source fingerprint. Empty tables have an empty state; NULL and binary values appear blank. Limited cell text has an ellipsis and an explicit notice.

Click a column header to sort the displayed page. This does not sort the entire table or change its snapshot row numbers. **Go to row** and Find navigation still use those row numbers, regardless of the displayed sort order.

### Find in a table

1. Open the database's **File preview** in the main window or Quick Search, or open its built-in database viewer.
2. Select the table and click the **Find in table** toolbar button to reveal the Find controls, or press **Ctrl+F** while the database preview is focused. Enter the text to find.
3. Click the options summary button, initially **All terms · All columns ▾**, to show matching and column choices. Choose **All terms**, **Either term**, or **Phrase**, and **All columns** or a specific column. Matching is case-insensitive unless **Match case** is selected.
4. Click **Find**. The detailed options collapse to leave room for records; the summary still shows the selected mode and column scope. Use **‹ Match** and **Match ›** to move between the matching cells. Navigation loads the appropriate 250-row page and selects the matching column; it is not restricted to cells already on screen.

| Find mode with `john smith` | A cell matches when |
| --- | --- |
| All terms | It contains both `john` and `smith`, in either order. |
| Either term | It contains `john`, `smith`, or both. |
| Phrase | It contains the literal text `john smith`, in that order. |

Every mode matches within one cell. Choosing All columns searches cells in each supported column; it does not combine separate column values into a record-wide match. All terms and Either term split the input into whitespace-separated literal terms. Enter a Phrase without surrounding quotes: quote characters and words such as `AND` or `OR` are text, not query operators in this Find control.

With the database preview focused, **Ctrl+F** focuses Find, **F3** moves to the next match, and **Shift+F3** moves to the previous match. **Stop** cancels a running scan. Changing the table or Find settings starts a new search scope; run Find again to populate its matches.

In Quick Search, opening Find gives the database preview more room. Hide Find to restore the compact results-list layout.

Long matching cells show an excerpt beginning at the first match so the highlighted text is visible. Hover over the cell to read its full bounded value; copying the cell copies that value rather than the Find excerpt. Click **Find in table** again to hide the controls. After a Find scan finishes, **Expand** retains its query, options, matching cells, and the open or hidden state of the Find controls and detailed options, along with the current database location while the source remains unchanged.

Find scans the selected table in the existing private snapshot. Its limits are the first **100,000 rows**, **250,000 non-NULL scalar cells** (empty strings count), **16 Mi characters**, **16,384 characters per cell**, **1,000 matching cells**, and **15 seconds** per scan. NULL and binary values are skipped. Find input is limited to **2,048 characters**; All terms and Either term accept up to **64 literal terms**. The result status identifies incomplete coverage, including scan limits, truncated cell text, cancellation, or a changed source. A limited or cancelled scan does not establish that the rest of the table contains no matches. These limits are separate from the 250-row display pages and the global file-search extraction limits below.

Find uses the same read-only snapshot protections and requires no SQL, Smart Search model, OCR, or PowerToys setup. If the database or WAL changes, reopen the preview from a fresh search; refresh the index first when it supplied the hit. The old snapshot's row positions and matches must not be treated as current.

Snapshot preparation and page reads run in the background. The status shows copy progress (including WAL bytes), opening, schema discovery, and source validation. **Stop loading** cancels preparation or record navigation; it is separate from **Stop** in Find. Select the file again to retry preparation. A canceled page keeps the previously displayed records when available.

The visible preview reuses its private session and, where possible, its existing grid when selecting another hit in the same unchanged database. Existing rows are disabled while that source is checked. Changed or failed preparation clears them; canceling ordinary page navigation can keep the previous page. Changing files, hiding/closing the preview, or detecting a changed database/WAL ends session reuse. A stale indexed hit still requires a refreshed index or a fresh search. Expanded previews own a separate snapshot.

Sequential pages use a small session-local continuation cache when the table has a native SQLite rowid or a single CSharpDB integer primary key. Row numbers and record locations retain their original ordering. Arbitrary row jumps, evicted continuations, and tables without a suitable key use bounded `LIMIT`/`OFFSET` queries, so a deep jump can still take longer while the engine skips earlier rows. Larger files also require more snapshot copying before the first table appears. Pagination limits the displayed records; it does not guarantee a constant loading time.

If the main database or its WAL changes, refresh the index or rerun the search before using an old hit location. WAL watcher events are routed to their parent database, and explicit refreshes reread databases with WAL files even when the main file's timestamp is unchanged. FileSearch's configured index is excluded from extraction, and its storage watcher events are ignored.

## Read-only behavior and limits

Source files are opened only for reading. FileSearch creates a bounded private database/WAL copy, verifies that the source stayed unchanged, and reads the copy. SQLite additionally uses a read-only connection; CSharpDB recovery and close-time writes affect only the private copy. Snapshots under `%TEMP%\FileSearchDatabasePreview` are removed when their session closes. After a crash or locked-file cleanup failure, close FileSearch and clear that directory to remove remaining copies.

- Snapshots, including WAL data, are limited to **256 MB**. The normal search file-size filter defaults to **100 MB** for the main file; increase it explicitly for larger supported sources. File preview uses the snapshot limit directly.
- Up to **256 user tables** and **512 columns per table** are supported. Views, virtual tables, SQLite shadow/system tables and generated columns, CSharpDB internal tables, and binary payloads are excluded. Typed NoSQL collections stored as binary payloads are not decoded.
- Search stops at **100,000 rows total**, **250,000 populated scalar cells**, or **16 Mi characters** per database. Extraction issues report limits reached. Preview navigation supports the first **100,000 rows per table**.
- Cell text is limited to **16,384 characters**, with an explicit notice. Text beyond that limit is not searched. Existing hit/display limits still apply.
- Locked, changing, encrypted, corrupt, or incompatible databases can fail. A rollback journal is refused; close its application cleanly or use a valid backup. No passwords are requested, extensions loaded, or arbitrary views/procedures invoked.
- CSharpDB file compatibility follows the bundled **CSharpDB.Engine 4.7.0** reader. Remote database servers are outside this feature.

Extracted values are stored locally in the index like other document text. See [Privacy](PRIVACY.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
