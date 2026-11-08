# WPF result delivery probe

This Windows-only probe opens the real MainWindow and QuickSearchWindow offscreen,
with synthetic indexed hits and fake services. It neither reads a volume index nor
starts indexing. Run it with no other builds, tests, or probes running.

```powershell
dotnet build benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release
dotnet benchmarks/FileSearch.UiPerfProbe/bin/Release/net10.0-windows10.0.19041.0/UiPerfProbe.dll C:/temp/ui-perf.json 500,2000,10000 default 20 4 --verify
```

Arguments are output JSON path, comma-separated file counts, comma-separated modes
(`default`, `flat`, `folder`), folder count, repeats, and optional `--verify`.
Each file has three hits. Repeat zero is a warmup. Use folder counts 1, 20, and
10000 to exercise one large group, typical groups, and many small groups.
Use `quick` instead of file counts to run only the four Quick Search samples.

The probe records time from starting search until dispatcher idle/layout, actual
realized ListBoxItems, allocations, query-property updates, refinement refresh and
clear times, and a 16 ms Input-priority dispatcher heartbeat (p95 and maximum gaps).
Refinement timings exclude the fixed 200 ms debounce. Heartbeat gaps are a proxy
for input responsiveness, not a physical keyboard or screen presentation test.
Quick Search records indexed-backend completion to settled display for 80 matches.

`--verify` additionally checks bounded realization before and after scrolling,
group collapse across recycling and filtering, visible automation names, and
selection binding. Missing metadata deliberately uses nonexistent synthetic paths;
all main-window hits include size and date. Filesystem metadata and stale-query
cancellation are covered by the GUI tests. Raw timings are machine-specific and
are not deterministic unit-test thresholds.

## Real-file preview baseline

After running the benchmark project's `tasks` command, pass its manifest here:

```powershell
dotnet build benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release
dotnet benchmarks/FileSearch.UiPerfProbe/bin/Release/net10.0-windows10.0.19041.0/UiPerfProbe.dll C:/temp/preview-baseline.json --task-preview-baseline C:/path/to/run/task-manifest.json 20 --include-ocr
```

Run sequentially with no other builds/tests/probes. The real shared preview control
loads text, code, PDF, spreadsheet, EML, both database engines, and a generated scan
in an offscreen window. It verifies corpus hashes and successful viewer states,
records first-invocation/warm readiness and 16 ms dispatcher heartbeat samples, and
saves JSON plus a screenshot. Database timing includes snapshot, schema, first
page, and grid layout; it differs from the core runner's component times. Readiness
polling adds up to a 10 ms delay plus dispatcher/layout work. This is not a complete
main/Quick Search user task, physical input measurement, or presentation time.

`--include-ocr` additionally measures live/indexed search of the generated English
scan using an existing Windows OCR language pack and verifies its OCR region.
It saves a separate `.ocr.json`; no recognizer is reported as unavailable, with no
download or configuration change. An available recognizer that fails the task
fails the probe. Ordinary preview timing does not require OCR. Raw first samples
are retained; exclude iteration zero when calculating warm percentiles. A small
sample count gives coarse P95/P99 estimates.

## Database navigation baseline

```powershell
dotnet build benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release --artifacts-path artifacts/database-navigation-build
dotnet artifacts/database-navigation-build/bin/UiPerfProbe/release/UiPerfProbe.dll artifacts/database-navigation.json --database-navigation-baseline C:/path/to/task-manifest.json 10
```

Use the same generated manifest and identical probe on separately built before/after
product sources. Run sequentially without other builds, tests, or probes. The
offscreen shared database viewer opens each engine, clicks Next twice, jumps to the
last record, clicks Previous, returns to row one, and initializes the same source at
row 750. SQLite/CSharpDB order alternates across rounds. The manifest must contain
at least 750 rows. Source/main/WAL/sidecar hashes and selected record keys, values,
row numbers, table, column and fingerprint are checked.

JSON and JSONL retain each operation's readiness, success/failure, 16 ms dispatcher
heartbeat, cumulative process allocations and sampled working set. Iteration zero
is retained separately from the default ten warm repeats; it is not OS-cold. The
10 ms polling/idle-layout method and small sample count give coarse tail estimates.
These are shared-viewer diagnostics, not physical-input, presentation-time,
main/Quick task or release performance qualification. Keep mixed results and UI
stalls visible. See the [benchmark results summary](../../README.md#benchmark-results).

Add `--database-phase-details` after the repeat count for an attribution run.
This attaches a per-control internal hook and records query/mapping, UI transfer,
dispatcher wait, explicit layout/selection, and resize-visibility work, plus
realized row/cell and scroll counters. Nested totals overlap and must not be
added. The complete readiness sample also includes uninstrumented automatic WPF
layout and the probe's idle-layout check. Ordinary previews and headline runs do
not attach this hook or collect phase clocks. Run separate uninstrumented samples
for before/after conclusions.

## Result refinement smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj --artifacts-path artifacts/refinement-smoke-build -- artifacts/refinement-smoke.json --refinement-smoke
```

This renders the real main window using temporary text fixtures and verifies the
result filter's label/help, simultaneous original/refinement highlighting, late
matching excerpts, Matches-pane highlighting, folder-group and card-path matches,
and restoration after clearing. It saves a JSON result and a PNG beside it. The
fixture files are retained under the output directory for inspection.

## Query and coverage guidance smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj --artifacts-path artifacts/guidance-smoke-build -- artifacts/guidance-smoke.json --guidance-smoke
```

This opens the real main window offscreen with fresh fake settings/shell services
and generated text/SQLite fixtures. It checks live AND/OR/phrase/literal
explanations, all three starting choices without query rewriting, a filename and
database-cell search, missing-index live fallback, covered indexed results, and
AND staying within a single database cell. Source hashes must remain unchanged.
Binding/accessibility-name checks and screenshots cover wide and minimum-width
windows, including search/options buttons and starting-choice reachability. JSON
and PNGs are retained beside the output. OCR, models and PowerToys are unnecessary.
This is a scripted functional/layout check, not physical-input or timing evidence.

## Database preview Find smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj --artifacts-path artifacts/database-find-smoke-build -- artifacts/database-find-smoke.json --database-find-smoke
```

This uses real shared main, Quick Search and expanded previews with generated
800-row SQLite/CSharpDB fixtures and fake settings/shell services. It finds cells
on rows 750/800 from page one, verifies selected row/key/column/fingerprint and
visible highlights, and checks matching modes, column scope, case, empty tables,
stale inputs, Stop/restart and source changes. A generated source timestamp is
temporarily changed/restored for the stale-source check; all source byte hashes
must remain unchanged. OCR, models and PowerToys are unnecessary.

It tests opening/closing Find options, retained Expand state, and Quick Search at
760/640 widths. Database previews reserve at least 300px of preview height and a
readable records viewport; their window minimum includes the measured header and
chooser. Find raises the window minimum to at least 540px and reserves additional
space when its controls require it. Hiding Find returns to the database minimum;
hiding an automatically grown database preview restores the prior compact height
unless the user resized it. Text/PDF keep their ordinary 420px compact window and
540px Find minimum. JSON and screenshots record actual dimensions. These
are functional/layout checks, not physical-input or performance measurements.
Failed cell assertions retain host screenshots and JSON viewport/selection
diagnostics. Earlier failed artifacts should be kept when fixes are investigated.

For an expanded-preview load timeout, add `--trace-expanded-database-load` to the
same command. This opt-in writes `<output>-sqlite-expanded-load-trace.json` and
`<output>-csdb-expanded-load-trace.json` beside the ordinary output, including on
failure. It attaches before the first expanded-window settle, retains controls'
status and cancellation history after fallback disposes them, and records source
byte hashes, dispatcher heartbeat gaps, and the existing page/Find positioning
phase hooks. Preparation status distinguishes snapshot/engine/schema work;
`page.query`, `page.ui-resume-wait`, `page.dispatch-wait` and `keep.dispatch-wait`
separate record reading, UI resumption, page positioning and restored Find
positioning. Unobserved phases are listed explicitly. These clocks overlap and
diagnostic overhead affects timing; they do not change the app's deadlines or
normal behavior. Preserve both successful and failed traces for comparison.

It also checks same-file hit changes through the outer FileDocumentPreview,
unchanged session/snapshot reuse, hide/reopen cleanup, replacement by another
source, and final window-close cleanup on both engines.

## Document preview Find smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release --artifacts-path artifacts/document-find-build -- artifacts/document-find-smoke.json --document-find-smoke
```

This uses real main, Quick Search and expanded previews with generated text,
Markdown and mail fixtures and fake settings/shell services. It checks literal
phrases, Unicode, case, late-hit selection, next/previous wrapping, query/source
invalidation, Stop, 10,000 retained text occurrences and truncated text/HTML
coverage. Expand retains query/case/occurrence, including regenerated embedded
mail image URLs. Quick Find is exercised at width 640 and height 540; Escape
restores its prior compact height of 420. All source byte hashes must remain
unchanged. Models, OCR and PowerToys are unnecessary.

Native HTML checks retain disabled scripts/host objects, block remote navigation
and reject unapproved preview resources. WPF PNGs capture controls; separate
native WebView2 PNGs capture the rendered document and Find highlights. JSON,
fixtures, failure diagnostics and screenshots remain alongside the output.
Shortcut routing is scripted; this does not qualify physical WebView2 input,
screen readers, OS high-contrast switching, performance or PDF text geometry.

## PDF preview Find smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release --artifacts-path artifacts/nw06-build -- artifacts/pdf-find-smoke.json --pdf-find-smoke
```

This renders generated PDFs in real main, Quick Search and expanded previews.
It checks current-page embedded-text Find, literal phrases, case, wrapping,
page/source races, all four page rotations with nonzero crop origins, and
exclusion of cropped-out text. Expand retains an active or closed Find, page,
Fit mode, manual zoom and normalized viewport center. Existing synthetic OCR
line regions require matching source metadata, page and bitmap dimensions;
stale regions are rejected without starting OCR. Quick Find is exercised at
640 by 540 and Escape restores its prior 420 height.

The source-change check changes and restores a fixture's modification timestamp;
source bytes must remain unchanged. JSON, screenshots and earlier failures are
retained beside the output. Separate GUI tests compare native glyph polygons to
actual Windows PDF raster ink across crop/page/text rotations. These are scripted
functional and geometry checks with generated fixtures and fake shell/settings
services, not physical-input, real OCR recognition, screen-reader, OS high-contrast
switching, large-document performance or unrestricted PDF-format qualification.

## Find and replace smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj --artifacts-path artifacts/replacement-smoke-build -- artifacts/replacement-smoke.json --replace-smoke
```

This mode renders the real MainWindow offscreen using temporary text files and the
replacement service. It verifies panel opening, query bindings, Enter preview,
realized preview rows, Apply and Undo buttons, and restoration of normal search.
It writes a JSON result and a PNG beside it, then removes its temporary files.

Use `artifacts/workflow-replacement-smoke.json --workflow-replace-smoke` with the same command to exercise the real Workflows editor and modal replacement review. This mode checks dry run, checkboxes, Enter handling, successive name/content replacement steps, and grouped Undo. It also saves editor and review screenshots.

## Multi-location scope and handoff smoke check

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release --artifacts-path artifacts/nw06-build -- artifacts/location-scope-smoke.json --location-scope-smoke
```

This uses real main-window, Quick Search and Settings controls with generated
text, SQLite and ZIP fixtures and fake settings/shell services. Its 16 checks
cover shared indexed-location checkboxes, selected/all scopes, explicit empty
selection, removed and unavailable locations, overlapping-root deduplication,
and Quick Search handoff with its names/path matching and literal content phrase
preserved. Saved searches and workspaces retain the scope and matching profile;
database-cell and archive-member locations survive handoff. The standard-matching
action and unsupported entire-machine handoff explanation are also exercised.

Layout checks keep scope controls and a selected preview reachable at minimum
window sizes. Clearing all indexed locations stays empty after refreshing Settings.
Source byte hashes must remain unchanged; JSON, fixture files, screenshots and
failure diagnostics remain beside the output. These are scripted functional/layout
checks, not physical keyboard or mouse, screen-reader, OS high-contrast, real
network/offline recovery, performance or installed-product qualification. Models,
OCR and PowerToys are unnecessary.

## Quick Search ranking baseline

```powershell
dotnet run --project benchmarks/FileSearch.UiPerfProbe/UiPerfProbe.csproj -c Release --artifacts-path artifacts/nw06-build -- artifacts/quick-ranking-before.json --quick-ranking-baseline
```

The frozen `quick-ranking-v1` corpus declares 24 tasks before ranking changes:
12 calibration tasks and 12 held-out tasks. Do not tune against the held-out
results. It covers late exact-name candidates, deep exact versus prefix matches,
own-name versus folder-word matches, deterministic ties, content arriving first,
mixed covered/live locations, an exact candidate arriving after 80 content files,
punctuation, case, Unicode and literal operators/path separators. Acronym and
typo cases expect strict misses; adding approximate matching is not scored as a
ranking improvement.

This replays explicitly scripted **fake** `IVolumeNameIndex` candidate scores/order
and `ISearcher` content schedules through the actual Quick Search view model and
an offscreen WPF results list. The two mixed-location tasks also exercise the
actual filesystem metadata fallback over generated files. It does not measure
native volume-provider ranking; use the separate Core corpus for that layer.

JSON retains all declared inputs, expected paths, final order, Top 1/Top 5/MRR,
selection/order timelines, cancellation, completion, every error/timeout and
first-useful-result latency. Target-visible means an intersecting realized WPF
item container in the offscreen viewport. Missing targets/visibility remain
explicit failures, and strict misses have separate counts. Timings include query
debounce and harness sampling/render work. Generated fixtures, SHA-256 source and
binary fingerprints, before/after source checks and per-case checkpoint JSON
remain beside the output. Fixture bytes must remain unchanged.

Compare before/after runs with the same frozen corpus and provider schedules.
These local synthetic-provider diagnostics do not qualify installed-product
performance, physical input, networking, whole-drive indexes, cold OS caches or
real-world relevance. No file launch or usage-history learning is exercised.
