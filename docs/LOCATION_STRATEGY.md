# Index Location Strategy

FileSearch does not treat every indexed root as equally trackable. Startup catch-up uses the best safe strategy for the root and volume that contain the location.

| Location type | Current strategy |
| --- | --- |
| Local NTFS fixed drive | USN journal catch-up plus `FileSystemWatcher`. |
| Local ReFS | Snapshot scan plus `FileSystemWatcher` until 128-bit ReFS file identifiers are supported. |
| Local FAT/exFAT or other non-USN filesystem | Snapshot scan plus `FileSystemWatcher`. |
| Network share or mapped network drive | Startup/scheduled snapshot scan; watcher events are best effort. |
| OneDrive, Dropbox, Box, or Google Drive folder | Snapshot scan plus watcher; delayed hydration is tolerated. |
| Removable drive | Keep cached index while offline; snapshot scan when the drive is available. |

## Runtime Rules

- USN replay is only enabled for roots classified as local NTFS fixed-drive roots and not cloud-backed.
- ReFS is routed to snapshot scans because FileSearch does not yet persist or resolve 128-bit ReFS file identifiers.
- Cloud-backed folders are classified by known environment roots and common path segments such as `OneDrive`, `Dropbox`, `Box`, and `Google Drive`.
- Unsupported roots still use the existing root refresh path, now described as a snapshot scan in status text.
- Network roots are queued for periodic snapshot refresh because watcher events are not a correctness guarantee.
- Cloud-backed and local non-USN roots are also periodically refreshed with a lower-frequency snapshot scan.
- If a removable root is unavailable at startup, FileSearch keeps the cached index and queues a snapshot scan when the location is available again.
- `FileSystemWatcher` remains enabled when the user selected it, but network watchers are treated as best effort rather than a correctness guarantee.
- Known hard-link delete ambiguity during USN replay causes root validation fallback instead of deleting by file ID.

## Manual Validation

Use real machine paths because cloud clients, network mappings, and removable-drive letters vary by setup.

1. Add a local NTFS fixed-drive folder and confirm the strategy shows USN journal catch-up.
2. Add a local ReFS folder, when available, and confirm the strategy shows snapshot scan plus watcher with a ReFS identifier warning.
3. Add a OneDrive or Dropbox folder and confirm the strategy shows snapshot scan plus watcher.
4. Add a mapped network drive or UNC path and confirm the strategy shows scheduled snapshot scan and watcher best effort.
5. Add a removable drive, close FileSearch, remove the drive, restart FileSearch, and confirm the cached index is retained while unavailable.
6. Reconnect the removable drive and confirm a snapshot scan is queued.
7. Leave a network/cloud root idle long enough for the configured scheduler interval and confirm a low-priority snapshot scan is queued.

## Deferred Work

- User-facing controls for snapshot refresh intervals.
- Per-location UI controls for disabling best-effort watchers on network roots.
- ReFS USN replay with 128-bit file identity persistence and native tests.
- Hard-link-aware USN delete/rename replay.
- Shared or centralized indexes for enterprise network folders.
