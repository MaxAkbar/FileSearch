using System.Diagnostics;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Core.Walker;

namespace FileSearch.Benchmarks;

/// <summary>
/// Measures the drive name index on a real volume: scan time and memory,
/// snapshot size and load time, ranked filename query latency, journal
/// catch-up after a burst of changes, and a timed parity check of the
/// indexed name search against the live walk for one folder.
/// </summary>
internal sealed class VolumeIndexBenchmarkRunner
{
    private static readonly string[] s_queries =
    [
        "report",
        "a",
        "config json",
        "invoice 2026",
        "readme md",
        "xyz-no-such-file-name",
        @"Users\maxim",
    ];

    public async Task<int> RunAsync(string drive, VolumeBuildMethod method, string? parityScope, CancellationToken cancellationToken)
    {
        var resolver = new WindowsIndexVolumeResolver();
        var root = VolumeNameIndexService.NormalizeVolumeRoot(drive);
        if (!resolver.TryResolveVolume(root, out var volume, out var reason))
        {
            Console.Error.WriteLine($"Cannot resolve {root}: {reason}");
            return 2;
        }

        var journal = new WindowsUsnJournalReader();
        var checkpoint = await journal.QueryAsync(volume, cancellationToken).ConfigureAwait(false);
        var resolved = method == VolumeBuildMethod.Auto
            ? Environment.IsPrivilegedProcess ? VolumeBuildMethod.MasterFileTable : VolumeBuildMethod.DirectoryWalk
            : method;

        Console.WriteLine($"Volume: {root} ({volume.FileSystemName}), method: {resolved}, elevated: {Environment.IsPrivilegedProcess}");
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var scanWatch = Stopwatch.StartNew();
        var table = new WindowsVolumeScanner().Scan(new VolumeScanTarget(root, volume.VolumeDevicePath), resolved, null, cancellationToken);
        scanWatch.Stop();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"Scan: {table.Count:n0} entries in {scanWatch.Elapsed.TotalSeconds:n1} s ({table.Count / Math.Max(scanWatch.Elapsed.TotalSeconds, 0.001):n0} entries/s)");
        Console.WriteLine($"Table memory: {(managedAfter - managedBefore) / (1024d * 1024d):n1} MiB managed ({table.EstimatedBytes / (1024d * 1024d):n1} MiB arrays, {(managedAfter - managedBefore) / (double)Math.Max(table.Count, 1):n0} bytes/entry)");
        Console.WriteLine($"Records: high water {table.HighWater:n0}, name bytes {table.NameBytesInUse:n0} (avg {table.NameBytesInUse / (double)Math.Max(table.Count, 1):n1})");

        var snapshotPath = Path.Combine(Path.GetTempPath(), "filesearch-volume-benchmark-" + Guid.NewGuid().ToString("N") + ".fsvol");
        try
        {
            var header = new VolumeSnapshotHeader(volume.VolumeKey, volume.VolumeSerial, root, checkpoint.JournalId, checkpoint.NextUsn,
                resolved, DateTime.UtcNow, DateTime.UtcNow, table.RootRecord, table.SequenceAt(table.RootRecord), table.Count);
            var writeWatch = Stopwatch.StartNew();
            VolumeSnapshotSerializer.WriteFile(snapshotPath, header, table);
            writeWatch.Stop();
            var readWatch = Stopwatch.StartNew();
            var loaded = VolumeSnapshotSerializer.TryReadFile(snapshotPath, out var error);
            readWatch.Stop();
            Console.WriteLine($"Snapshot: {new FileInfo(snapshotPath).Length / (1024d * 1024d):n1} MiB, write {writeWatch.ElapsedMilliseconds:n0} ms, load {readWatch.ElapsedMilliseconds:n0} ms{(loaded is null ? $" (FAILED: {error})" : string.Empty)}");
        }
        finally
        {
            File.Delete(snapshotPath);
        }

        await using var service = new VolumeNameIndexService(
            resolver,
            journal,
            new WindowsVolumeScanner(),
            new WindowsVolumeFileIdResolverFactory(),
            launcher: null,
            new VolumeNameIndexOptions { SnapshotDirectory = Path.Combine(Path.GetTempPath(), "filesearch-volume-benchmark") });
        var state = service.AttachForTesting(root, table, volume, checkpoint.JournalId, checkpoint.NextUsn);

        var catchUpWatch = Stopwatch.StartNew();
        await service.CatchUpForTestingAsync(state, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Journal catch-up since scan start: {catchUpWatch.ElapsedMilliseconds:n0} ms");

        Console.WriteLine();
        Console.WriteLine("Ranked filename queries (whole volume, top 100):");
        foreach (var query in s_queries)
        {
            var timings = new List<double>();
            VolumeTermSearchResult? last = null;
            for (var i = 0; i < 12; i++)
            {
                var watch = Stopwatch.StartNew();
                last = await service.SearchAsync(new VolumeTermSearchRequest(query, ScopeRoots: [root]), cancellationToken).ConfigureAwait(false);
                watch.Stop();
                if (i >= 2)
                    timings.Add(watch.Elapsed.TotalMilliseconds);
            }

            timings.Sort();
            Console.WriteLine(
                $"  {query,-24} matches {last!.TotalMatches,10:n0}   P50 {Percentile(timings, 0.5),7:n1} ms   P95 {Percentile(timings, 0.95),7:n1} ms   top: {(last.Matches.Count > 0 ? last.Matches[0].Path : "-")}");
        }

        await MeasureChurnAsync(service, state, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(parityScope) && Directory.Exists(parityScope))
            await MeasureParityAsync(service, parityScope, cancellationToken).ConfigureAwait(false);

        return 0;
    }

    private static async Task MeasureChurnAsync(VolumeNameIndexService service, VolumeIndexState state, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-volume-churn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await service.CatchUpForTestingAsync(state, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 2_000; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"churn-{i:D5}.txt"), "x", cancellationToken).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            await service.CatchUpForTestingAsync(state, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            var found = await service.SearchAsync(
                new VolumeTermSearchRequest("churn-0", MaxResults: 5_000, ScopeRoots: [directory]),
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"Journal catch-up after creating 2,000 files: {watch.ElapsedMilliseconds:n0} ms; indexed afterwards: {found.TotalMatches:n0}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MeasureParityAsync(VolumeNameIndexService service, string scope, CancellationToken cancellationToken)
    {
        var plain = new PlainTextExtractor();
        var live = new Searcher(new FileWalker(), new ExtractorRegistry(new ITextExtractor[] { plain }, plain));
        var indexed = new VolumeNameSearcher(live, service);
        Console.WriteLine();
        Console.WriteLine($"Name search parity under {scope}:");
        foreach (var (text, target) in new[] { ("config", SearchTarget.FileNames), ("test", SearchTarget.FolderNames), ("e", SearchTarget.FileAndFolderNames) })
        {
            var query = new TermQuery(text);
            var options = new WalkerOptions();
            var liveWatch = Stopwatch.StartNew();
            var liveHits = await CollectAsync(live, new SearchRequest(query, [scope], options, SearchTarget: target), cancellationToken).ConfigureAwait(false);
            liveWatch.Stop();
            var statuses = new List<string>();
            var indexedWatch = Stopwatch.StartNew();
            var indexedHits = await CollectAsync(indexed, new SearchRequest(query, [scope], options, UseIndex: true, Status: statuses.Add, SearchTarget: target), cancellationToken).ConfigureAwait(false);
            indexedWatch.Stop();

            var livePaths = liveHits.Select(hit => hit.Path).ToHashSet(StringComparer.Ordinal);
            var indexedPaths = indexedHits.Select(hit => hit.Path).ToHashSet(StringComparer.Ordinal);
            var route = indexedHits.Count > 0 && indexedHits.All(hit => hit.Route == HitRoute.Indexed) ? "index" : statuses.LastOrDefault() ?? "live";
            Console.WriteLine(
                $"  {target,-18} \"{text}\": live {liveHits.Count,8:n0} hits {liveWatch.ElapsedMilliseconds,7:n0} ms | indexed {indexedHits.Count,8:n0} hits {indexedWatch.ElapsedMilliseconds,7:n0} ms | same set: {livePaths.SetEquals(indexedPaths)} | route: {route}");
            if (!livePaths.SetEquals(indexedPaths))
            {
                foreach (var path in livePaths.Except(indexedPaths).Take(5))
                    Console.WriteLine($"      live only:    {path}");
                foreach (var path in indexedPaths.Except(livePaths).Take(5))
                    Console.WriteLine($"      indexed only: {path}");
            }
        }
    }

    private static async Task<List<Hit>> CollectAsync(ISearcher searcher, SearchRequest request, CancellationToken cancellationToken)
    {
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, cancellationToken).ConfigureAwait(false))
            hits.Add(hit);
        return hits;
    }

    private static double Percentile(List<double> sorted, double percentile) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];
}
