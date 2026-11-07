# SQLite and CSharpDB search

FileSearch searches scalar values in ordinary SQL tables inside local SQLite and CSharpDB files. The engine is detected from the file header. Supported extensions are `.db`, `.sqlite`, `.sqlite3`, `.db3`, `.csharpdb`, `.csdb`, and `.cdb`.

## Searching

1. Close the database's application cleanly or choose a stable backup, including its WAL file when present.
2. Choose the containing folder, **Contents**, and **Document & database text**.
3. Search normally or use Unified mode with `type:database needle`. Filename patterns such as `*.sqlite;*.db;*.csdb` also narrow the scope.
4. Refresh the folder's content index to add database contents to existing indexes and Quick Search.

Results preserve the database path, table, row number, column, available record key, and a source fingerprint. Text and numeric values are searched; NULL and binary values are skipped. No user-entered SQL is executed.

```powershell
.\FileSearch.Cli.exe search "type:database invoice" --path "C:\Data" --mode unified --json
.\FileSearch.Cli.exe index build "C:\Data" --json
.\FileSearch.Cli.exe search "type:database invoice" --path "C:\Data" --mode unified --index --json
```

The CLI and desktop share the extractor and limits. CLI JSON and JSON Lines include a `Database` location object; desktop exports preserve it in source anchors/locators. CLI CSV adds database engine/table/row/column/key/fingerprint columns, and Markdown includes readable database locations.

## Table preview

**File preview** in the main window and Quick Search opens a read-only grid at the matching table, row, and column. Filename-only results start at the first supported table. The whole supported record is available around a matching value. **Record key** shows SQLite rowid or primary-key values when available; the row number is a position in this snapshot, not a permanent identifier.

**Open** and **Open at line** also use the built-in database viewer in a separate window, preserving a matching record when available. This works for filename searches such as `*.db` and does not require a default Windows database application. Unsupported or unreadable databases show a preview error.

Use the table selector, **Go to row**, and page buttons to navigate 250-row pages. Columns scroll horizontally. **Expand** retains the table, row, and column. Empty tables have an empty state; NULL and binary values appear blank. Limited cell text has an ellipsis and an explicit notice.

Snapshot preparation and page reads run in the background. Before showing the first table, FileSearch copies the database and its WAL into a private snapshot and reads the table schemas. Larger files can therefore take longer to open even though records are paginated. Page queries use `LIMIT`/`OFFSET`, so jumping far into a table can also take longer while the engine skips earlier rows. Pagination limits the displayed records; it does not guarantee a constant loading time.

If the main database or its WAL changes, refresh the index or rerun the search before using an old hit location. WAL watcher events are routed to their parent database, and explicit refreshes reread databases with WAL files even when the main file's timestamp is unchanged. FileSearch's configured index is excluded from extraction, and its storage watcher events are ignored.

## Read-only behavior and limits

Source files are opened only for reading. FileSearch creates a bounded private database/WAL copy, verifies that the source stayed unchanged, and reads the copy. SQLite additionally uses a read-only connection; CSharpDB recovery and close-time writes affect only the private copy. Snapshots under `%TEMP%\FileSearchDatabasePreview` are removed when their session closes. After a crash or locked-file cleanup failure, close FileSearch and clear that directory to remove remaining copies.

- Snapshots, including WAL data, are limited to **256 MB**. The normal search file-size filter defaults to **100 MB** for the main file; increase it explicitly for larger supported sources. File preview uses the snapshot limit directly.
- Up to **256 user tables** and **512 columns per table** are supported. Views, virtual tables, SQLite shadow/system tables and generated columns, CSharpDB internal tables, and binary payloads are excluded. Typed NoSQL collections stored as binary payloads are not decoded.
- Search stops at **100,000 rows total**, **250,000 populated scalar cells**, or **16 Mi characters** per database. Extraction issues report limits reached. Preview navigation supports the first **100,000 rows per table**.
- Cell text is limited to **16,384 characters**, with an explicit notice. Text beyond that limit is not searched. Existing hit/display limits still apply.
- Locked, changing, encrypted, corrupt, or incompatible databases can fail. A rollback journal is refused; close its application cleanly or use a valid backup. No passwords are requested, extensions loaded, or arbitrary views/procedures invoked.
- CSharpDB file compatibility follows the bundled **CSharpDB.Engine 4.6.4** reader. Remote database servers are outside this feature.

Extracted values are stored locally in the index like other document text. See [Privacy](PRIVACY.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
