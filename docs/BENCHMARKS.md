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
| Indexed query phase breakdown | Attributes fixed query overhead (DB open, metadata lookup, trigram lookup, row fetch, recheck) so optimization targets the right phase |
| Indexed regex query latency | Measures required-literal trigram narrowing and the indexed line-row scan fallback for patterns that cannot be bounded |
| Indexed substring query latency + parity | Mid-token substrings stress trigram candidate narrowing; parity compares indexed vs live hit coverage for the same query |
| Live content scan latency/throughput | The no-index correctness baseline; MB/s comparable to grep-class tools |
| Time to first result | Usually matters more than total completion time |
| Initial index throughput | Determines onboarding quality |
| Incremental catch-up throughput | Measures re-indexing existing changed files, including indexed cleanup of superseded rows |
| Incremental delete throughput | Measures tombstone publication plus indexed cleanup of each deleted file's owned rows |
| Index freshness after an event | Watcher event to first indexed hit, including debounce and queue dispatch |
| Memory per million files | Determines whether users leave the indexer running |
| Index disk size | Affects adoption and storage trust |
| Search relevance | Speed does not help if the right file is buried |
| Semantic exact-vector P50/P95/P99 | Decides whether exhaustive int8 scoring remains viable or HNSW is justified |
| Semantic int8/float top-10 overlap | Prevents storage compression from silently degrading ranking quality |
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
| Exact semantic vector scan at 200,000 chunks P95 | under 250 ms; otherwise evaluate HNSW |
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

Semantic exact-search/HNSW decision gate:

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- semantic --documents 200000 --queries 20
```

The command returns exit code `0` when exact search meets the 250 ms P95 component budget and `3` when the HNSW evaluation gate is crossed. It uses deterministic 384-dimension int8 vectors, exact top-50 scoring, three warmups, and reports per-query allocation.

Real ONNX batch smoke (downloads the selected catalog model when `--install-model` is present):

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- semantic-model --model-id all-minilm-l6-v2-onnx --model-directory "$env:TEMP\filesearch-semantic-model-smoke" --install-model
```

The release smoke compares eight single embeddings with one token-budgeted batch and fails unless the model executes a batch larger than one with minimum cosine similarity of 0.9999. The current MiniLM smoke executed batch size 8 without fallback and produced cosine 1.000000.

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

`incremental_catch_up_throughput` seeds files before timing, changes their contents, and times replacement upserts rather than fresh inserts. `incremental_delete_throughput` then times removal of those indexed files. Together they exercise CSharpDB 4.0.2's index-planned mutation path and FileSearch's transactional per-file cleanup.

The `indexed_query_phase_*` metrics come from an internal timing hook on `CSharpDbFileIndex.SearchAsync` that is only attached while the instrumented iterations run; the headline latency percentiles are measured without it. The `_other` phase is the remainder between the whole call and the named phases.

`index_freshness_after_event` starts the real `IndexingService`, `IndexQueue`, and `IndexWatcherService`, watches the content root, writes a marker file, and polls indexed search until the marker is visible. It measures steady-state freshness (watcher active, index current) and includes the watcher debounce window; it excludes startup catch-up.

`substring_index_live_parity` runs the same mid-token substring query through the indexed path and the live scanner and compares distinct matched files. 100 means the indexed path found every file the live baseline found; lower values quantify indexed substring misses.

`memory_per_million_files` is only reported when the index holds at least 100,000 files; below that, normalizing whole-process working set per million files amplifies fixed process overhead into a meaningless number. `benchmark_process_working_set` is always reported, raw.

Scan-bound measurements (regex, substring, live scan) run `clamp(QueryIterations / 10, 3, 10)` iterations so the standard and full profiles stay tractable.

The semantic quantization regression compares exact int8 results with the float32 baseline. Each deterministic query must retain at least 9 of the float top 10 and keep common-result score error at or below 0.01. The 200,000-vector local gate measured 88.3 ms P95 and about 12 KB allocated per query after removing per-candidate score-object allocation, so HNSW is not currently selected.
