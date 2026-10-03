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
