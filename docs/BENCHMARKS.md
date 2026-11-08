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

## Database preview phase attribution

The [database-only profiler](../benchmarks/FileSearch.Benchmarks/README.md#database-preview-phase-profile)
separates the actual preview-opening path without rerunning searches/indexing:

```powershell
dotnet run -c Release --project benchmarks/FileSearch.Benchmarks -- database-profile --resume C:/path/to/task-manifest.json --queries 100
```

It records six warmup pairs and an even number of measured pairs with equal
engine-first/second positions. Each session creates a fresh bounded private copy
of the same generated source. Raw samples, phase percentiles, per-position results,
matched-round differences and source/sidecar hashes are saved in a new
`artifacts/benchmarks/database-phases/<run>` directory. Source/index/user settings
are preserved. Run sequentially with no other builds/tests/probes.

Copy is a subset of Snapshot. EngineOpen, EngineSetup, Schema, Validation and
OpenOther are disjoint contributors to OpenTotal; OpenAndFirstPage is separately
timed. Component medians/percentiles do not add. Paired differences use complete
successful rounds, and failed/incomplete phases remain explicit. Different engine
APIs do different amounts of setup/catalog work during Open. These measurements
attribute preview cost, not general database performance or indexed-search speed.
See the [README benchmark summary](../README.md#benchmark-results).

The additional `SequentialDeepPage` phase primes the penultimate page outside its
timer, then measures continuation into the last page. It retains the original
first-page and arbitrary-deep-page measurements. Applicability is explicit, and
failed or inapplicable timings remain null. This isolates a supported sequential
continuation from a random row jump; its warm cache state differs from the earlier
deep read. Compare the same phase, source, and schedule across builds.

The [database navigation probe](../benchmarks/FileSearch.UiPerfProbe/README.md#database-navigation-baseline)
adds shared-viewer Next/Previous/Go and same-source-location operations, with
selected-record checks, source hashes, heartbeat, allocation and sampled working
set evidence. Its readiness combines query/mapping/grid/layout; it does not
independently time grid rendering.

Keep opt-in phase/stack diagnostics separate from uninstrumented grid comparisons.
Source and selected-record correctness must pass before using timings. New-preview opening,
page changes and same-source hit selection are separate operations; gains in one
do not establish gains in all three. Preserve different binary hashes and sample
counts, failed attempts, background-load observations and unchanged source hashes.

## Task baseline and comparison protocol

The `tasks` command extends the component suite with real generated files. Run a
Release build without other builds, tests, or performance probes:

```powershell
dotnet run -c Release --project benchmarks/FileSearch.Benchmarks -- tasks --profile standard --queries 20
```

The new run directory contains `task-manifest.json`, `task-baseline.json`, and
`task-baseline.md`. No direct-seeded rows are used. The manifest identifies every
file by SHA-256 and provides task queries, matching modes, and expected source
locations. Use the [WPF probe](../benchmarks/FileSearch.UiPerfProbe/README.md#real-file-preview-baseline)
against this same manifest to measure preview readiness and optional scan/OCR
retrieval. It records UI heartbeat gaps separately from engine query latency.

The first result is useful only when it identifies the expected file **and** the
expected PDF page, ZIP member, or database cell where applicable. Empty/failing
tasks keep null useful-result timings. Measured failures remain in completion
samples and prevent a timing-goal pass. Task/routes run in sequential isolated
workers, each with a 60-second total budget including startup and repetitions.
Unresponsive workers are terminated; missing timings remain unmeasured and fail
that task. Per-worker JSONL checkpoints survive interruption. `--resume` validates
an existing generated manifest's root, path boundaries and hashes, reuses its own
index, and leaves initial index time unmeasured. Iteration zero is a fresh worker's
first invocation with warm OS caches, followed by same-worker warm samples. It is
not an OS-cold measurement. Coordinator peak and worker working-set samples are
reported separately, not as aggregate memory. Warm percentiles use
nearest rank; 20 samples are exploratory and at least 100 are needed for a more
useful P99 estimate. Large-corpus, OS-cold, human relevance, and competitive claims
must not be inferred from a passing smoke run.

Compare runs on the same corpus and preserve timeouts and missing warm samples.
Fresh-process timings,
warm percentiles, cumulative allocation and retained memory are separate measures.

For an installed-product comparison:

1. Freeze a corpus copy outside all index/report folders. Use identical real files
   in every product, retain its manifest/hash list, and include unsuccessful and
   unsupported tasks. Never compare the older seeded metadata corpus to a product
   indexing real files.
2. Record product version/channel/license/features, executable/version paths,
   hardware, storage/filesystem, OS/runtime, available RAM, power mode, antivirus
   state, corpus provenance, indexed scope, exclusions, freshness, optional OCR,
   language/model/version, and whether indexes/models were already loaded. Record
   unavailable information as unknown, not an assumed default.
3. Use known-file tasks for filename-oriented products, and document investigations
   for content-oriented products. Establish equivalent literal/phrase/AND behavior
   deliberately. Record different interpretation or unsupported coverage as a
   task result; do not silently change the question for one product.
4. Keep first process launch, first invocation, OS-cold tests, and warm runs
   separate. Randomize product order across independent sessions; use at least
   100 warm samples for tail comparisons. Do not flush system caches or alter
   machine settings as a side effect of the harness.
5. Record first correct visible result, successful open at the expected source,
   task completion, cancellation, responsiveness, process memory, index disk size,
   and event-to-searchable freshness. Engine timings, dispatcher idle, and human
   task times are different measurements. Record first/deep database page and
   snapshot/schema costs separately where instrumentation permits.
6. Freeze a separate relevance set before tuning ranking or typo behavior. Have a
   reviewer judge answers on files not used for tuning, including plausible
   distractors, phrases, no-match cases, and ordinary-language semantic requests.
   Report top-result accuracy, MRR, NDCG@10, Recall@20 and zero-result rate with
   the query count and judgments. The controlled `tasks` fixture checks correctness;
   it is not this held-out set.
7. Retain raw samples, settings, failed/partial/unsupported tasks, and conditions
   alongside a curated result summary. Keep the [README benchmark overview](../README.md#benchmark-results)
   brief. Performance conclusions require a correctness-qualified build; goals are not promises.

Installed competitor versions/configurations, human task/relevance judgments,
semantic models, network/removable roots and OS-cold measurements are not supplied
by these generated-fixture runners. Qualify them separately before making public
comparisons.

## Existing component-suite notes

The metadata corpus is direct-seeded so the full profile can cover one million indexed entries without creating one million real files. Physical corpus files exercise the extractor and content index paths.

The stopped-indexer recovery corpus currently validates restart correctness through a fresh refresh against deterministic changed files. Native USN catch-up qualification should be covered by a separate Windows-only integration suite on NTFS volumes.

`incremental_catch_up_throughput` seeds files before timing, changes their contents, and times replacement upserts rather than fresh inserts. `incremental_delete_throughput` then times removal of those indexed files. Together they exercise CSharpDB 4.0.2's index-planned mutation path and FileSearch's transactional per-file cleanup.

The `indexed_query_phase_*` metrics come from an internal timing hook on `CSharpDbFileIndex.SearchAsync` that is only attached while the instrumented iterations run; the headline latency percentiles are measured without it. The `_other` phase is the remainder between the whole call and the named phases.

`index_freshness_after_event` starts the real `IndexingService`, `IndexQueue`, and `IndexWatcherService`, watches the content root, writes a marker file, and polls indexed search until the marker is visible. It measures steady-state freshness (watcher active, index current) and includes the watcher debounce window; it excludes startup catch-up.

`substring_index_live_parity` runs the same mid-token substring query through the indexed path and the live scanner and compares distinct matched files. 100 means the indexed path found every file the live baseline found; lower values quantify indexed substring misses.

`memory_per_million_files` is only reported when the index holds at least 100,000 files; below that, normalizing whole-process working set per million files amplifies fixed process overhead into a meaningless number. `benchmark_process_working_set` is always reported, raw.

Scan-bound measurements (regex, substring, live scan) run `clamp(QueryIterations / 10, 3, 10)` iterations so the standard and full profiles stay tractable.

The semantic quantization regression compares exact int8 results with the float32 baseline. Each deterministic query must retain at least 9 of the float top 10 and keep common-result score error at or below 0.01. The 200,000-vector local gate measured 88.3 ms P95 and about 12 KB allocated per query after removing per-candidate score-object allocation, so HNSW is not currently selected.
