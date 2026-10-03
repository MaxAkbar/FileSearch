# FileSearch Privacy

FileSearch is designed as a local file search application. The app does not require a cloud service for searching, indexing, or previewing files.

## Data Processed Locally

FileSearch may read:

- file names and paths under folders selected by the user,
- file metadata such as size, extension, and modified time,
- file contents for supported text and document formats,
- search queries typed into the app or CLI.

## Data Stored Locally

FileSearch stores user settings under the current user's profile:

- `%AppData%\FileSearch\settings.json`
- `%AppData%\FileSearch\file-types.json`

When indexing is enabled, FileSearch stores extracted searchable content locally:

- `%LocalAppData%\FileSearch\Index\filesearch.db`

The index can include file paths, file metadata, extracted line text, indexing status, and failed-file error information.

When a drive file-name index is enabled, FileSearch also stores a list of the file and folder names on that drive:

- `%LocalAppData%\FileSearch\Index\Volumes\*.fsvol`

A drive index built with the administrator (master file table) scan contains the names of **every** file and folder on the drive. That includes folders the current user cannot open, such as other users' profiles, the same as Everything's database. The folder scan only records folders the user can read. Remove a drive's index from **Settings > Drive file-name index** or with `filesearch volumes remove C:`.

## Telemetry

FileSearch does not currently send telemetry or search data to a remote service.

If telemetry is added later, it should be opt-in, documented here, and avoid collecting file contents, file paths, or search queries by default.

## Clearing Local Data

Use the app's index management UI or CLI index commands to remove indexed locations. Users can also remove FileSearch settings and index files from the paths listed above when the app is closed.

## PowerShell and CLI Usage

PowerShell automation through the CLI runs locally. Output may include file paths and matching line text, so redirect CLI output only to locations you trust.
