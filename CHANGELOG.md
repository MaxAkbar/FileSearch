# FileSearch changelog

Work landed from `bbd4b73` (*Add background indexed search*, June 2026) to the current head, grouped by area.

## FileSearch 1.11.3

- **Preview reliability**: publish database readiness only after the requested cell is selected, and show a diagnostic Details/Open fallback when the Windows WebP decoder is unavailable or cannot open the file. Cancellation cleanup tests now wait for an active child within the normal host deadline. Earlier 1.11 validation candidates were not published.
- **Index lifecycle reliability**: finish an idle index's database cleanup before returning from disposal, so immediately reopening the index cannot race its WAL cleanup. Active readers and queued writes finish before deferred cleanup closes their database and gates.
- **Release validation**: GUI tests wait for dispatcher shutdown and thread exit on success, failure, timeout, or cancellation.

- **Consistent search locations**: main search and Quick Search share selected indexed-location checkboxes, support explicit selected/all-indexed scopes, and explain empty, offline, and removed locations. Open full search preserves Quick Search query matching and source selection; saved searches and workspaces retain scope and matching intent. See [search location Help](src/FileSearch.Gui/Help/search.html#search-locations).
- **Find in file previews**: highlight and navigate literal phrases in loaded text/source, formatted HTML, and embedded text on the displayed PDF page across the main window, Quick Search, and Expand. Find shows its loaded-part/page scope and partial limits; unavailable text or browser Find leaves the ordinary preview available. PDF highlights follow supported page crop/rotation, validated existing OCR hits can show a separate line outline, and Expand retains Fit/custom zoom and position. See [preview Find Help](src/FileSearch.Gui/Help/results.html#preview-find).
- **Database loading and navigation**: preparation stages and snapshot copy progress, Stop loading, reuse of the visible session and grid for another hit in the same unchanged database, and bounded continuation bookmarks for supported sequential pages. Ordinary page changes update existing rows where possible; sorting and record locations remain distinct. Arbitrary deep jumps retain a correct offset fallback; read-only snapshot and size/row limits remain in effect.
- **Find in database previews**: search the selected SQLite or CSharpDB table beyond the loaded page, choose all/either literal terms or a phrase and a column scope, then navigate matching cells. Scans are cancellable and bounded, with explicit partial coverage; the database stays read-only. See [database Find and limits](README.Databases.md#find-in-a-table).
- **Search guidance**: live mode/target explanations, starting setups for files/content/database values, and persistent per-target routing details, including zero-hit fallback reasons. Plain text, AND/OR/phrases, filename filters and database cell scope are explained in bundled Help.
- **Built-in file previews**: switch between Matches and File preview in the main window or Quick Search. Preview images and SVG, navigate PDF pages, read formatted Markdown, and view syntax-highlighted source code, including filename-only results. Expand the same preview into a larger window. Installed Windows preview handlers extend format coverage in an isolated helper process, with file details or text as the fallback. PowerToys is not required.
- **Whole-page PDF context**: PDF file previews show the complete original page and start at the current match's page when available, including partial snippets and OCR-region hits. Expanded previews retain the current page; Matches remains an excerpt view, and text fallbacks are labeled separately.
- **Database search and preview**: search SQLite and CSharpDB scalar values with `type:database`, retain table/row/column/key locations in live and indexed results, and browse complete supported records in a read-only grid. Private database/WAL snapshots leave source files unchanged and reject stale hit locations. Refresh existing indexes to add database contents. See [coverage and limits](README.Databases.md).
- **More built-in viewers**: read CSV/TSV, XLSX, and ODS in a paged grid with sheet selection and matching-cell navigation; browse actual ZIP members; read complete EML/MSG and selected PST/OST messages with attachment names; preview WebP, animated GIF, and multipage TIFF; and read whole EPUB chapters plus reflowed ODT/ODP/RTF content without Office. Expand preserves the selected part or cell. Formulas and active/remote document content are disabled. Refresh old indexes for precise XLSX and ZIP locations. See [File preview Help](src/FileSearch.Gui/Help/results.html) for format limits and fallback behavior. PowerToys remains optional.
- **Offline Help search**: search the bundled guide by keyword or quoted phrase, view matching passages, and open the relevant section with highlighted matches. Press Ctrl+K from any Help page. The search index is generated automatically when building or publishing the GUI; no web service or additional application runtime is required.
- **Structured mail queries**: use Unified `from:`, `to:`, `cc:`, `recipient:`, `subject:`, and `sent:` fields to match one message's headers and sent date. Refresh older mail content indexes to populate the new metadata. See [Mail search](README.Mail.md).
- **PDF and Office text inside ZIP files**: search native text in PDF, DOCX, XLSX, and PPTX members, retain member/page/sheet/cell locations, and preview the matching supported part. Bounded extraction reports skipped or partial content; nested archives, attachment contents, and new document-image OCR are outside this release.
- **Quick Search ranking and selection**: prioritize exact names and complete name components, retain deliberate selection as results arrive, and preserve matching intent when opening full search. Literal matching remains unchanged; no automatic typo expansion or usage learning is added.
- **Dependency maintenance**: update CSharpDB to 4.7.0, WebView2, MCP, the optional PowerToys companion dependencies, and test tooling. WiX remains on 6.0.2.
- **Documentation and reproducible checks**: refresh the README with a real-app animated tour and focused CLI/developer guides; add benchmark runners and native preview regression probes. Local measurements retain mixed results and open qualification; no general performance or competitor claim is made.

## FileSearch 1.10.0

- **Optional PowerToys companion**: search indexed file names or document text from Command Palette, view snippets, open files, reveal them in Explorer, copy paths or matching text, and continue a query in FileSearch. The companion is installed separately; normal FileSearch startup, shortcuts, and dependencies do not require PowerToys. See the [PowerToys setup guide](README.PowerToys.md).
- **Desktop query handoff**: launch or activate the desktop app with a query, folder, target, and selected file. Active searches and replacements are preserved, and a missing folder never redirects the query to another location.
- **Bundled Help**: added the PowerToys guide, troubleshooting, and executable argument reference throughout the app's Help pages.
- **Release packaging**: separate companion MSIX, unsigned development ZIP when needed, and checksums, with automatic package verification and main-app dependency isolation checks.
- **Dependency maintenance**: updated the existing PKCS dependency to 10.0.13 to address its reported vulnerabilities.

## FileSearch 1.9.1

- **Find and replace**: preview and apply checked content or name changes from the main Search screen, with strict Unicode encoding preservation, native DOCX/XLSX/PPTX editing, collision checks, protected roots, durable backups, and Undo after restart.
- **Workflow replacement**: add Find and replace steps with fresh folder scopes or exact earlier-search scopes, before/after review, dry-run previews, and grouped Undo. CLI application requires `--apply-replacements`.
- **Semantic search**: desktop concept-search controls, relevance thresholds, result limits, and optional local EmbeddingGemma model support. See [Smart Search help](src/FileSearch.Gui/Help/smart-search.html).
- **Model-license packaging**: keep bundled license bytes stable across Windows clean checkouts so pinned model-pack checksums remain valid.

## Search engine and indexing

- **Background indexed search** (`bbd4b73`): CSharpDB-backed file index with background refresh, coverage checks, and live-scan fallback — the baseline for the changes below. Documented in [README.Indexing.md](README.Indexing.md).
- **Indexing correctness, threading, and performance overhaul** (`13b1f9c`): removed the watcher feedback loop that caused double indexing, skipped upserts for unchanged files, ran schema setup once per process, and serialized cross-process writers with a lock file. Heavy parsing and preview extraction moved off the UI thread, UTF-16/32 files are detected correctly, overlapping indexed roots are supported, and search-time filters no longer shrink index coverage. Performance work included directory-exclude pruning, cached per-file verdicts, batched UI updates, and throttled progress callbacks, plus centralized file logging and safer settings management.
- **Index storage layering** (`1b08535`): split the index implementation into `IndexDatabase` (schema, versioning, write exclusion) and `IndexTables` (DML), extracted a tested `SearchHistory` MRU helper, and moved folder picking and UI dispatch behind injectable services.
- **Worker and queue fixes** (`2317524`): queue priority, worker edge cases, and small races found in review.
- **Compiler-enforced SQL escaping** (`6778331`): index queries are built through an interpolated-string handler (`Sql.Format`), so a value that bypasses escaping no longer compiles.
- **Workflow search**: saved multi-step searches with if/else conditions on hit or file counts, retry and for-each loops, sub-searches over previous results, JSON/CSV/Markdown export, and confirmed replacement/copy/move/run-program actions — stored as hand-editable JSON files, one per workflow. The file format is documented in [README.Workflows.md](README.Workflows.md).

## Desktop app

- **Indexed locations management** (`0d94748`): dedicated window for per-location options (subfolders, hidden files, document extraction, change watching) with live status badges and queue counts in the sidebar.
- **Shutdown deadlock fix** (`5a1fa5c`): the app could hang forever on exit while stopping background indexing.
- **View-model split** (`41f59e7`): `MainViewModel` split into focused `Search`, `Index`, `History`, and `StatusBar` view models, backed by a binding-path audit test that verifies every XAML binding resolves.
- **Sidebar scrolling** (`13402db`): the collapsible sections (Scopes, Recent folders, Indexed locations, Saved searches) scroll as one surface when they outgrow the window instead of pushing the lower sections out of view, and the nav lists no longer swallow mouse-wheel events.

## Command line

- **Interactive search REPL** (`b9c2ec5`): Spectre.Console front end with live and indexed search, query-mode/case/filter/limit commands, target/document/OCR controls, workflow execution, background-indexer controls, and index build/clear/stats — sharing the same core pipeline and index database as the GUI.

## Extractors

- **Hardening pass** (`362b18e`): zip-bomb entry and size caps, bounded file reads, charset-correct EML decoding, and RTF/markup fixes.

## CI, tests, and release

- **CI and release foundation** (`a8e8965`): GitHub Actions build/test workflows, dependency review, Store packaging workflow, Dependabot, `SECURITY.md`, `PRIVACY.md`, the release checklist, and the competitive roadmap.
- **Coverage and pipeline hardening** (`71a616d`): new searcher, indexed-searcher, and indexing-service test suites and a stricter CI pipeline.

Return to the [main README](README.md).
