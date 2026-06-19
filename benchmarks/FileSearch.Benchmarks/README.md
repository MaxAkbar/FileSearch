# FileSearch Benchmarks

This project generates deterministic benchmark corpora and measures FileSearch indexing, search latency, relevance, recovery correctness, disk usage, memory normalization, and extraction success.

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
