using BenchmarkDotNet.Running;
using FileSearch.Core.Engine;

namespace FileSearch.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var options = CommandOptions.Parse(args.Skip(1));
        var profile = BenchmarkProfile.Resolve(options.Profile);
        var paths = BenchmarkPaths.Resolve(profile, options.Root);

        try
        {
            switch (command)
            {
                case "generate":
                    var manifest = new BenchmarkCorpusGenerator().EnsureCorpus(paths, options.ForceCorpus);
                    Console.WriteLine($"Generated {manifest.Profile} corpus at {paths.CorpusDirectory}");
                    Console.WriteLine($"Physical files: {manifest.PhysicalFileCount:n0}");
                    Console.WriteLine($"Metadata-only entries: {manifest.MetadataOnlyEntryCount:n0}");
                    return 0;

                case "report":
                    var report = await new BenchmarkReportRunner()
                        .RunAsync(paths, options.ForceCorpus, options.ForceIndex, CancellationToken.None)
                        .ConfigureAwait(false);
                    Console.WriteLine($"Wrote report for {report.Profile} profile to {paths.ReportsDirectory}");
                    return 0;

                case "bench":
                    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args.Skip(1).ToArray());
                    return 0;

                case "semantic":
                    var semantic = new SemanticVectorBenchmarkRunner().Run(
                        options.Documents ?? 200_000,
                        options.Queries ?? 20);
                    Console.WriteLine($"Documents: {semantic.DocumentCount:n0}");
                    Console.WriteLine($"Dimensions: {semantic.Dimension:n0}");
                    Console.WriteLine($"Int8 payload: {semantic.PayloadBytes / (1024d * 1024d):n1} MiB");
                    Console.WriteLine($"Exact search P50/P95/P99: {semantic.Latency.P50Milliseconds:n1} / {semantic.Latency.P95Milliseconds:n1} / {semantic.Latency.P99Milliseconds:n1} ms");
                    Console.WriteLine($"Allocated per query: {semantic.AllocatedBytesPerQuery:n0} bytes");
                    Console.WriteLine(semantic.RequiresHnsw
                        ? "Decision: exact scan exceeded the 250 ms P95 gate; evaluate HNSW."
                        : "Decision: exact scan meets the 250 ms P95 gate; do not add HNSW.");
                    return semantic.RequiresHnsw ? 3 : 0;

                case "semantic-model":
                    var modelSmoke = await new SemanticModelSmokeRunner().RunAsync(
                        options.ModelId ?? "all-minilm-l6-v2-onnx",
                        options.ModelDirectory ?? EmbeddingModelPackOptions.GetDefaultModelPacksDirectory(),
                        options.InstallModel,
                        CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine($"Model: {modelSmoke.ModelId}");
                    Console.WriteLine($"Texts: {modelSmoke.TextCount:n0}");
                    Console.WriteLine($"Largest inference batch: {modelSmoke.LargestInferenceBatchSize:n0}");
                    Console.WriteLine($"Compatibility fallback used: {modelSmoke.UsedCompatibilityFallback}");
                    Console.WriteLine($"Single/batch elapsed: {modelSmoke.SingleElapsedMilliseconds:n1} / {modelSmoke.BatchElapsedMilliseconds:n1} ms");
                    Console.WriteLine($"Minimum batch-vs-single cosine: {modelSmoke.MinimumCosineSimilarity:n6}");
                    return modelSmoke.MinimumCosineSimilarity >= 0.9999 && modelSmoke.LargestInferenceBatchSize > 1 ? 0 : 4;

                case "volume":
                    return await new VolumeIndexBenchmarkRunner().RunAsync(
                        options.Drive ?? @"C:\",
                        options.VolumeMethod,
                        options.ParityScope,
                        CancellationToken.None).ConfigureAwait(false);

                case null:
                case "":
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown benchmark command: {command}");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("FileSearch.Benchmarks");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  generate  Generate deterministic benchmark corpus only.");
        Console.WriteLine("  report    Generate corpus, build indexes, measure metrics, and write Markdown/JSON reports.");
        Console.WriteLine("  bench     Run BenchmarkDotNet benchmarks.");
        Console.WriteLine("  semantic  Measure exact int8 semantic-vector search and evaluate the HNSW gate.");
        Console.WriteLine("  semantic-model  Smoke-test real batched ONNX embeddings against single inference.");
        Console.WriteLine("  volume    Build a drive name index for a real volume and measure scan, snapshot, query, and journal costs.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --profile smoke|standard|full");
        Console.WriteLine("  --root <directory>");
        Console.WriteLine("  --force-corpus");
        Console.WriteLine("  --force-index");
        Console.WriteLine("  --documents <count>  Semantic benchmark document count (default 200000).");
        Console.WriteLine("  --queries <count>    Semantic benchmark measured queries (default 20).");
        Console.WriteLine("  --model-id <id>      Semantic model catalog ID.");
        Console.WriteLine("  --model-directory <path>  Explicit model-pack directory.");
        Console.WriteLine("  --install-model      Install the model before the semantic-model smoke.");
        Console.WriteLine("  --drive <C:>         Volume for the volume benchmark (default C:).");
        Console.WriteLine("  --method auto|mft|walk  Volume scan method; mft needs an elevated prompt.");
        Console.WriteLine("  --parity-scope <dir>  Folder to compare indexed and live name searches under.");
    }

    private sealed record CommandOptions(
        string? Profile,
        string? Root,
        bool ForceCorpus,
        bool ForceIndex,
        int? Documents,
        int? Queries,
        string? ModelId,
        string? ModelDirectory,
        bool InstallModel,
        string? Drive = null,
        FileSearch.Core.Volumes.VolumeBuildMethod VolumeMethod = FileSearch.Core.Volumes.VolumeBuildMethod.Auto,
        string? ParityScope = null)
    {
        public static CommandOptions Parse(IEnumerable<string> args)
        {
            string? profile = Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_PROFILE");
            string? root = Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_ROOT");
            var forceCorpus = false;
            var forceIndex = false;
            int? documents = null;
            int? queries = null;
            string? modelId = null;
            string? modelDirectory = null;
            var installModel = false;
            string? drive = null;
            var volumeMethod = FileSearch.Core.Volumes.VolumeBuildMethod.Auto;
            string? parityScope = null;
            var queue = new Queue<string>(args);

            while (queue.Count > 0)
            {
                var arg = queue.Dequeue();
                switch (arg)
                {
                    case "--profile" when queue.Count > 0:
                        profile = queue.Dequeue();
                        break;

                    case "--root" when queue.Count > 0:
                        root = queue.Dequeue();
                        break;

                    case "--force-corpus":
                        forceCorpus = true;
                        break;

                    case "--force-index":
                        forceIndex = true;
                        break;

                    case "--documents" when queue.Count > 0 && int.TryParse(queue.Dequeue(), out var parsedDocuments):
                        documents = parsedDocuments;
                        break;

                    case "--queries" when queue.Count > 0 && int.TryParse(queue.Dequeue(), out var parsedQueries):
                        queries = parsedQueries;
                        break;

                    case "--model-id" when queue.Count > 0:
                        modelId = queue.Dequeue();
                        break;

                    case "--model-directory" when queue.Count > 0:
                        modelDirectory = queue.Dequeue();
                        break;

                    case "--install-model":
                        installModel = true;
                        break;

                    case "--drive" when queue.Count > 0:
                        drive = queue.Dequeue();
                        break;

                    case "--method" when queue.Count > 0:
                        volumeMethod = queue.Dequeue().ToLowerInvariant() switch
                        {
                            "mft" => FileSearch.Core.Volumes.VolumeBuildMethod.MasterFileTable,
                            "walk" => FileSearch.Core.Volumes.VolumeBuildMethod.DirectoryWalk,
                            _ => FileSearch.Core.Volumes.VolumeBuildMethod.Auto,
                        };
                        break;

                    case "--parity-scope" when queue.Count > 0:
                        parityScope = queue.Dequeue();
                        break;
                }
            }

            return new CommandOptions(
                profile,
                root,
                forceCorpus,
                forceIndex,
                documents,
                queries,
                modelId,
                modelDirectory,
                installModel,
                drive,
                volumeMethod,
                parityScope);
        }
    }
}
