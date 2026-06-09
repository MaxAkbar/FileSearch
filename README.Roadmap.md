# FileSearch Competitive Roadmap Refresh

## Summary

FileSearch has already closed two major gaps from the older competitive analysis: optional CSharpDB-backed indexing and a Spectre.Console REPL CLI now exist. The remaining work should focus on turning FileSearch from a capable desktop scanner into a polished, daily-driver local search platform with reliable releases, measurable performance, richer indexed-search UX, better automation output, saved workflows, and clear product maturity around accessibility, privacy, and security.

## Current State

Completed since the older roadmap:

- Hybrid live/indexed search with CSharpDB-backed extracted line storage.
- Multi-location indexed folders with background watcher updates.
- Index management UI with status badges and pause/resume controls.
- Spectre.Console CLI REPL with live search, indexed search, index maintenance, and filter commands.
- Programmatic Core API examples and PowerShell CLI automation docs.
- GitHub Actions CI foundation for build, test, CLI smoke, GUI publish smoke, dependency review, and manual Store packaging.
- Security, privacy, and release checklist documentation.

Still missing or partial:

- No benchmark/performance harness for live scan, index build, or indexed query latency.
- CLI is interactive-first; it lacks first-class one-shot commands and JSON/CSV output.
- Saved scopes exist, but full saved searches/profiles do not.
- Results lack facets, grouping, advanced sort presets, and export/reporting.
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
- Add an **Index Health** view showing indexed roots, file count, line count, index size, queue depth, last refresh, failed/skipped files, and extractor error counts.
- Add maintenance commands for compact/vacuum database, rebuild all indexes, clear all indexes, and retry failed files.
- Improve stale-index handling with clearer status messages and per-location warnings.
- Add default excluded folders for large noisy trees: `.git`, `.vs`, `bin`, `obj`, `node_modules`, package caches, and build outputs.
- Add optional **Search all indexed locations** mode separate from folder-specific search.
- Add ranking and snippets for indexed results while still rechecking every hit with the existing query engine.

### 3. Search Workflow and Result UX

- Promote current custom scopes into full saved searches.
- Saved searches should store name, root or indexed-location set, query text, query mode, case sensitivity, recursion, glob filters, extension filters, size/date filters, and index preference.
- Add result facets for extension, folder, modified date bucket, file size bucket, and indexed/live source.
- Add sort/group presets for relevance, path, modified date, extension, file size, and line count/hit count.
- Add export commands from GUI and CLI for CSV, JSON, and Markdown reports.
- Add richer preview controls for context size and copy/export of selected result snippets.

### 4. CLI, PowerShell, and API Surface

- Add one-shot CLI commands alongside the REPL:
  - `filesearch search "query" --path C:\src --mode boolean --json`
  - `filesearch index build C:\src`
  - `filesearch index locations --json`
  - `filesearch index stats C:\src --json`
- Add table, JSON, JSON Lines, and CSV output formats.
- Add stable DTOs for automation output: search hit, search summary, index location, index stats, and index error.
- Keep PowerShell usage CLI-driven for now.
- Defer a dedicated PowerShell module until JSON/CSV one-shot CLI support exists.
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
- Add benchmark tests with tracked baselines for live and indexed paths.
- Add Core tests for search-all-indexed-locations, default excluded folders, index health stats, compact/rebuild/clear maintenance commands, and ranked/snippet results matching live-search correctness.
- Add GUI tests for saved search persistence, index health display, facet selection, and export command availability.
- Add CLI tests for one-shot search, JSON output shape, CSV escaping, exit codes, and index stats/build/clear commands.
- Add accessibility smoke checks for key windows and dialogs.

## Assumptions

- FileSearch remains Windows-first for the GUI.
- `FileSearch.Core` and `FileSearch.Cli` should stay cross-platform where practical.
- CSharpDB remains the embedded database for the next roadmap phase.
- Live scan remains the correctness fallback.
- Indexing remains opt-in by location.
- Watchers continue to run only while the app is open.
- PowerShell support should initially use CLI automation, not a separate module.
