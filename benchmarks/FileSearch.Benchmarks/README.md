# FileSearch Benchmarks

This project generates deterministic benchmark corpora and measures FileSearch indexing, search latency, relevance, recovery correctness, disk usage, memory normalization, and extraction success.

## Native filename task corpus

```powershell
dotnet run -c Release --project benchmarks/FileSearch.Benchmarks --artifacts-path artifacts/nw06-build -- quick-name-tasks --documents 10000 --queries 20
```

`quick-name-tasks` uses the real Windows `DirectoryWalkScanner` to build a table
from generated **empty files**, then queries the actual `VolumeTermSearch` kernel.
The frozen `quick-name-tasks-v1` definition has 32 tasks: 16 calibration and 16
held-out, each with 12 intended targets and four literal/negative controls. It
includes a declared snapshot of filenames from this repository, standard source
and document naming patterns, delimiter/PascalCase/acronym boundaries, duplicate
leaf names with explicit roots, punctuation, Unicode, exact deep stems and
literal typo/acronym/operator controls. No repository or user file contents are
copied. Do not tune against the held-out partition or alter the frozen definition
after capturing a baseline.

The default 10,000 deterministic distractors contain many nonmatches and more
than the top-80 shortlist's 320-candidate window for several same-tier queries.
Use `--documents 100000` for the larger generated-file scale, or 1,000 for setup
diagnostics; the supported range is 1,000 through 100,000. `--queries` supplies
3–200 warm repetitions per task after the separately retained first invocation.
The first invocation is not OS-cold, and later tasks share the process/table/JIT
state. Run without concurrent builds/tests/probes.

`--root` changes the parent directory (default `artifacts/benchmarks`). The command
uses its own `quick-name-tasks/<version>/distractors-<count>/corpus` tree and never
opens the user index. Existing manifests/fixtures must match exactly; mismatches
are retained and rejected rather than overwritten. Reuse the same root/count
before and after a scorer change. Each run writes a new GUID report directory
under `quick-name-tasks/reports`, with the declared definition/task SHA-256,
manifest and generated paths/types/empty-byte fingerprints, settings, source and
loaded-assembly hashes, raw query JSONL, per-task JSON checkpoints and final JSON.

Top 1/Top 5/MRR exclude expected no-match controls, which have separate counts.
All intended misses, complete ranked lists, unexpected control hits, rank changes
across repetitions, errors, timeouts and incomplete tasks remain in the report.
Exit zero means the diagnostic completed with intact sources/fixtures and controls;
it does not mean every intended target ranked first. Exit 5 indicates integrity,
setup, control or query failure; inspect the retained artifact. Generation, corpus
validation, scanning and query compilation have separate clocks. The actual
batch-kernel clock includes query ranking/visibility/path reconstruction, with
per-sample process-total allocated-byte deltas, GC collections and sampled working
set. A 15-second cancellation deadline bounds each query; scanning has 120 seconds.

The kernel returns one completed list, so first-useful **streaming** latency is
not separately observable and remains null. A distinct target-at-completion field
is populated only if the target was returned; missing targets never become zero
latency. First and warm completion samples/nearest-rank percentiles are retained.
Small repeat counts do not qualify stable tail latency or production performance.

Real NTFS file-reference IDs can make even a small subtree table sparse; report
`RootRecord`, `HighWater`, entry count and estimated table bytes alongside process
resources. Those costs depend on volume history, not only fixture-file count.
Fixture-root-relative depth differs from production drive-root-relative depth.
This is neither a whole-drive MFT/USN/service benchmark nor a UI/user-task latency,
installed-product, OS-cold, accessibility or real-world relevance qualification.
It adds no approximate matching or usage learning. Keep baseline artifacts and
failures, and finish the broader qualification before publishing product claims.

## Database preview phase profile

Use an existing generated task manifest to profile just the database preview path:

```powershell
dotnet run -c Release --project benchmarks/FileSearch.Benchmarks -- database-profile --resume C:/path/to/task-manifest.json --queries 100
```

Run without concurrent builds/tests/probes. `--queries` specifies an even number
of measured SQLite/CSharpDB pairs, from 10 to 1000. Six alternating warmup pairs
are retained separately. Measured order alternates SQLite/CSharpDB then
CSharpDB/SQLite, giving each engine equal first/second positions. Every session
uses a fresh private snapshot of the same source fixture; the corpus/index and
user settings are not rebuilt or changed. The command reads the existing corpus,
validates its hashes, and saves a new GUID report directory under
`artifacts/benchmarks/database-phases/`.

It records snapshot copying and other snapshot work, engine/connection opening,
connection settings, schema discovery, source validation, first/deep-page query
and row mapping, and cleanup. Opt-in clocks run on the actual `DatabaseReadSession`
path. Ordinary previews do not collect timing objects or call the clock. Copy is
part of Snapshot; nested totals and independent percentiles must not be added.
Paired differences subtract matching successful rounds, rather than subtracting
unpaired P95s. Failures, missing phases, warmups, positions, source main/WAL/sidecar
hashes, sizes, software versions and environment are retained in JSON/JSONL.

`SequentialDeepPage` separately measures the final page after an untimed read of
its preceding page. FirstPage and arbitrary DeepPage retain their original order
and meaning. Tables with at most 250 rows have no applicable sequential deep page;
inapplicable/failed timings remain null. The priming read has already warmed engine
pages, so compare the same phase across builds rather than equating it with the
original random jump.

These are warm-OS-cache preview measurements. Engine-open APIs perform different
amounts of initialization/catalog work, so this is not a pure storage-engine
comparison. See the [benchmark results summary](../../README.md#benchmark-results).

## Real-file task baseline

```powershell
dotnet build benchmarks/FileSearch.Benchmarks/FileSearch.Benchmarks.csproj -c Release
dotnet benchmarks/FileSearch.Benchmarks/bin/Release/net10.0/FileSearch.Benchmarks.dll tasks --profile standard --queries 20
```

Run without concurrent builds/tests/probes. Each invocation creates a new GUID run
under `artifacts/benchmarks/tasks/`; it never deletes an old corpus or uses the user
index. `--root` changes that parent. `--queries` is the number of warm repetitions
(3–1000); iteration zero is retained separately. Use at least 100 for useful tail
estimates. Timing goals are reported, not enforced as deterministic test thresholds.
Exit code 5 means a task, exact-source check, cancellation, extraction, refresh,
or source-integrity check failed; inspect the retained JSON rather than discarding
the run. Exit code 1 indicates a setup/unhandled error.
Each task/route runs in its own sequential worker, with iteration zero followed by
warm repetitions in that worker. A 60-second total worker budget (including startup
and all repetitions) bounds unresponsive library work. Timeouts fail that task and
retain completed samples; missing timings are never recorded as zero. Lower the
repetition count if a slow but completing task exceeds the total budget. Raw samples
are flushed to `<route>-<task>.jsonl` between queries and survive interrupted runs.
An interrupted run is incomplete, not a pass.
The standard/full profiles can take minutes to build and run; start with smoke.

To reuse an unchanged generated corpus/index after interruption:

```powershell
dotnet benchmarks/FileSearch.Benchmarks/bin/Release/net10.0/FileSearch.Benchmarks.dll tasks --resume C:/path/to/task-manifest.json --queries 3
```

Resume checks the original root, relative path boundaries and every source hash.
It writes reports to a new run directory and leaves initial index time unmeasured.
It uses only the index beside that generated manifest. The final freshness probe
adds/updates one generated `freshness.txt` fixture and updates that private index.
Per-worker working sets and coordinator peak memory are separate; they are not
combined into a fictitious process-memory total.

The corpus contains real text/source, Unicode, Word, Excel, PDF, EML and ZIP files,
and SQLite/CSharpDB tables. Smoke has 50 background files and 1,000 rows per database;
standard has 10,000 background files and 10,000 rows; full has 250,000 background
files and 50,000 rows. **These counts differ from the older `report` profiles:**
`tasks` seeds no virtual metadata and creates no customer/private content.

The manifest records hashes, queries, modes, targets, and expected file/page/member/
record locations. Reports retain first invocation and warm raw samples, first any
and first correct result, completion percentiles, failures, status, allocation and
memory snapshots, versions/build/Git state, index size, explicit update freshness,
active-query cancellation, and database open/first/deep-page component times.
First invocation has a fresh worker and warm OS caches; it is **not OS-cold**. Copy/schema
costs are combined. A no-match task has no first-useful-result time.

The `sqlite`/`csharpdb` search tasks use the same database-value query and corpus;
the label selects which source location counts as the first useful result. Both
databases are scanned/searched in each query. Their completion times do not
compare engine speed; indexed results use the same FileSearch CSharpDB index.
Use `database-profile` for separate per-source preview measurements.

Run the [WPF preview baseline](../FileSearch.UiPerfProbe/README.md#real-file-preview-baseline)
against the same manifest afterward. Optional installed Windows OCR is qualified
there using a generated scan. See [methodology and comparison protocol](../../docs/BENCHMARKS.md#task-baseline-and-comparison-protocol)
before interpreting these controlled fixtures as real-user relevance or competitor
results. Held-out judgments, installed models/products, and complete user tasks
remain separate qualification.

Run a fast smoke report:

```powershell
dotnet run --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- report --profile smoke --force-index
```

Run the full-scale local benchmark:

```powershell
dotnet run --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- report --profile full --force-index
```

Generated corpora, databases, and reports are written under `artifacts/benchmarks/<profile>` by default. The full profile includes one million metadata-only entries and hundreds of thousands of physical text/source files; it is intended for a dedicated local run, not routine CI.

Optional external root probes can be supplied with:

```powershell
$env:FILESEARCH_BENCHMARK_NETWORK_ROOT='\\server\share'
$env:FILESEARCH_BENCHMARK_REMOVABLE_ROOT='E:\'
$env:FILESEARCH_BENCHMARK_CLOUD_ROOT="$env:OneDrive"
```

BenchmarkDotNet search microbenchmarks are also available:

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- bench --filter *
```

Run the deterministic 200,000-vector exact semantic-search gate with:

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- semantic --documents 200000 --queries 20
```

This command measures exact top-50 search over 384-dimension int8 vectors. It recommends an HNSW evaluation only when P95 exceeds 250 ms.

Run a real model batch-vs-single smoke in an explicit model directory with:

```powershell
dotnet run -c Release --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- semantic-model --model-id all-minilm-l6-v2-onnx --model-directory "$env:TEMP\filesearch-semantic-model-smoke" --install-model
```
