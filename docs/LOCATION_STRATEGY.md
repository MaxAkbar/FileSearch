# Index Location Strategy

FileSearch does not treat every indexed root as equally trackable. Startup catch-up uses the best safe strategy for the root and volume that contain the location.

| Location type | Current strategy |
| --- | --- |
| Local NTFS/ReFS | USN journal catch-up plus `FileSystemWatcher`. |
| Local FAT/exFAT or other non-USN filesystem | Snapshot scan plus `FileSystemWatcher`. |
| Network share or mapped network drive | Startup/scheduled snapshot scan; watcher events are best effort. |
| OneDrive, Dropbox, Box, or Google Drive folder | Snapshot scan plus watcher; delayed hydration is tolerated. |
| Removable drive | Keep cached index while offline; snapshot scan when the drive is available. |

## Runtime Rules

- USN replay is only enabled for roots classified as local NTFS/ReFS and not cloud-backed.
- Cloud-backed folders are classified by known environment roots and common path segments such as `OneDrive`, `Dropbox`, `Box`, and `Google Drive`.
- Unsupported roots still use the existing root refresh path, now described as a snapshot scan in status text.
- Network roots are queued for periodic snapshot refresh because watcher events are not a correctness guarantee.
- Cloud-backed and local non-USN roots are also periodically refreshed with a lower-frequency snapshot scan.
- If a removable root is unavailable at startup, FileSearch keeps the cached index and queues a snapshot scan when the location is available again.
- `FileSystemWatcher` remains enabled when the user selected it, but network watchers are treated as best effort rather than a correctness guarantee.

## Manual Validation

Use real machine paths because cloud clients, network mappings, and removable-drive letters vary by setup.

1. Add a local NTFS/ReFS folder and confirm the strategy shows USN journal catch-up.
2. Add a OneDrive or Dropbox folder and confirm the strategy shows snapshot scan plus watcher.
3. Add a mapped network drive or UNC path and confirm the strategy shows scheduled snapshot scan and watcher best effort.
4. Add a removable drive, close FileSearch, remove the drive, restart FileSearch, and confirm the cached index is retained while unavailable.
5. Reconnect the removable drive and confirm a snapshot scan is queued.
6. Leave a network/cloud root idle long enough for the configured scheduler interval and confirm a low-priority snapshot scan is queued.

## Deferred Work

- User-facing controls for snapshot refresh intervals.
- Per-location UI controls for disabling best-effort watchers on network roots.
- Shared or centralized indexes for enterprise network folders.
