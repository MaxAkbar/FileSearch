# FileSearch Competitive Roadmap Refresh

## Summary

FileSearch has already closed two major gaps from the older competitive analysis: optional CSharpDB-backed indexing and a Spectre.Console REPL CLI now exist. The remaining work should focus on turning FileSearch from a capable desktop scanner into a polished, daily-driver local search platform with reliable releases, measurable performance, richer indexed-search UX, better automation output, saved workflows, and clear product maturity around accessibility, privacy, and security.

## Current State

Completed since the older roadmap:

- Hybrid live/indexed search with CSharpDB-backed extracted line storage.
- Multi-location indexed folders with GUI and tray-indexer background updates.
- Per-user tray indexer with optional Windows sign-in startup.
- NTFS startup catch-up through USN journal checkpoints, with snapshot fallback for unsupported or unsafe roots.
- Local filesystem strategy classification for NTFS, ReFS, FAT/exFAT, network, cloud-backed, and removable roots.
- Index management UI with status badges, health diagnostics, validation, compaction, and pause/resume controls.
- Resource controls for battery, CPU profile, and disk pauses.
- Out-of-process extractor host, reusable extractor host pool, extractor versioning, IFilter fallback, archive limits, and failed-file export.
- Spectre.Console CLI REPL with live search, indexed search, index maintenance, filter commands, one-shot search export, one-shot index reporting, and one-shot index build/rebuild/clear actions to CSV/JSON/JSON Lines/Markdown.
- Programmatic Core API examples and PowerShell CLI automation docs.
- GitHub Actions CI foundation for build, test, formatting, CLI smoke, published sidecar smoke, dependency review, and manual Store packaging.
- Security, privacy, and release checklist documentation.

Still missing or partial:

- No benchmark/performance harness for live scan, index build, or indexed query latency.
- Retrying failed index files from the CLI remains future work.
- Saved searches, custom scopes, and shareable workspace import/export exist; workspaces can optionally run their saved search when loaded.
- Results now have GUI facets, indexed/live source route filtering, grouping, sort presets, direct CSV/JSON/JSON Lines/Markdown export, drag-and-drop, persistent favorites, shared pinned paths, safe result rename/delete, and configurable main-window keyboard shortcuts; richer reporting remains missing.
- Durable USN replay is NTFS-only. ReFS remains on snapshot validation until 128-bit file identifiers are supported and tested.
- Hard-link-aware path identity is not fully implemented; ambiguous USN deletes fall back to root validation.
- There is no true Windows Service yet. Background indexing is a per-user tray process that starts after user sign-in.
- Accessibility, localization, release signing, and benchmark maturity are still partial.

## Roadmap

### 1. Engineering and Release Maturity

- Extend GitHub Actions from the current CI/package foundation into a full signed-release pipeline.
- Add performance benchmarks for cold live scan, warm live scan, index build, incremental index refresh, indexed query, and CLI startup/query time.
- Add deterministic benchmark corpora under test assets, including plain text, Office/PDF samples, many tiny files, and a few large files.
- Add signed release validation around the existing MSIX packaging script.
- Keep the current live scan path as the correctness baseline for indexed search.

### 2. Indexing Platform V2

- Keep CSharpDB as the embedded index store for the next phase.
- Expand **Index Health** with richer trend/history data, machine-specific journal diagnostics, and clearer remediation actions.
- Add remaining maintenance commands for rebuild all indexes, clear all indexes, and retry failed files.
- Improve stale-index handling with clearer status messages and per-location warnings.
- Add default excluded folders for large noisy trees: `.git`, `.vs`, `bin`, `obj`, `node_modules`, package caches, and build outputs.
- Add optional **Search all indexed locations** mode separate from folder-specific search.
- Add ranking and snippets for indexed results while still rechecking every hit with the existing query engine.
- Add ReFS-safe durable replay with 128-bit file identifiers, `ExtendedFileIdType`, and native integration tests.
- Add explicit hard-link policy or path-level identity storage for USN delete/rename replay.
- Consider a true non-UI Windows Service only as a later, separate component for startup-before-login and deeper enterprise scenarios.

### 3. Search Workflow and Result UX

- Workspace bundles now store name, search settings, custom scopes, result view state, selected Quick Search indexed roots, pinned/favorite result sets, and optional run-on-load behavior, and can be imported/exported as shareable JSON files.
- Extend result facets beyond the current extension, folder, modified date, indexed/live source route, and file size facets when users need additional indexed metadata.
- Extend sort/group presets beyond the current relevance, recency, filename, hit count, folder, file type, and modified date controls when users need more advanced path and size views.
- Add richer GUI reports and report-oriented rollups beyond the current GUI and CLI CSV, JSON, JSON Lines, and Markdown export paths.
- Add richer preview controls for context size and copy/export of selected result snippets.

### 4. CLI, PowerShell, and API Surface

- One-shot CLI output now covers search results, `filesearch index build [FOLDER]`, `filesearch index rebuild [FOLDER]`, `filesearch index clear [FOLDER]`, `filesearch index locations`, `filesearch index stats [FOLDER]`, and `filesearch index failures`.
- Add remaining failed-file retry command once the core index exposes a targeted retry API:
  - `filesearch index retry-failures`
- Document and test stable DTO contracts for automation output: search hit, search summary, index location, index stats, and index error.
- Keep PowerShell usage CLI-driven for now.
- Defer a dedicated PowerShell module until failed-file retry and DTO contracts are finalized.
- If a PowerShell module is added later, it should wrap the CLI/API with cmdlets such as `Search-FileContent`, `Update-FileSearchIndex`, `Get-FileSearchIndexLocation`, and `Clear-FileSearchIndex`.

### 5. Product Maturity

- Add an accessibility pass for keyboard navigation, focus order, labels, contrast, screen reader names, and high-contrast behavior.
- Move user-facing strings toward resource files so localization is possible later.
- Add local-only privacy documentation that explains where settings live, where indexes live, what content is stored, and that no telemetry is collected unless explicitly added later.
- Add extractor safety guidance and temp-file cleanup rules.
- Add dependency review/SBOM generation in the release workflow.
- Keep implementation MIT-compatible and avoid borrowing code from GPL/EPL competitors.

## Test Plan

- CI verifies `dotnet build .\FileSearch.slnx` and `dotnet test .\FileSearch.slnx`.
- CI publishes the GUI, tray indexer, and extractor host together, then runs a sidecar smoke test that launches the indexer, pings IPC, reads status, pauses, resumes, requests shutdown, and verifies exit.
- Windows-only native smoke tests cover NTFS USN startup replay and safe fallback behavior.
- Add benchmark tests with tracked baselines for live and indexed paths.
- Add Core tests for search-all-indexed-locations, default excluded folders, rebuild-all/clear/retry maintenance commands, ReFS replay support when implemented, hard-link policy, and ranked/snippet results matching live-search correctness.
- Add GUI tests for saved search persistence, richer index health remediation, facet selection, and export command availability.
- Add CLI tests for one-shot search, one-shot index build/rebuild/clear/locations/stats/failures, JSON output shape, CSV escaping, exit codes, and index stats/build/clear commands.
- Add accessibility smoke checks for key windows and dialogs.

## Assumptions

- FileSearch remains Windows-first for the GUI.
- `FileSearch.Core` and `FileSearch.Cli` should stay cross-platform where practical.
- CSharpDB remains the embedded database for the next roadmap phase.
- Live scan remains the correctness fallback.
- Indexing remains opt-in by location.
- Watchers run while an indexing owner is running: either the GUI process or the per-user tray indexer.
- PowerShell support should initially use CLI automation, not a separate module.
