# FileSearch Benchmark Methodology

FileSearch benchmarks are reproducible internal measurements. They are not competitor claims. Do not claim "faster than Everything", "best search quality", or similar until a published benchmark compares the same corpus, hardware, settings, and query set against those products.

## Corpus Profiles

| Profile | Purpose | Scale |
| --- | --- | --- |
| `smoke` | Fast development check | 10,000 metadata rows and hundreds of physical files |
| `standard` | Routine local performance run | 100,000 metadata rows and thousands of physical files |
| `full` | Release/performance qualification | 1,000,000 metadata rows and hundreds of thousands of physical files |

The deterministic corpus covers:

| Area | Coverage |
| --- | --- |
| Metadata-only entries | Direct-seeded file rows and metadata tokens in CSharpDB |
| Small text/source files | Real files across many directories and extensions |
| Office/PDF | Generated `.docx`, `.xlsx`, and `.pdf` fixtures |
| Large logs | Deterministic log files with repeated markers |
| Archives | ZIP files with text members |
| Unicode and long paths | Unicode filenames and deeper path segments |
| Stopped-indexer changes | Files created after the initial index and recovered by restart refresh |
| Network/removable/cloud roots | Optional probes via environment variables |

## Metrics

| Metric | Why it matters |
| --- | --- |
| Metadata query P50/P95/P99 | Determines whether filename/path search feels instant |
| Indexed content query latency | Measures the core content-search path |
| Indexed query phase breakdown | Attributes fixed query overhead (DB open, FTS lookup, row fetch, recheck) so optimization targets the right phase |
| Indexed regex query latency | Regexes cannot use FTS candidates; measures the full lines-table scan fallback |
| Indexed substring query latency + parity | Mid-token substrings stress FTS token matching; parity compares indexed vs live hit coverage for the same query |
| Live content scan latency/throughput | The no-index correctness baseline; MB/s comparable to grep-class tools |
| Time to first result | Usually matters more than total completion time |
| Initial index throughput | Determines onboarding quality |
| Incremental catch-up throughput | Validates changed-file processing cost |
| Index freshness after an event | Watcher event to first indexed hit, including debounce and queue dispatch |
| Memory per million files | Determines whether users leave the indexer running |
| Index disk size | Affects adoption and storage trust |
| Search relevance | Speed does not help if the right file is buried |
| Crash/restart correctness | Establishes trust in durable indexing |
| Extraction success rate | Shows practical document-format coverage |

Relevance reports include MRR, NDCG@10, Recall@20, zero-result rate, and top-result accuracy.

## Initial Targets

These are internal targets, not product promises:

| Target | Goal |
| --- | ---: |
| Warm metadata search P95 | under 50 ms |
| Warm lexical/content search P95 | under 150 ms |
| First semantic/content results | under 500 ms |
| Search UI keystroke response | under 16 ms |
| Index freshness after an event | under 2 seconds |
| No lost changes after restart | 100% in the recovery corpus |

## Running

Fast smoke report:

```powershell
dotnet run --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- report --profile smoke --force-index
```

Full local report:

```powershell
dotnet run --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- report --profile full --force-index
```

BenchmarkDotNet search microbenchmarks:

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- bench --filter *
```

Reports are written to `artifacts/benchmarks/<profile>/reports/benchmark-report.md` and `.json`.

Optional external-root inputs:

```powershell
$env:FILESEARCH_BENCHMARK_NETWORK_ROOT='\\server\share'
$env:FILESEARCH_BENCHMARK_REMOVABLE_ROOT='E:\'
$env:FILESEARCH_BENCHMARK_CLOUD_ROOT="$env:OneDrive"
```

## Notes

The metadata corpus is direct-seeded so the full profile can cover one million indexed entries without creating one million real files. Physical corpus files exercise the extractor and content index paths.

The stopped-indexer recovery corpus currently validates restart correctness through a fresh refresh against deterministic changed files. Native USN catch-up qualification should be covered by a separate Windows-only integration suite on NTFS volumes.

The `indexed_query_phase_*` metrics come from an internal timing hook on `CSharpDbFileIndex.SearchAsync` that is only attached while the instrumented iterations run; the headline latency percentiles are measured without it. The `_other` phase is the remainder between the whole call and the named phases.

`index_freshness_after_event` starts the real `IndexingService`, `IndexQueue`, and `IndexWatcherService`, watches the content root, writes a marker file, and polls indexed search until the marker is visible. It measures steady-state freshness (watcher active, index current) and includes the watcher debounce window; it excludes startup catch-up.

`substring_index_live_parity` runs the same mid-token substring query through the indexed path and the live scanner and compares distinct matched files. 100 means the indexed path found every file the live baseline found; lower values quantify indexed substring misses.

`memory_per_million_files` is only reported when the index holds at least 100,000 files; below that, normalizing whole-process working set per million files amplifies fixed process overhead into a meaningless number. `benchmark_process_working_set` is always reported, raw.

Scan-bound measurements (regex, substring, live scan) run `clamp(QueryIterations / 10, 3, 10)` iterations so the standard and full profiles stay tractable.
