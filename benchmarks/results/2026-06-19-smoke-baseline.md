# 2026-06-19 Smoke Baseline

This is a development smoke result from the deterministic `smoke` profile. It validates that the benchmark harness can generate a corpus, build the index, measure latency and throughput, score relevance, and write reports. It is not a release-performance claim.

## Run

| Field | Value |
| --- | --- |
| Profile | `smoke` |
| Command | `dotnet run --project .\benchmarks\FileSearch.Benchmarks\FileSearch.Benchmarks.csproj -- report --profile smoke --force-corpus --force-index` |
| OS | Windows developer workstation |
| Runtime | .NET 10 |
| Corpus | 10,000 metadata-only entries; 59 physical files |

## Metrics

| Metric | Value | Unit |
| --- | ---: | --- |
| Metadata query P50 | 86.23 | ms |
| Metadata query P95 | 120.362 | ms |
| Metadata query P99 | 139.419 | ms |
| Indexed content query P50 | 84.209 | ms |
| Indexed content query P95 | 104.997 | ms |
| Indexed content query P99 | 113.909 | ms |
| Time to first result | 82.753 | ms |
| Initial index throughput | 2.575 | files/second |
| Incremental catch-up throughput | 1.97 | files/second |
| Index disk size | 23,105,536 | bytes |
| Extraction success rate | 100 | percent |
| Crash/restart correctness | 100 | percent |

## Relevance

| Metric | Value |
| --- | ---: |
| MRR | 1 |
| NDCG@10 | 1 |
| Recall@20 | 1 |
| Zero-result rate | 0 |
| Top-result accuracy | 1 |
| Query count | 7 |

## Notes

This run shows the current smoke metadata P95 is above the initial internal target of 50 ms, so it should be treated as a baseline for optimization work rather than a pass/fail release gate.
