using BenchmarkDotNet.Running;

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
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --profile smoke|standard|full");
        Console.WriteLine("  --root <directory>");
        Console.WriteLine("  --force-corpus");
        Console.WriteLine("  --force-index");
    }

    private sealed record CommandOptions(
        string? Profile,
        string? Root,
        bool ForceCorpus,
        bool ForceIndex)
    {
        public static CommandOptions Parse(IEnumerable<string> args)
        {
            string? profile = Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_PROFILE");
            string? root = Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_ROOT");
            var forceCorpus = false;
            var forceIndex = false;
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
                }
            }

            return new CommandOptions(profile, root, forceCorpus, forceIndex);
        }
    }
}
