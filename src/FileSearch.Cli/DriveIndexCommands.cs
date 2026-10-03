using System.Globalization;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Volumes;

namespace FileSearch.Cli;

/// <summary>
/// <c>filesearch volumes ...</c>: build, inspect, remove, and search
/// whole-drive file name indexes. The CLI never tracks drives live; it
/// loads saved snapshots on demand and replays the change journal in
/// memory, so searches are current without the GUI running.
/// </summary>
internal sealed class DriveIndexCommands
{
    public const string Usage =
        "Usage: filesearch volumes list [--json]\n" +
        "       filesearch volumes build DRIVE [--admin|--walk] [--json]\n" +
        "       filesearch volumes remove DRIVE\n" +
        "       filesearch volumes search TEXT [--drive DRIVE]... [--path FOLDER]... [--limit N] [--files|--folders] [--hidden]\n" +
        "                                 [--json|--jsonl|--csv|--markdown] [--output PATH]\n" +
        "  build uses the master file table when elevated or with --admin (Windows asks for permission),\n" +
        "  otherwise an unprivileged folder scan; --walk forces the folder scan.";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions s_jsonLine = new();
    private readonly IVolumeNameIndex _index;

    public DriveIndexCommands(IVolumeNameIndex index)
    {
        _index = index;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        var rest = args.Skip(1).ToArray();
        switch (args[0].ToLowerInvariant())
        {
            case "list":
            case "status":
                return List(rest);
            case "build":
            case "rebuild":
                return await BuildAsync(rest, cancellationToken).ConfigureAwait(false);
            case "remove":
            case "clear":
                return await RemoveAsync(rest, cancellationToken).ConfigureAwait(false);
            case "search":
            case "find":
                return await SearchAsync(rest, cancellationToken).ConfigureAwait(false);
            default:
                Console.Error.WriteLine($"Unknown volumes command: {args[0]}");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private int List(string[] args)
    {
        var statuses = _index.GetStatuses().ToDictionary(status => status.VolumeRoot, StringComparer.OrdinalIgnoreCase);
        var rows = _index.GetCandidateVolumes()
            .Select(candidate =>
            {
                statuses.TryGetValue(candidate.VolumeRoot, out var status);
                return new VolumeListRow(
                    candidate.VolumeRoot,
                    candidate.Label,
                    candidate.FileSystem,
                    candidate.IsSupported,
                    candidate.IsSupported ? status?.Phase.ToString() ?? nameof(VolumeIndexPhase.NotIndexed) : nameof(VolumeIndexPhase.Unsupported),
                    status?.EntryCount ?? 0,
                    status?.BuildMethod?.ToString(),
                    status?.BuiltUtc,
                    status?.UpdatedUtc,
                    candidate.IsSupported ? status?.Message : candidate.UnsupportedReason);
            })
            .ToList();

        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(rows, s_json));
            return 0;
        }

        foreach (var row in rows)
        {
            var detail = row.State == nameof(VolumeIndexPhase.Ready)
                ? $"{row.Entries:n0} names, {row.Method}, built {row.BuiltUtc?.ToLocalTime():g}"
                : row.Message ?? string.Empty;
            Console.Out.WriteLine($"{row.Root,-5} {row.FileSystem,-6} {row.State,-12} {detail}");
        }

        return 0;
    }

    private async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var drive = args.FirstOrDefault(arg => !arg.StartsWith('-'));
        if (drive is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var method = args.Contains("--walk", StringComparer.OrdinalIgnoreCase)
            ? VolumeBuildMethod.DirectoryWalk
            : args.Contains("--admin", StringComparer.OrdinalIgnoreCase) || args.Contains("--mft", StringComparer.OrdinalIgnoreCase)
                ? VolumeBuildMethod.MasterFileTable
                : VolumeBuildMethod.Auto;
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        void OnStatus(object? sender, VolumeIndexStatus status)
        {
            if (!json && status.Phase == VolumeIndexPhase.Building && status.ProgressEntries is > 0)
                Console.Error.Write($"\r{status.VolumeRoot} {status.ProgressEntries.Value:n0} names...");
        }

        _index.StatusChanged += OnStatus;
        var started = DateTime.UtcNow;
        try
        {
            var status = await _index.BuildAsync(drive, method, cancellationToken).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - started;
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    root = status.VolumeRoot,
                    state = status.Phase.ToString(),
                    entries = status.EntryCount,
                    method = status.BuildMethod?.ToString(),
                    seconds = Math.Round(elapsed.TotalSeconds, 2),
                    message = status.Message,
                }, s_json));
            }
            else
            {
                Console.Error.WriteLine();
                Console.Out.WriteLine($"Indexed {status.EntryCount:n0} names on {status.VolumeRoot} with {status.BuildMethod} in {elapsed.TotalSeconds:n1} s.");
            }

            return 0;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            _index.StatusChanged -= OnStatus;
        }
    }

    private async Task<int> RemoveAsync(string[] args, CancellationToken cancellationToken)
    {
        var drive = args.FirstOrDefault(arg => !arg.StartsWith('-'));
        if (drive is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        await _index.RemoveAsync(drive, cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine($"Removed the file name index for {drive}.");
        return 0;
    }

    private async Task<int> SearchAsync(string[] args, CancellationToken cancellationToken)
    {
        var terms = new List<string>();
        var roots = new List<string>();
        var limit = 50;
        var includeFiles = true;
        var includeFolders = true;
        var includeHidden = false;
        string? format = null;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--drive" or "--path" or "-p" when i + 1 < args.Length:
                    roots.Add(args[++i]);
                    break;
                case "--limit" or "-n" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    limit = Math.Max(1, parsed);
                    i++;
                    break;
                case "--files":
                    includeFolders = false;
                    break;
                case "--folders":
                    includeFiles = false;
                    break;
                case "--hidden":
                    includeHidden = true;
                    break;
                case "--json" or "--jsonl" or "--csv" or "--markdown" or "--md":
                    format = args[i].TrimStart('-').ToLowerInvariant();
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                default:
                    terms.Add(args[i]);
                    break;
            }
        }

        var text = string.Join(' ', terms);
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var result = await _index.SearchAsync(
            new VolumeTermSearchRequest(
                text,
                limit,
                roots.Count == 0 ? null : roots,
                includeFiles,
                includeFolders,
                IncludeHiddenFolders: includeHidden,
                ExcludeDirectoryNames: includeHidden ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null),
            cancellationToken).ConfigureAwait(false);

        if (result.SearchedVolumes.Count == 0)
        {
            Console.Error.WriteLine("No drive name index covers the requested drives. Build one with: filesearch volumes build C:");
            return 4;
        }

        var rendered = Render(text, result, format);
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Out.Write(rendered);
        }
        else
        {
            var fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, rendered, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static string Render(string text, VolumeTermSearchResult result, string? format)
    {
        var matches = result.Matches.Select(match => new VolumeMatchRow(match.Path, match.IsDirectory, Math.Round(match.Score, 1))).ToList();
        var builder = new StringBuilder();
        switch (format)
        {
            case "json":
                builder.AppendLine(JsonSerializer.Serialize(new
                {
                    query = text,
                    totalMatches = result.TotalMatches,
                    elapsedMilliseconds = Math.Round(result.Elapsed.TotalMilliseconds, 1),
                    searchedVolumes = result.SearchedVolumes,
                    uncoveredRoots = result.UncoveredRoots,
                    matches,
                }, s_json));
                break;
            case "jsonl":
                foreach (var match in matches)
                    builder.AppendLine(JsonSerializer.Serialize(match, s_jsonLine));
                break;
            case "csv":
                builder.AppendLine("Path,IsDirectory,Score");
                foreach (var match in matches)
                    builder.AppendLine(CultureInfo.InvariantCulture, $"{Csv(match.Path)},{match.IsDirectory},{match.Score}");
                break;
            case "markdown" or "md":
                builder.AppendLine(CultureInfo.InvariantCulture, $"# Drive name search: {text}");
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"{result.TotalMatches:n0} matches in {result.Elapsed.TotalMilliseconds:n0} ms.");
                builder.AppendLine();
                builder.AppendLine("| Path | Kind | Score |");
                builder.AppendLine("| --- | --- | ---: |");
                foreach (var match in matches)
                    builder.AppendLine(CultureInfo.InvariantCulture, $"| `{match.Path.Replace("|", "\\|", StringComparison.Ordinal)}` | {(match.IsDirectory ? "folder" : "file")} | {match.Score} |");
                break;
            default:
                foreach (var match in matches)
                    builder.AppendLine(match.IsDirectory ? match.Path + Path.DirectorySeparatorChar : match.Path);
                builder.AppendLine(CultureInfo.InvariantCulture, $"-- {result.TotalMatches:n0} matches in {result.Elapsed.TotalMilliseconds:n0} ms (showing {matches.Count:n0})");
                foreach (var root in result.UncoveredRoots)
                    builder.AppendLine(CultureInfo.InvariantCulture, $"-- not indexed: {root}");
                break;
        }

        return builder.ToString();
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private sealed record VolumeListRow(
        string Root,
        string Label,
        string FileSystem,
        bool Supported,
        string State,
        long Entries,
        string? Method,
        DateTime? BuiltUtc,
        DateTime? UpdatedUtc,
        string? Message);

    private sealed record VolumeMatchRow(string Path, bool IsDirectory, double Score);
}
