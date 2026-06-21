using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Extractors;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Workflows;
using Spectre.Console;

namespace FileSearch.Cli;

internal sealed class FileSearchRepl
{
    private static readonly string[] s_indexerStatusArgs = ["status"];

    private readonly Searcher _liveSearcher;
    private readonly IFileIndex _index;
    private readonly IndexCoverageService _coverageService;
    private readonly IQueryFactory _queryFactory;
    private readonly IExtractorRegistry _extractorRegistry;
    private readonly IWorkflowStore _workflowStore;
    private readonly IWorkflowRunner _workflowRunner;
    private readonly CliState _state = new();
    private CancellationTokenSource? _activeCommand;

    public FileSearchRepl(
        Searcher liveSearcher,
        IFileIndex index,
        IndexCoverageService coverageService,
        IQueryFactory queryFactory,
        IExtractorRegistry extractorRegistry,
        IWorkflowStore workflowStore,
        IWorkflowRunner workflowRunner)
    {
        _liveSearcher = liveSearcher;
        _index = index;
        _coverageService = coverageService;
        _queryFactory = queryFactory;
        _extractorRegistry = extractorRegistry;
        _workflowStore = workflowStore;
        _workflowRunner = workflowRunner;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 &&
            args[0].Equals("search", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RunOneShotSearchAsync(args.Skip(1).ToArray(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        if (args.Length > 0 &&
            args[0].Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RunOneShotIndexAsync(args.Skip(1).ToArray(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        if (args.Length > 0 &&
            args[0].Equals("workflow", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RunOneShotWorkflowAsync(args.Skip(1).ToArray(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        if (args.Length > 0 &&
            args[0].Equals("indexer", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RunOneShotIndexerAsync(args.Skip(1).ToArray(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        ApplyStartupArgs(args);
        RenderHeader();
        RenderQuickHelp();

        while (true)
        {
            var input = ReadInput();
            if (input is null)
                return 0;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var tokens = CommandLine.Tokenize(input);
            if (tokens.Count == 0)
                continue;

            using var commandCts = new CancellationTokenSource();
            _activeCommand = commandCts;

            try
            {
                var keepRunning = await ExecuteAsync(input, tokens, commandCts.Token).ConfigureAwait(false);
                if (!keepRunning)
                    return 0;
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Canceled.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            }
            finally
            {
                _activeCommand = null;
            }
        }
    }

    private async Task<bool> ExecuteAsync(
        string input,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        var command = tokens[0].ToLowerInvariant();
        switch (command)
        {
            case "exit":
            case "quit":
            case "q":
                return false;
            case "help":
            case "?":
            case "h":
                RenderHelp();
                return true;
            case "clear":
            case "cls":
                AnsiConsole.Clear();
                return true;
            case "path":
            case "root":
            case "cd":
                SetRoot(tokens);
                return true;
            case "pwd":
                RenderRoot();
                return true;
            case "options":
            case "opts":
            case "settings":
                RenderOptions();
                return true;
            case "mode":
                SetMode(tokens);
                return true;
            case "target":
                SetTarget(tokens);
                return true;
            case "case":
                SetBool(tokens, "case sensitivity", value => _state.CaseSensitive = value, () => _state.CaseSensitive);
                return true;
            case "recursive":
            case "recurse":
                SetBool(tokens, "recursive search", value => _state.Recursive = value, () => _state.Recursive);
                return true;
            case "hidden":
                SetBool(tokens, "hidden files", value => _state.IncludeHidden = value, () => _state.IncludeHidden);
                return true;
            case "docs":
            case "documents":
            case "document-extraction":
                SetBool(tokens, "document extraction", value => _state.EnableDocumentExtraction = value, () => _state.EnableDocumentExtraction);
                return true;
            case "ocr":
            case "image-ocr":
                SetBool(tokens, "image OCR", value => _state.EnableImageOcr = value, () => _state.EnableImageOcr);
                return true;
            case "known-only":
            case "known-types":
            case "skip-unknown":
                SetBool(tokens, "known file types only", value => _state.SkipUnknownFileTypes = value, () => _state.SkipUnknownFileTypes);
                return true;
            case "limit":
                SetLimit(tokens);
                return true;
            case "include":
                SetGlobList(tokens, _state.IncludeGlobs, "include globs");
                return true;
            case "exclude":
                SetGlobList(tokens, _state.ExcludeGlobs, "exclude globs");
                return true;
            case "ext":
            case "include-ext":
                SetExtensionList(tokens, _state.IncludeExtensions, "included extensions");
                return true;
            case "exclude-ext":
                SetExtensionList(tokens, _state.ExcludeExtensions, "excluded extensions");
                return true;
            case "exclude-dir":
            case "exclude-dirs":
                SetNameList(tokens, _state.ExcludeDirectories, "excluded folders");
                return true;
            case "plain-ext":
            case "text-ext":
                SetExtensionList(tokens, _state.AdditionalPlainTextExtensions, "additional plain-text extensions");
                return true;
            case "min-size":
                SetSize(tokens, "minimum file size", value => _state.MinFileSizeBytes = value);
                return true;
            case "max-size":
                SetSize(tokens, "maximum file size", value => _state.MaxFileSizeBytes = value);
                return true;
            case "after":
                SetDate(tokens, "modified after", value => _state.ModifiedAfterUtc = value);
                return true;
            case "before":
                SetDate(tokens, "modified before", value => _state.ModifiedBeforeUtc = value);
                return true;
            case "clear-filters":
                _state.ClearFilters();
                AnsiConsole.MarkupLine("[green]Filters cleared.[/]");
                return true;
            case "search":
            case "find":
            case "s":
                await SearchAsync(CommandLine.JoinArgs(tokens.Skip(1)), cancellationToken).ConfigureAwait(false);
                return true;
            case "index":
                await ExecuteIndexCommandAsync(tokens, cancellationToken).ConfigureAwait(false);
                return true;
            case "workflow":
                await ExecuteWorkflowCommandAsync(tokens, cancellationToken).ConfigureAwait(false);
                return true;
            case "indexer":
                await ExecuteIndexerCommandAsync(tokens, cancellationToken).ConfigureAwait(false);
                return true;
            case "locations":
                await RenderLocationsAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case "db":
                RenderDatabasePath();
                return true;
            default:
                await SearchAsync(input, cancellationToken).ConfigureAwait(false);
                return true;
        }
    }

    private async Task SearchAsync(string queryText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            queryText = AnsiConsole.Ask<string>("Query:");

        if (!Directory.Exists(_state.Root))
        {
            AnsiConsole.MarkupLine($"[red]Search root does not exist:[/] {Markup.Escape(_state.Root)}");
            return;
        }

        Query query;
        try
        {
            query = _queryFactory.Build(queryText, _state.Mode, _state.CaseSensitive);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid query:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        var hits = new List<Hit>();
        var statusMessages = new ConcurrentQueue<string>();
        var totalHits = 0;
        var progress = new SearchProgress(0, 0, 0, 0, 0);
        var indexed = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var walkerOptions = _state.ToWalkerOptions(_extractorRegistry.SupportedExtensions);
        var request = new SearchRequest(
            query,
            [_state.Root],
            walkerOptions,
            Progress: p => progress = p,
            UseIndex: _state.UseIndex,
            Status: message => statusMessages.Enqueue(message),
            RawQuery: queryText,
            Mode: _state.Mode,
            SearchTarget: _state.SearchTarget);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Searching {Markup.Escape(_state.Root)}", async ctx =>
            {
                IAsyncEnumerable<Hit> stream;
                if (_state.UseIndex)
                {
                    var coverage = await _coverageService.GetCoverageAsync(request, cancellationToken).ConfigureAwait(false);
                    statusMessages.Enqueue(coverage.IsCovered
                        ? coverage.Message
                        : $"{coverage.Message}; using live scan");
                    indexed = coverage.IsCovered;
                    stream = coverage.IsCovered
                        ? _index.SearchAsync(request, cancellationToken)
                        : _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken);
                }
                else
                {
                    stream = _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken);
                }

                await foreach (var hit in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    totalHits++;
                    if (hits.Count < _state.ResultLimit)
                        hits.Add(hit);

                    ctx.Status(BuildSearchStatus(totalHits, progress, indexed));
                }
            }).ConfigureAwait(false);
        stopwatch.Stop();

        RenderSearchSummary(queryText, totalHits, hits.Count, stopwatch.Elapsed, indexed, statusMessages);
        RenderHits(hits, totalHits);
    }

    private async Task<int> RunOneShotSearchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            Console.Out.WriteLine(OneShotSearchUsage);
            return 0;
        }

        var options = ParseOneShotSearchOptions(args);
        if (!Directory.Exists(options.State.Root))
        {
            Console.Error.WriteLine($"Search root does not exist: {options.State.Root}");
            return 2;
        }

        Query query;
        try
        {
            query = _queryFactory.Build(options.QueryText, options.State.Mode, options.State.CaseSensitive);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid query: {ex.Message}");
            return 2;
        }

        var hits = new List<Hit>();
        var totalHits = 0;
        var indexed = false;
        var statusMessages = new ConcurrentQueue<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var request = new SearchRequest(
            query,
            [options.State.Root],
            options.State.ToWalkerOptions(_extractorRegistry.SupportedExtensions),
            Progress: null,
            UseIndex: options.State.UseIndex,
            Status: message => statusMessages.Enqueue(message),
            RawQuery: options.QueryText,
            Mode: options.State.Mode,
            SearchTarget: options.State.SearchTarget);

        IAsyncEnumerable<Hit> stream;
        if (options.State.UseIndex)
        {
            var coverage = await _coverageService.GetCoverageAsync(request, cancellationToken).ConfigureAwait(false);
            statusMessages.Enqueue(coverage.IsCovered ? coverage.Message : $"{coverage.Message}; using live scan");
            indexed = coverage.IsCovered;
            stream = coverage.IsCovered
                ? _index.SearchAsync(request, cancellationToken)
                : _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken);
        }
        else
        {
            stream = _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken);
        }

        await foreach (var hit in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            totalHits++;
            if (hits.Count < options.State.ResultLimit)
                hits.Add(hit);
        }

        stopwatch.Stop();
        var rendered = RenderOneShotSearch(options, hits, totalHits, indexed, stopwatch.Elapsed, statusMessages);
        await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunOneShotIndexAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 ||
            args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            Console.Out.WriteLine(OneShotIndexUsage);
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        switch (action)
        {
            case "build":
            case "refresh":
                return await RunOneShotIndexRefreshAsync(
                        args[1..],
                        "build",
                        IndexRefreshMode.Incremental,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "rebuild":
            case "full":
                return await RunOneShotIndexRefreshAsync(
                        args[1..],
                        "rebuild",
                        IndexRefreshMode.Full,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "clear":
                return await RunOneShotIndexClearAsync(args[1..], cancellationToken).ConfigureAwait(false);
            case "locations":
            case "roots":
                {
                    var options = ParseOneShotIndexOptions(args[1..], allowTargetPath: false);
                    var locations = await _index.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
                    var rendered = RenderOneShotIndexLocations(locations, options.Format);
                    await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
                    return 0;
                }
            case "stats":
                {
                    var options = ParseOneShotIndexOptions(args[1..], allowTargetPath: true);
                    var root = string.IsNullOrWhiteSpace(options.TargetPath)
                        ? Path.GetFullPath(Directory.GetCurrentDirectory())
                        : Path.GetFullPath(ExpandHome(options.TargetPath));
                    var stats = await _index.GetStatsAsync(root, cancellationToken).ConfigureAwait(false);
                    var rendered = RenderOneShotIndexStats(stats, options.Format);
                    await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
                    return 0;
                }
            case "failures":
            case "failed":
                {
                    var options = ParseOneShotIndexOptions(args[1..], allowTargetPath: false);
                    var failures = await _index.GetFailedFilesAsync(cancellationToken).ConfigureAwait(false);
                    var rendered = RenderOneShotIndexFailures(failures, options.Format);
                    await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
                    return 0;
                }
            default:
                Console.Error.WriteLine($"Unknown index command: {args[0]}");
                Console.Error.WriteLine(OneShotIndexUsage);
                return 2;
        }
    }

    private async Task<int> RunOneShotIndexRefreshAsync(
        string[] args,
        string action,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        var options = ParseOneShotIndexOptions(args, allowTargetPath: true, allowWalkerOptions: true);
        var root = ResolveOneShotIndexTarget(options);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Folder does not exist: {root}");
            return 2;
        }

        var progress = new IndexProgress(0, 0, 0, 0, 0, 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var request = new IndexRequest(
            root,
            options.State.ToWalkerOptions(_extractorRegistry.SupportedExtensions),
            p => progress = p);

        await _index.RefreshRootAsync(request, mode, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var stats = await _index.GetStatsAsync(root, cancellationToken).ConfigureAwait(false);
        var document = new OneShotIndexActionDocument(
            action,
            root,
            "completed",
            mode.ToString(),
            stopwatch.Elapsed.TotalSeconds,
            ToOneShotIndexProgress(progress),
            null,
            ToOneShotIndexStats(stats),
            null);
        var rendered = RenderOneShotIndexAction(document, options.Format);
        await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunOneShotIndexClearAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOneShotIndexOptions(args, allowTargetPath: true);
        var root = ResolveOneShotIndexTarget(options);
        var before = await _index.GetStatsAsync(root, cancellationToken).ConfigureAwait(false);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _index.ClearAsync(root, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var after = await _index.GetStatsAsync(root, cancellationToken).ConfigureAwait(false);
        var document = new OneShotIndexActionDocument(
            "clear",
            root,
            "completed",
            null,
            stopwatch.Elapsed.TotalSeconds,
            null,
            ToOneShotIndexStats(before),
            ToOneShotIndexStats(after),
            null);
        var rendered = RenderOneShotIndexAction(document, options.Format);
        await WriteOneShotOutputAsync(rendered, options.OutputPath, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunOneShotWorkflowAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            Console.Out.WriteLine(OneShotWorkflowUsage);
            return 0;
        }

        return await ExecuteWorkflowActionAsync(args, oneShot: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunOneShotIndexerAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            Console.Out.WriteLine(OneShotIndexerUsage);
            return 0;
        }

        return await ExecuteIndexerActionAsync(args, oneShot: true, cancellationToken).ConfigureAwait(false);
    }

    private static OneShotSearchOptions ParseOneShotSearchOptions(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException(OneShotSearchUsage);

        var state = new CliState();
        var queryParts = new List<string>();
        var format = OneShotOutputFormat.Json;
        string? outputPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--path":
                case "--root":
                case "-p":
                    state.Root = Path.GetFullPath(ExpandHome(NextValue(args, ref i, arg)));
                    break;
                case "--mode":
                case "-m":
                    state.Mode = ParseMode(NextValue(args, ref i, arg));
                    break;
                case "--target":
                case "-t":
                    state.SearchTarget = ParseSearchTarget(NextValue(args, ref i, arg));
                    break;
                case "--limit":
                case "-n":
                    if (!int.TryParse(NextValue(args, ref i, arg), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ||
                        limit < 1)
                        throw new ArgumentException("Limit must be a positive integer.");
                    state.ResultLimit = limit;
                    break;
                case "--format":
                case "-f":
                    format = ParseOutputFormat(NextValue(args, ref i, arg));
                    break;
                case "--output":
                case "-o":
                    outputPath = NextValue(args, ref i, arg);
                    break;
                case "--json":
                    format = OneShotOutputFormat.Json;
                    break;
                case "--jsonl":
                case "--json-lines":
                    format = OneShotOutputFormat.JsonLines;
                    break;
                case "--csv":
                    format = OneShotOutputFormat.Csv;
                    break;
                case "--markdown":
                case "--md":
                    format = OneShotOutputFormat.Markdown;
                    break;
                case "--case":
                case "--case-sensitive":
                    state.CaseSensitive = true;
                    break;
                case "--ignore-case":
                    state.CaseSensitive = false;
                    break;
                case "--recursive":
                    state.Recursive = true;
                    break;
                case "--no-recursive":
                    state.Recursive = false;
                    break;
                case "--hidden":
                case "--include-hidden":
                    state.IncludeHidden = true;
                    break;
                case "--docs":
                case "--documents":
                    state.EnableDocumentExtraction = true;
                    break;
                case "--no-docs":
                case "--no-documents":
                    state.EnableDocumentExtraction = false;
                    break;
                case "--ocr":
                case "--image-ocr":
                    state.EnableImageOcr = true;
                    break;
                case "--no-ocr":
                case "--no-image-ocr":
                    state.EnableImageOcr = false;
                    break;
                case "--known-only":
                case "--known-types-only":
                case "--skip-unknown":
                    state.SkipUnknownFileTypes = true;
                    break;
                case "--all-types":
                case "--include-unknown":
                    state.SkipUnknownFileTypes = false;
                    break;
                case "--index":
                    state.UseIndex = true;
                    break;
                case "--no-index":
                    state.UseIndex = false;
                    break;
                case "--include":
                    state.IncludeGlobs.Clear();
                    state.IncludeGlobs.AddRange(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--exclude":
                    state.ExcludeGlobs.Clear();
                    state.ExcludeGlobs.AddRange(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--ext":
                case "--include-ext":
                    state.IncludeExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.IncludeExtensions.Add(extension);
                    break;
                case "--exclude-ext":
                    state.ExcludeExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.ExcludeExtensions.Add(extension);
                    break;
                case "--exclude-dir":
                case "--exclude-dirs":
                    state.ExcludeDirectories.Clear();
                    state.ExcludeDirectories.UnionWith(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--plain-ext":
                case "--text-ext":
                    state.AdditionalPlainTextExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.AdditionalPlainTextExtensions.Add(extension);
                    break;
                case "--min-size":
                    if (!CliState.TryParseSize(NextValue(args, ref i, arg), out var minSize))
                        throw new ArgumentException("Minimum size must be a number with an optional kb, mb, or gb suffix.");
                    state.MinFileSizeBytes = minSize;
                    break;
                case "--max-size":
                    if (!CliState.TryParseSize(NextValue(args, ref i, arg), out var maxSize))
                        throw new ArgumentException("Maximum size must be a number with an optional kb, mb, or gb suffix.");
                    state.MaxFileSizeBytes = maxSize;
                    break;
                case "--after":
                    state.ModifiedAfterUtc = ParseOneShotDate(NextValue(args, ref i, arg));
                    break;
                case "--before":
                    state.ModifiedBeforeUtc = ParseOneShotDate(NextValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option: {arg}");
                    queryParts.Add(arg);
                    break;
            }
        }

        var queryText = string.Join(' ', queryParts).Trim();
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException(OneShotSearchUsage);

        return new OneShotSearchOptions(queryText, state, format, outputPath);
    }

    private static OneShotIndexOptions ParseOneShotIndexOptions(
        string[] args,
        bool allowTargetPath,
        bool allowWalkerOptions = false)
    {
        var state = new CliState();
        var format = OneShotOutputFormat.Json;
        string? outputPath = null;
        string? targetPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--format":
                case "-f":
                    format = ParseOutputFormat(NextValue(args, ref i, arg));
                    break;
                case "--output":
                case "-o":
                    outputPath = NextValue(args, ref i, arg);
                    break;
                case "--path":
                case "--root":
                case "-p":
                    EnsureOneShotIndexTargetAllowed(allowTargetPath, arg);
                    targetPath = NextValue(args, ref i, arg);
                    break;
                case "--json":
                    format = OneShotOutputFormat.Json;
                    break;
                case "--jsonl":
                case "--json-lines":
                    format = OneShotOutputFormat.JsonLines;
                    break;
                case "--csv":
                    format = OneShotOutputFormat.Csv;
                    break;
                case "--markdown":
                case "--md":
                    format = OneShotOutputFormat.Markdown;
                    break;
                case "--recursive":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.Recursive = true;
                    break;
                case "--no-recursive":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.Recursive = false;
                    break;
                case "--hidden":
                case "--include-hidden":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.IncludeHidden = true;
                    break;
                case "--docs":
                case "--documents":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.EnableDocumentExtraction = true;
                    break;
                case "--no-docs":
                case "--no-documents":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.EnableDocumentExtraction = false;
                    break;
                case "--ocr":
                case "--image-ocr":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.EnableImageOcr = true;
                    break;
                case "--no-ocr":
                case "--no-image-ocr":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.EnableImageOcr = false;
                    break;
                case "--known-only":
                case "--known-types-only":
                case "--skip-unknown":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.SkipUnknownFileTypes = true;
                    break;
                case "--all-types":
                case "--include-unknown":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.SkipUnknownFileTypes = false;
                    break;
                case "--include":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.IncludeGlobs.Clear();
                    state.IncludeGlobs.AddRange(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--exclude":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.ExcludeGlobs.Clear();
                    state.ExcludeGlobs.AddRange(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--ext":
                case "--include-ext":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.IncludeExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.IncludeExtensions.Add(extension);
                    break;
                case "--exclude-ext":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.ExcludeExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.ExcludeExtensions.Add(extension);
                    break;
                case "--exclude-dir":
                case "--exclude-dirs":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.ExcludeDirectories.Clear();
                    state.ExcludeDirectories.UnionWith(CliState.SplitList(NextValue(args, ref i, arg)));
                    break;
                case "--plain-ext":
                case "--text-ext":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.AdditionalPlainTextExtensions.Clear();
                    foreach (var extension in CliState.SplitList(NextValue(args, ref i, arg)).Select(CliState.NormalizeExtension))
                        state.AdditionalPlainTextExtensions.Add(extension);
                    break;
                case "--min-size":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    if (!CliState.TryParseSize(NextValue(args, ref i, arg), out var minSize))
                        throw new ArgumentException("Minimum size must be a number with an optional kb, mb, or gb suffix.");
                    state.MinFileSizeBytes = minSize;
                    break;
                case "--max-size":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    if (!CliState.TryParseSize(NextValue(args, ref i, arg), out var maxSize))
                        throw new ArgumentException("Maximum size must be a number with an optional kb, mb, or gb suffix.");
                    state.MaxFileSizeBytes = maxSize;
                    break;
                case "--after":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.ModifiedAfterUtc = ParseOneShotDate(NextValue(args, ref i, arg));
                    break;
                case "--before":
                    EnsureOneShotWalkerOptionsAllowed(allowWalkerOptions, arg);
                    state.ModifiedBeforeUtc = ParseOneShotDate(NextValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option: {arg}");
                    if (!allowTargetPath)
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    if (!string.IsNullOrWhiteSpace(targetPath))
                        throw new ArgumentException("Only one folder can be provided.");
                    targetPath = arg;
                    break;
            }
        }

        return new OneShotIndexOptions(targetPath, state, format, outputPath);
    }

    private static void EnsureOneShotIndexTargetAllowed(bool allowTargetPath, string option)
    {
        if (!allowTargetPath)
            throw new ArgumentException($"{option} is not supported for this index command.");
    }

    private static void EnsureOneShotWalkerOptionsAllowed(bool allowWalkerOptions, string option)
    {
        if (!allowWalkerOptions)
            throw new ArgumentException($"{option} is only supported by index build and index rebuild.");
    }

    private static string ResolveOneShotIndexTarget(OneShotIndexOptions options) =>
        string.IsNullOrWhiteSpace(options.TargetPath)
            ? Path.GetFullPath(Directory.GetCurrentDirectory())
            : Path.GetFullPath(ExpandHome(options.TargetPath));

    private static async Task WriteOneShotOutputAsync(
        string rendered,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Out.Write(rendered);
            return;
        }

        var fullPath = Path.GetFullPath(ExpandHome(outputPath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, rendered, cancellationToken).ConfigureAwait(false);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");

        return args[++index];
    }

    private static QueryMode ParseMode(string value) =>
        value.ToLowerInvariant() switch
        {
            "plain" or "text" or "literal" => QueryMode.PlainText,
            "regex" or "regexp" => QueryMode.Regex,
            "bool" or "boolean" => QueryMode.Boolean,
            "unified" or "query" or "structured" => QueryMode.Unified,
            _ => throw new ArgumentException("Mode must be plain, regex, boolean, or unified."),
        };

    private static SearchTarget ParseSearchTarget(string value) =>
        NormalizeTargetToken(value) switch
        {
            "content" or "contents" or "text" => SearchTarget.Content,
            "file" or "files" or "filename" or "filenames" or "file-name" or "file-names" =>
                SearchTarget.FileNames,
            "folder" or "folders" or "directory" or "directories" or "folder-name" or "folder-names" =>
                SearchTarget.FolderNames,
            "name" or "names" or "file-folder" or "files-folders" or "file-and-folder" or "file-and-folder-names" =>
                SearchTarget.FileAndFolderNames,
            _ => throw new ArgumentException("Target must be content, files, folders, or names."),
        };

    private static string FormatSearchTarget(SearchTarget target) =>
        target switch
        {
            SearchTarget.FileNames => "file names",
            SearchTarget.FolderNames => "folder names",
            SearchTarget.FileAndFolderNames => "file and folder names",
            _ => "contents",
        };

    private static string NormalizeTargetToken(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static OneShotOutputFormat ParseOutputFormat(string value) =>
        value.ToLowerInvariant() switch
        {
            "json" => OneShotOutputFormat.Json,
            "jsonl" or "json-lines" or "jsonlines" => OneShotOutputFormat.JsonLines,
            "csv" => OneShotOutputFormat.Csv,
            "markdown" or "md" => OneShotOutputFormat.Markdown,
            _ => throw new ArgumentException("Format must be json, jsonl, csv, or markdown."),
        };

    private static DateTime ParseOneShotDate(string value)
    {
        if (!DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date))
            throw new ArgumentException("Date must be parseable, for example 2026-06-09.");

        return date.ToUniversalTime();
    }

    private static string RenderOneShotSearch(
        OneShotSearchOptions options,
        IReadOnlyList<Hit> hits,
        int totalHits,
        bool indexed,
        TimeSpan elapsed,
        ConcurrentQueue<string> statusMessages) =>
        options.Format switch
        {
            OneShotOutputFormat.Csv => RenderOneShotCsv(hits),
            OneShotOutputFormat.JsonLines => RenderOneShotJsonLines(hits),
            OneShotOutputFormat.Markdown => RenderOneShotMarkdown(options, hits, totalHits, indexed, elapsed, statusMessages),
            _ => RenderOneShotJson(options, hits, totalHits, indexed, elapsed, statusMessages),
        };

    private static string RenderOneShotJson(
        OneShotSearchOptions options,
        IReadOnlyList<Hit> hits,
        int totalHits,
        bool indexed,
        TimeSpan elapsed,
        ConcurrentQueue<string> statusMessages)
    {
        var document = new OneShotSearchDocument(
            options.QueryText,
            options.State.Root,
            options.State.Mode.ToString(),
            options.State.SearchTarget.ToString(),
            indexed ? "indexed" : "live",
            totalHits,
            hits.Count,
            elapsed.TotalSeconds,
            statusMessages.Distinct().ToArray(),
            hits.Select(ToOneShotHit).ToArray());
        return JsonSerializer.Serialize(document, s_oneShotJsonOptions) + Environment.NewLine;
    }

    private static string RenderOneShotJsonLines(IReadOnlyList<Hit> hits)
    {
        var sb = new StringBuilder();
        foreach (var hit in hits)
            sb.AppendLine(JsonSerializer.Serialize(ToOneShotHit(hit), s_oneShotJsonLineOptions));
        return sb.ToString();
    }

    private static string RenderOneShotCsv(IReadOnlyList<Hit> hits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,lineNumber,kind,score,sizeBytes,modifiedUtc,line");
        foreach (var hit in hits)
        {
            sb.Append(CsvField(hit.Path)).Append(',');
            sb.Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(CsvField(hit.Kind.ToString())).Append(',');
            sb.Append(hit.Score.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(hit.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(CsvField(hit.ModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',');
            sb.AppendLine(CsvField(hit.LineContent));
        }

        return sb.ToString();
    }

    private static string RenderOneShotMarkdown(
        OneShotSearchOptions options,
        IReadOnlyList<Hit> hits,
        int totalHits,
        bool indexed,
        TimeSpan elapsed,
        ConcurrentQueue<string> statusMessages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FileSearch Results");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Query: {options.QueryText}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Root: {options.State.Root}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Mode: {options.State.Mode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Target: {FormatSearchTarget(options.State.SearchTarget)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Search: {(indexed ? "indexed" : "live")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Hits: {totalHits:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Displayed: {hits.Count:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Elapsed: {elapsed.TotalSeconds:0.###}s");
        foreach (var message in statusMessages.Distinct())
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Status: {message}");

        foreach (var group in hits.GroupBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            sb.AppendLine();
            foreach (var hit in group)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- L{hit.LineNumber}: {hit.LineContent.Trim()}");
        }

        return sb.ToString();
    }

    private static string RenderOneShotIndexLocations(
        IReadOnlyList<IndexedLocationInfo> locations,
        OneShotOutputFormat format) =>
        format switch
        {
            OneShotOutputFormat.Csv => RenderOneShotIndexLocationsCsv(locations),
            OneShotOutputFormat.JsonLines => RenderOneShotIndexLocationsJsonLines(locations),
            OneShotOutputFormat.Markdown => RenderOneShotIndexLocationsMarkdown(locations),
            _ => JsonSerializer.Serialize(
                new OneShotIndexLocationsDocument(locations.Count, locations.Select(ToOneShotIndexLocation).ToArray()),
                s_oneShotJsonOptions) + Environment.NewLine,
        };

    private static string RenderOneShotIndexStats(IndexStats stats, OneShotOutputFormat format)
    {
        var document = ToOneShotIndexStats(stats);
        return format switch
        {
            OneShotOutputFormat.Csv => RenderOneShotIndexStatsCsv(document),
            OneShotOutputFormat.JsonLines => JsonSerializer.Serialize(document, s_oneShotJsonLineOptions) + Environment.NewLine,
            OneShotOutputFormat.Markdown => RenderOneShotIndexStatsMarkdown(document),
            _ => JsonSerializer.Serialize(document, s_oneShotJsonOptions) + Environment.NewLine,
        };
    }

    private static string RenderOneShotIndexFailures(
        IReadOnlyList<IndexFailureInfo> failures,
        OneShotOutputFormat format) =>
        format switch
        {
            OneShotOutputFormat.Csv => RenderOneShotIndexFailuresCsv(failures),
            OneShotOutputFormat.JsonLines => RenderOneShotIndexFailuresJsonLines(failures),
            OneShotOutputFormat.Markdown => RenderOneShotIndexFailuresMarkdown(failures),
            _ => JsonSerializer.Serialize(
                new OneShotIndexFailuresDocument(failures.Count, failures.Select(ToOneShotIndexFailure).ToArray()),
                s_oneShotJsonOptions) + Environment.NewLine,
        };

    private static string RenderOneShotIndexAction(
        OneShotIndexActionDocument document,
        OneShotOutputFormat format) =>
        format switch
        {
            OneShotOutputFormat.Csv => RenderOneShotIndexActionCsv(document),
            OneShotOutputFormat.JsonLines => JsonSerializer.Serialize(document, s_oneShotJsonLineOptions) + Environment.NewLine,
            OneShotOutputFormat.Markdown => RenderOneShotIndexActionMarkdown(document),
            _ => JsonSerializer.Serialize(document, s_oneShotJsonOptions) + Environment.NewLine,
        };

    private static string RenderOneShotIndexLocationsJsonLines(IReadOnlyList<IndexedLocationInfo> locations)
    {
        var sb = new StringBuilder();
        foreach (var location in locations)
            sb.AppendLine(JsonSerializer.Serialize(ToOneShotIndexLocation(location), s_oneShotJsonLineOptions));
        return sb.ToString();
    }

    private static string RenderOneShotIndexLocationsCsv(IReadOnlyList<IndexedLocationInfo> locations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("root,exists,fileCount,lineCount,indexedUtc,profile,lastFullScanUtc,volumeKey,lastFullValidationUtc,lastValidationStatus,lastValidationMessage,lastValidationFilesChecked,lastValidationMissingFromIndexCount,lastValidationChangedCount,lastValidationMissingFromDiskCount,lastValidationFailedCount");
        foreach (var location in locations)
        {
            sb.Append(CsvField(location.Root)).Append(',');
            sb.Append(location.Exists ? "true" : "false").Append(',');
            sb.Append(location.FileCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(location.LineCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(CsvField(FormatIsoDate(location.IndexedUtc))).Append(',');
            sb.Append(CsvField(location.Profile)).Append(',');
            sb.Append(CsvField(FormatIsoDate(location.LastFullScanUtc))).Append(',');
            sb.Append(CsvField(location.VolumeKey ?? string.Empty)).Append(',');
            sb.Append(CsvField(FormatIsoDate(location.LastFullValidationUtc))).Append(',');
            sb.Append(CsvField(location.LastValidationStatus)).Append(',');
            sb.Append(CsvField(location.LastValidationMessage)).Append(',');
            sb.Append(location.LastValidationFilesChecked.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(location.LastValidationMissingFromIndexCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(location.LastValidationChangedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(location.LastValidationMissingFromDiskCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(location.LastValidationFailedCount.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string RenderOneShotIndexLocationsMarkdown(IReadOnlyList<IndexedLocationInfo> locations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Indexed Locations");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Count: {locations.Count:n0}");
        if (locations.Count == 0)
            return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("| Root | Exists | Files | Lines | Last indexed | Profile | Validation |");
        sb.AppendLine("| --- | --- | ---: | ---: | --- | --- | --- |");
        foreach (var location in locations)
        {
            sb.Append("| ")
                .Append(MarkdownCell(location.Root)).Append(" | ")
                .Append(location.Exists ? "yes" : "no").Append(" | ")
                .Append(location.FileCount.ToString("n0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(location.LineCount.ToString("n0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(MarkdownCell(FormatIsoDate(location.IndexedUtc))).Append(" | ")
                .Append(MarkdownCell(location.Profile)).Append(" | ")
                .Append(MarkdownCell(location.LastValidationStatus)).AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string RenderOneShotIndexStatsCsv(OneShotIndexStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("root,exists,fileCount,lineCount,indexedUtc");
        sb.Append(CsvField(stats.Root)).Append(',');
        sb.Append(stats.Exists ? "true" : "false").Append(',');
        sb.Append(stats.FileCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append(stats.LineCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.AppendLine(CsvField(FormatIsoDate(stats.IndexedUtc)));
        return sb.ToString();
    }

    private static string RenderOneShotIndexStatsMarkdown(OneShotIndexStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Index Stats");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Root: {stats.Root}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Exists: {(stats.Exists ? "yes" : "no")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Files: {stats.FileCount:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Lines: {stats.LineCount:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Last indexed: {FormatIsoDate(stats.IndexedUtc)}");
        return sb.ToString();
    }

    private static string RenderOneShotIndexFailuresJsonLines(IReadOnlyList<IndexFailureInfo> failures)
    {
        var sb = new StringBuilder();
        foreach (var failure in failures)
            sb.AppendLine(JsonSerializer.Serialize(ToOneShotIndexFailure(failure), s_oneShotJsonLineOptions));
        return sb.ToString();
    }

    private static string RenderOneShotIndexFailuresCsv(IReadOnlyList<IndexFailureInfo> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("root,path,memberPath,failureKind,issueCode,severity,extractorId,extractorVersion,extractionAttemptCount,retryCount,lastAttemptUtc,error");
        foreach (var failure in failures)
        {
            sb.Append(CsvField(failure.Root)).Append(',');
            sb.Append(CsvField(failure.Path)).Append(',');
            sb.Append(CsvField(failure.MemberPath ?? string.Empty)).Append(',');
            sb.Append(CsvField(failure.FailureKind)).Append(',');
            sb.Append(CsvField(failure.IssueCode ?? string.Empty)).Append(',');
            sb.Append(CsvField(failure.Severity ?? string.Empty)).Append(',');
            sb.Append(CsvField(failure.ExtractorId)).Append(',');
            sb.Append(CsvField(failure.ExtractorVersion)).Append(',');
            sb.Append(failure.ExtractionAttemptCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(failure.RetryCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(CsvField(FormatIsoDate(failure.LastAttemptUtc))).Append(',');
            sb.AppendLine(CsvField(failure.Error));
        }

        return sb.ToString();
    }

    private static string RenderOneShotIndexFailuresMarkdown(IReadOnlyList<IndexFailureInfo> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Index Failures");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Count: {failures.Count:n0}");
        if (failures.Count == 0)
            return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("| Path | Issue | Severity | Extractor | Attempts | Last attempt |");
        sb.AppendLine("| --- | --- | --- | --- | ---: | --- |");
        foreach (var failure in failures)
        {
            sb.Append("| ")
                .Append(MarkdownCell(string.IsNullOrWhiteSpace(failure.MemberPath)
                    ? failure.Path
                    : $"{failure.Path}!{failure.MemberPath}")).Append(" | ")
                .Append(MarkdownCell(failure.IssueCode ?? failure.FailureKind)).Append(" | ")
                .Append(MarkdownCell(failure.Severity ?? string.Empty)).Append(" | ")
                .Append(MarkdownCell($"{failure.ExtractorId} {failure.ExtractorVersion}".Trim())).Append(" | ")
                .Append(failure.ExtractionAttemptCount.ToString("n0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(MarkdownCell(FormatIsoDate(failure.LastAttemptUtc))).AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string RenderOneShotIndexActionCsv(OneShotIndexActionDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("action,root,status,mode,elapsedSeconds,filesEnumerated,filesIndexed,filesSkippedUnchanged,filesRemoved,filesFailed,linesIndexed,beforeExists,beforeFileCount,beforeLineCount,beforeIndexedUtc,afterExists,afterFileCount,afterLineCount,afterIndexedUtc,message");
        sb.Append(CsvField(document.Action)).Append(',');
        sb.Append(CsvField(document.Root)).Append(',');
        sb.Append(CsvField(document.Status)).Append(',');
        sb.Append(CsvField(document.Mode ?? string.Empty)).Append(',');
        sb.Append(document.ElapsedSeconds.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append(document.Progress?.FilesEnumerated.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Progress?.FilesIndexed.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Progress?.FilesSkippedUnchanged.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Progress?.FilesRemoved.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Progress?.FilesFailed.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Progress?.LinesIndexed.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Before?.Exists.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() ?? string.Empty).Append(',');
        sb.Append(document.Before?.FileCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.Before?.LineCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(CsvField(FormatIsoDate(document.Before?.IndexedUtc))).Append(',');
        sb.Append(document.After?.Exists.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() ?? string.Empty).Append(',');
        sb.Append(document.After?.FileCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(document.After?.LineCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(CsvField(FormatIsoDate(document.After?.IndexedUtc))).Append(',');
        sb.AppendLine(CsvField(document.Message ?? string.Empty));
        return sb.ToString();
    }

    private static string RenderOneShotIndexActionMarkdown(OneShotIndexActionDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Index Action");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Action: {document.Action}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Root: {document.Root}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Status: {document.Status}");
        if (!string.IsNullOrWhiteSpace(document.Mode))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Mode: {document.Mode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Elapsed: {document.ElapsedSeconds:0.###}s");
        if (!string.IsNullOrWhiteSpace(document.Message))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Message: {document.Message}");

        if (document.Progress is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Progress");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Files enumerated: {document.Progress.FilesEnumerated:n0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Files indexed: {document.Progress.FilesIndexed:n0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Files skipped unchanged: {document.Progress.FilesSkippedUnchanged:n0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Files removed: {document.Progress.FilesRemoved:n0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Files failed: {document.Progress.FilesFailed:n0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Lines indexed: {document.Progress.LinesIndexed:n0}");
        }

        if (document.Before is not null)
            AppendOneShotIndexStatsMarkdown(sb, "Before", document.Before);
        if (document.After is not null)
            AppendOneShotIndexStatsMarkdown(sb, "After", document.After);

        return sb.ToString();
    }

    private static void AppendOneShotIndexStatsMarkdown(
        StringBuilder sb,
        string title,
        OneShotIndexStats stats)
    {
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Exists: {(stats.Exists ? "yes" : "no")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Files: {stats.FileCount:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Lines: {stats.LineCount:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Last indexed: {FormatIsoDate(stats.IndexedUtc)}");
    }

    private static readonly SearchValues<char> s_csvSpecialChars = SearchValues.Create(",\"\n\r");

    private static readonly JsonSerializerOptions s_oneShotJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions s_oneShotJsonLineOptions = new();

    private static string CsvField(string value)
    {
        if (!value.AsSpan().ContainsAny(s_csvSpecialChars))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static OneShotSearchHit ToOneShotHit(Hit hit) =>
        new(
            hit.Path,
            hit.LineNumber,
            hit.Kind.ToString(),
            hit.Score,
            hit.SizeBytes,
            hit.ModifiedUtc,
            hit.LineContent);

    private static OneShotIndexLocation ToOneShotIndexLocation(IndexedLocationInfo location) =>
        new(
            location.Root,
            location.Exists,
            location.FileCount,
            location.LineCount,
            location.IndexedUtc,
            location.Profile,
            location.LastFullScanUtc,
            location.VolumeKey,
            location.LastFullValidationUtc,
            location.LastValidationStatus,
            location.LastValidationMessage,
            location.LastValidationFilesChecked,
            location.LastValidationMissingFromIndexCount,
            location.LastValidationChangedCount,
            location.LastValidationMissingFromDiskCount,
            location.LastValidationFailedCount);

    private static OneShotIndexStats ToOneShotIndexStats(IndexStats stats) =>
        new(stats.Root, stats.Exists, stats.FileCount, stats.LineCount, stats.IndexedUtc);

    private static OneShotIndexFailure ToOneShotIndexFailure(IndexFailureInfo failure) =>
        new(
            failure.Root,
            failure.Path,
            failure.MemberPath,
            failure.FailureKind,
            failure.IssueCode,
            failure.Severity,
            failure.ExtractorId,
            failure.ExtractorVersion,
            failure.ExtractionAttemptCount,
            failure.RetryCount,
            failure.LastAttemptUtc,
            failure.Error);

    private static OneShotIndexProgress ToOneShotIndexProgress(IndexProgress progress) =>
        new(
            progress.FilesEnumerated,
            progress.FilesIndexed,
            progress.FilesSkippedUnchanged,
            progress.FilesRemoved,
            progress.FilesFailed,
            progress.LinesIndexed);

    private static string FormatIsoDate(DateTime? date) =>
        date?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string MarkdownCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private async Task ExecuteIndexCommandAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        if (tokens.Count == 1)
        {
            RenderIndexStatus();
            return;
        }

        var action = tokens[1].ToLowerInvariant();
        switch (action)
        {
            case "on":
                _state.UseIndex = true;
                AnsiConsole.MarkupLine("[green]Indexed search enabled for covered searches.[/]");
                return;
            case "off":
                _state.UseIndex = false;
                AnsiConsole.MarkupLine("[yellow]Indexed search disabled; live scan will be used.[/]");
                return;
            case "build":
            case "refresh":
                await BuildIndexAsync(ResolveFolder(tokens, 2), IndexRefreshMode.Incremental, cancellationToken).ConfigureAwait(false);
                return;
            case "rebuild":
            case "full":
                await BuildIndexAsync(ResolveFolder(tokens, 2), IndexRefreshMode.Full, cancellationToken).ConfigureAwait(false);
                return;
            case "clear":
                await ClearIndexAsync(ResolveFolder(tokens, 2), cancellationToken).ConfigureAwait(false);
                return;
            case "stats":
                await RenderIndexStatsAsync(ResolveFolder(tokens, 2), cancellationToken).ConfigureAwait(false);
                return;
            case "locations":
                await RenderLocationsAsync(cancellationToken).ConfigureAwait(false);
                return;
            case "failures":
            case "failed":
                if (tokens.Count >= 3 && tokens[2].Equals("export", StringComparison.OrdinalIgnoreCase))
                    await ExportIndexFailuresAsync(tokens, cancellationToken).ConfigureAwait(false);
                else
                    await RenderIndexFailuresAsync(cancellationToken).ConfigureAwait(false);
                return;
            case "db":
                RenderDatabasePath();
                return;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown index command:[/] {Markup.Escape(action)}");
                AnsiConsole.MarkupLine("Try [cyan]index build[/], [cyan]index stats[/], [cyan]index failures[/], [cyan]index locations[/], or [cyan]index on[/].");
                return;
        }
    }

    private async Task ExecuteWorkflowCommandAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        if (tokens.Count == 1)
        {
            RenderWorkflowHelp();
            return;
        }

        _ = await ExecuteWorkflowActionAsync(tokens.Skip(1).ToArray(), oneShot: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> ExecuteWorkflowActionAsync(
        string[] args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var action = args[0].ToLowerInvariant();
        switch (action)
        {
            case "list":
            case "ls":
                RenderWorkflowList();
                return 0;
            case "folder":
            case "dir":
                AnsiConsole.MarkupLine($"[bold]Workflow folder:[/] {Markup.Escape(_workflowStore.DirectoryPath)}");
                return 0;
            case "show":
            case "cat":
                {
                    var selector = RequireArgument(args, 1, "workflow show needs a workflow name or file.");
                    var workflow = LoadWorkflow(selector, out _, out var error);
                    if (workflow is null)
                        return RenderCommandError(error ?? "Workflow not found.", oneShot);
                    Console.Out.WriteLine(WorkflowJson.Serialize(workflow));
                    return 0;
                }
            case "validate":
            case "check":
                {
                    var selector = RequireArgument(args, 1, "workflow validate needs a workflow name or file.");
                    var workflow = LoadWorkflow(selector, out var fileName, out var error);
                    if (workflow is null)
                        return RenderCommandError(error ?? "Workflow not found.", oneShot);

                    var errors = WorkflowValidator.Validate(workflow);
                    if (errors.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[green]Workflow is valid:[/] {Markup.Escape(fileName)}");
                        return 0;
                    }

                    AnsiConsole.MarkupLine($"[red]Workflow is invalid:[/] {Markup.Escape(fileName)}");
                    foreach (var validationError in errors)
                        AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(validationError)}");
                    return 2;
                }
            case "dry-run":
            case "dryrun":
                return await RunWorkflowAsync(args, forceDryRun: true, oneShot, cancellationToken).ConfigureAwait(false);
            case "run":
                return await RunWorkflowAsync(args, forceDryRun: false, oneShot, cancellationToken).ConfigureAwait(false);
            default:
                return RenderCommandError($"Unknown workflow command: {args[0]}", oneShot);
        }
    }

    private async Task<int> RunWorkflowAsync(
        string[] args,
        bool forceDryRun,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var selector = RequireArgument(args, 1, "workflow run needs a workflow name or file.");
        var dryRun = forceDryRun;
        var assumeYes = false;
        var showHits = true;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--dry-run":
                case "--dryrun":
                    dryRun = true;
                    break;
                case "--yes":
                case "-y":
                    assumeYes = true;
                    break;
                case "--no-hits":
                    showHits = false;
                    break;
                default:
                    return RenderCommandError($"Unknown workflow run option: {args[i]}", oneShot);
            }
        }

        var workflow = LoadWorkflow(selector, out var fileName, out var error);
        if (workflow is null)
            return RenderCommandError(error ?? "Workflow not found.", oneShot);

        AnsiConsole.MarkupLine(dryRun
            ? $"[yellow]Dry-running workflow:[/] {Markup.Escape(workflow.Name)} ([grey]{Markup.Escape(fileName)}[/])"
            : $"[green]Running workflow:[/] {Markup.Escape(workflow.Name)} ([grey]{Markup.Escape(fileName)}[/])");

        var observer = new CliWorkflowObserver(showHits);
        var interaction = new CliWorkflowInteraction(assumeYes);
        var result = await _workflowRunner.RunAsync(
                workflow,
                new WorkflowRunOptions { DryRun = dryRun },
                observer,
                interaction,
                cancellationToken)
            .ConfigureAwait(false);

        RenderWorkflowResult(result);
        return result.Succeeded ? 0 : 2;
    }

    private async Task ExecuteIndexerCommandAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        if (tokens.Count == 1)
        {
            _ = await ExecuteIndexerActionAsync(s_indexerStatusArgs, oneShot: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _ = await ExecuteIndexerActionAsync(tokens.Skip(1).ToArray(), oneShot: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> ExecuteIndexerActionAsync(
        string[] args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var action = args[0].ToLowerInvariant();
        switch (action)
        {
            case "status":
                return await RenderBackgroundIndexerStatusAsync(oneShot, cancellationToken).ConfigureAwait(false);
            case "start":
                return await StartBackgroundIndexerAsync(oneShot, cancellationToken).ConfigureAwait(false);
            case "pause":
                return await SendSimpleIndexerCommandAsync(BackgroundIndexerCommand.Pause, "Background indexing paused.", oneShot, cancellationToken)
                    .ConfigureAwait(false);
            case "resume":
                return await SendSimpleIndexerCommandAsync(BackgroundIndexerCommand.Resume, "Background indexing resumed.", oneShot, cancellationToken)
                    .ConfigureAwait(false);
            case "shutdown":
            case "stop":
                return await SendSimpleIndexerCommandAsync(
                        BackgroundIndexerCommand.Shutdown,
                        "Background indexer shutdown requested.",
                        oneShot,
                        cancellationToken,
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            case "compact":
                return await SendSimpleIndexerCommandAsync(
                        BackgroundIndexerCommand.CompactDatabase,
                        "Index database compaction requested.",
                        oneShot,
                        cancellationToken,
                        TimeSpan.FromSeconds(90))
                    .ConfigureAwait(false);
            case "profile":
                return await SetIndexerProfileAsync(args, oneShot, cancellationToken).ConfigureAwait(false);
            case "runtime":
                return await SetIndexerRuntimeAsync(args, oneShot, cancellationToken).ConfigureAwait(false);
            case "refresh":
                return await SendLocationIndexerCommandAsync(
                        args,
                        BackgroundIndexerCommand.RefreshRoot,
                        "Background index refresh queued.",
                        oneShot,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "semantic":
            case "smart":
                return await SendLocationIndexerCommandAsync(
                        args,
                        BackgroundIndexerCommand.RefreshSemanticRoot,
                        "Smart Search index refresh queued.",
                        oneShot,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "validate":
                return await ValidateBackgroundIndexerRootAsync(args, oneShot, cancellationToken).ConfigureAwait(false);
            case "queue":
                return await QueueBackgroundIndexerRootAsync(args, oneShot, cancellationToken).ConfigureAwait(false);
            case "add":
            case "add-location":
                return await SendLocationIndexerCommandAsync(
                        args,
                        BackgroundIndexerCommand.AddOrUpdateLocation,
                        "Background indexed location added or updated.",
                        oneShot,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "remove":
            case "remove-location":
                return await RemoveBackgroundIndexerLocationAsync(args, oneShot, cancellationToken).ConfigureAwait(false);
            default:
                return RenderCommandError($"Unknown indexer command: {args[0]}", oneShot);
        }
    }

    private async Task BuildIndexAsync(
        string root,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]Folder does not exist:[/] {Markup.Escape(root)}");
            return;
        }

        var progress = new IndexProgress(0, 0, 0, 0, 0, 0);
        var title = mode == IndexRefreshMode.Full ? "Rebuilding index" : "Refreshing index";
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"{title} for {Markup.Escape(root)}", async ctx =>
            {
                var request = new IndexRequest(
                    root,
                    _state.ToWalkerOptions(_extractorRegistry.SupportedExtensions),
                    p =>
                    {
                        progress = p;
                        ctx.Status(BuildIndexStatus(title, progress));
                    });

                await _index.RefreshRootAsync(request, mode, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        AnsiConsole.MarkupLine("[green]Index refresh complete.[/]");
        await RenderIndexStatsAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearIndexAsync(string root, CancellationToken cancellationToken)
    {
        await _index.ClearAsync(root, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Cleared index for:[/] {Markup.Escape(root)}");
    }

    private async Task RenderIndexStatsAsync(string root, CancellationToken cancellationToken)
    {
        var stats = await _index.GetStatsAsync(root, cancellationToken).ConfigureAwait(false);
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Index stats[/]");
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Root", Markup.Escape(stats.Root));
        table.AddRow("Exists", stats.Exists ? "[green]yes[/]" : "[yellow]no[/]");
        table.AddRow("Files", stats.FileCount.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Lines", stats.LineCount.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Last indexed", FormatDate(stats.IndexedUtc));
        AnsiConsole.Write(table);
    }

    private async Task RenderLocationsAsync(CancellationToken cancellationToken)
    {
        var locations = await _index.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        if (locations.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No indexed locations found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Indexed locations[/]");
        table.AddColumn("Root");
        table.AddColumn("Files");
        table.AddColumn("Lines");
        table.AddColumn("Last indexed");
        table.AddColumn("Profile");

        foreach (var location in locations)
        {
            table.AddRow(
                Markup.Escape(location.Root),
                location.FileCount.ToString("n0", CultureInfo.CurrentCulture),
                location.LineCount.ToString("n0", CultureInfo.CurrentCulture),
                FormatDate(location.IndexedUtc),
                Markup.Escape(location.Profile));
        }

        AnsiConsole.Write(table);
    }

    private async Task RenderIndexFailuresAsync(CancellationToken cancellationToken)
    {
        var failures = await _index.GetFailedFilesAsync(cancellationToken).ConfigureAwait(false);
        if (failures.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No failed index extractions found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Failed index extractions[/]");
        table.AddColumn("Path");
        table.AddColumn("Extractor");
        table.AddColumn("Attempts");
        table.AddColumn("Last attempt");
        table.AddColumn("Error");

        foreach (var failure in failures)
        {
            table.AddRow(
                Markup.Escape(FormatFailurePath(failure)),
                Markup.Escape(FormatExtractor(failure)),
                failure.ExtractionAttemptCount.ToString("n0", CultureInfo.CurrentCulture),
                FormatDate(failure.LastAttemptUtc),
                Markup.Escape(FormatFailureMessage(failure)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Export with [cyan]index failures export failures.csv[/] or [cyan]index failures export failures.json json[/].[/]");
    }

    private async Task ExportIndexFailuresAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Count < 4)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] index failures export PATH [[csv|json]]");
            return;
        }

        var path = tokens[3];
        var format = tokens.Count >= 5
            ? ParseFailureExportFormat(tokens[4])
            : GuessFailureExportFormat(path);

        await _index.ExportFailedFilesAsync(path, format, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Exported index failures to:[/] {Markup.Escape(Path.GetFullPath(path))}");
    }

    private void RenderWorkflowHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Workflow commands[/]");
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("[cyan]workflow list[/]", "List saved workflow JSON files.");
        table.AddRow("[cyan]workflow folder[/]", "Show the workflow library folder.");
        table.AddRow("[cyan]workflow show[/] NAME", "Print a workflow JSON file.");
        table.AddRow("[cyan]workflow validate[/] NAME", "Validate a workflow without running it.");
        table.AddRow("[cyan]workflow run[/] NAME [[--dry-run]] [[--yes]] [[--no-hits]]", "Run a workflow.");
        table.AddRow("[cyan]workflow dry-run[/] NAME", "Run searches and exports in dry-run mode for side-effecting steps.");
        AnsiConsole.Write(table);
    }

    private void RenderWorkflowList()
    {
        var workflows = _workflowStore.List();
        if (workflows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No workflows found in:[/] {Markup.Escape(_workflowStore.DirectoryPath)}");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Workflows[/]");
        table.AddColumn("File");
        table.AddColumn("Name");
        table.AddColumn("Description");
        table.AddColumn("Status");

        foreach (var workflow in workflows)
        {
            table.AddRow(
                Markup.Escape(workflow.FileName),
                Markup.Escape(workflow.Name),
                Markup.Escape(workflow.Description),
                workflow.Error is null ? "[green]ok[/]" : $"[red]{Markup.Escape(workflow.Error)}[/]");
        }

        AnsiConsole.Write(table);
    }

    private WorkflowDefinition? LoadWorkflow(string selector, out string displayName, out string? error)
    {
        selector = ExpandHome(selector.Trim());
        if (File.Exists(selector))
        {
            displayName = Path.GetFileName(selector);
            return WorkflowJson.TryDeserialize(File.ReadAllText(selector), out error);
        }

        var directFileName = selector.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? selector
            : selector + ".json";
        if (directFileName == Path.GetFileName(directFileName))
        {
            var direct = _workflowStore.TryLoad(directFileName, out error);
            if (direct is not null)
            {
                displayName = directFileName;
                return direct;
            }
        }

        var matches = _workflowStore.List()
            .Where(summary =>
                string.Equals(summary.FileName, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(summary.FileName), selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(summary.Name, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length > 1)
        {
            displayName = selector;
            error = $"Workflow selector '{selector}' is ambiguous.";
            return null;
        }

        if (matches.Length == 1)
        {
            displayName = matches[0].FileName;
            return _workflowStore.TryLoad(matches[0].FileName, out error);
        }

        displayName = selector;
        error = $"Workflow not found: {selector}";
        return null;
    }

    private static void RenderWorkflowResult(WorkflowRunResult result)
    {
        var color = result.Succeeded ? "green" : "red";
        AnsiConsole.MarkupLine($"[{color}]Workflow {result.Status}.[/]" +
            (string.IsNullOrWhiteSpace(result.Message) ? "" : $" {Markup.Escape(result.Message)}"));

        if (result.ValidationErrors.Count > 0)
        {
            foreach (var error in result.ValidationErrors)
                AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(error)}");
        }

        if (result.StepOutcomes.Count == 0)
            return;

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Workflow steps[/]");
        table.AddColumn("Step");
        table.AddColumn("Kind");
        table.AddColumn("Result");
        table.AddColumn("Hits");
        table.AddColumn("Files");
        table.AddColumn("Detail");

        foreach (var outcome in result.StepOutcomes)
        {
            table.AddRow(
                Markup.Escape(outcome.DisplayName),
                Markup.Escape(outcome.StepKind),
                outcome.Succeeded ? "[green]ok[/]" : "[red]failed[/]",
                outcome.HitCount.ToString("n0", CultureInfo.CurrentCulture),
                outcome.FileCount.ToString("n0", CultureInfo.CurrentCulture),
                Markup.Escape(outcome.Detail ?? ""));
        }

        AnsiConsole.Write(table);
    }

    private async Task<int> RenderBackgroundIndexerStatusAsync(bool oneShot, CancellationToken cancellationToken)
    {
        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(BackgroundIndexerCommand.GetStatus),
                TimeSpan.FromSeconds(1),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true || response.Status is null)
        {
            AnsiConsole.MarkupLine("[yellow]Background indexer is not running.[/]");
            return 0;
        }

        RenderIndexingStatus(response.Status);
        return 0;
    }

    private static void RenderIndexingStatus(IndexingStatus status)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Background indexer[/]");
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Running", status.IsRunning ? "[green]yes[/]" : "[yellow]no[/]");
        table.AddRow("Paused", status.IsPaused ? "[yellow]yes[/]" : "[green]no[/]");
        table.AddRow("Processing", status.IsProcessing ? "[green]yes[/]" : "[grey]no[/]");
        table.AddRow("Queue", status.QueueLength.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Message", Markup.Escape(status.Message));
        table.AddRow("Active root", Markup.Escape(status.ActiveRoot ?? ""));
        table.AddRow("Active kind", Markup.Escape(status.ActiveKind?.ToString() ?? ""));

        if (status.ActiveProgress is { } progress)
        {
            table.AddRow("Files indexed", progress.FilesIndexed.ToString("n0", CultureInfo.CurrentCulture));
            table.AddRow("Files failed", progress.FilesFailed.ToString("n0", CultureInfo.CurrentCulture));
            table.AddRow("Lines indexed", progress.LinesIndexed.ToString("n0", CultureInfo.CurrentCulture));
        }

        AnsiConsole.Write(table);
    }

    private async Task<int> StartBackgroundIndexerAsync(bool oneShot, CancellationToken cancellationToken)
    {
        var ping = await SendIndexerAsync(
                new BackgroundIndexerRequest(BackgroundIndexerCommand.Ping),
                TimeSpan.FromSeconds(1),
                cancellationToken)
            .ConfigureAwait(false);
        if (ping?.Success == true)
        {
            AnsiConsole.MarkupLine("[green]Background indexer is already running.[/]");
            return 0;
        }

        var executablePath = ResolveIndexerExecutablePath();
        if (!File.Exists(executablePath))
            return RenderCommandError($"Could not find FileSearch.Indexer.exe near '{AppContext.BaseDirectory}'.", oneShot);

        Process.Start(new ProcessStartInfo(executablePath)
        {
            Arguments = "--headless",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            ping = await SendIndexerAsync(
                    new BackgroundIndexerRequest(BackgroundIndexerCommand.Ping),
                    TimeSpan.FromSeconds(1),
                    cancellationToken)
                .ConfigureAwait(false);
            if (ping?.Success == true)
            {
                AnsiConsole.MarkupLine("[green]Background indexer started.[/]");
                return 0;
            }
        }

        return RenderCommandError("Background indexer did not respond after starting.", oneShot);
    }

    private async Task<int> SendSimpleIndexerCommandAsync(
        BackgroundIndexerCommand command,
        string successMessage,
        bool oneShot,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(command),
                timeout ?? TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(successMessage)}[/]");
        return 0;
    }

    private async Task<int> SendLocationIndexerCommandAsync(
        IReadOnlyList<string> args,
        BackgroundIndexerCommand command,
        string successMessage,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var root = ResolveIndexerRoot(args);
        if (!Directory.Exists(root))
            return RenderCommandError($"Folder does not exist: {root}", oneShot);

        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(command, Location: CreateBackgroundLocation(root)),
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(successMessage)}[/] {Markup.Escape(root)}");
        return 0;
    }

    private async Task<int> ValidateBackgroundIndexerRootAsync(
        IReadOnlyList<string> args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var root = ResolveIndexerRoot(args);
        if (!Directory.Exists(root))
            return RenderCommandError($"Folder does not exist: {root}", oneShot);

        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.ValidateRoot,
                    Location: CreateBackgroundLocation(root)),
                TimeSpan.FromSeconds(90),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true || response.ValidationResult is null)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        RenderValidationResult(response.ValidationResult);
        return response.ValidationResult.Status is IndexValidationStatus.Passed ? 0 : 2;
    }

    private async Task<int> QueueBackgroundIndexerRootAsync(
        string[] args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var root = ResolveIndexerRoot(args);
        if (!Directory.Exists(root))
            return RenderCommandError($"Folder does not exist: {root}", oneShot);

        var priority = args.Length >= 3
            ? ParseIndexQueuePriority(args[2])
            : IndexQueuePriority.Normal;
        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.QueueRootRefresh,
                    Location: CreateBackgroundLocation(root),
                    Priority: priority),
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine($"[green]Background index refresh queued ({priority}).[/] {Markup.Escape(root)}");
        return 0;
    }

    private async Task<int> RemoveBackgroundIndexerLocationAsync(
        IReadOnlyList<string> args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var root = ResolveIndexerRoot(args);
        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.RemoveLocation,
                    Root: IndexPath.NormalizeRoot(root)),
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine($"[green]Background indexed location removed.[/] {Markup.Escape(root)}");
        return 0;
    }

    private async Task<int> SetIndexerProfileAsync(
        string[] args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        if (args.Length < 2)
            return RenderCommandError("Usage: indexer profile low|balanced|high", oneShot);

        var profile = args[1].ToLowerInvariant() switch
        {
            "low" => IndexerResourceProfile.Low,
            "balanced" or "normal" => IndexerResourceProfile.Balanced,
            "high" => IndexerResourceProfile.High,
            _ => throw new ArgumentException("Profile must be low, balanced, or high."),
        };

        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.SetResourceProfile,
                    ResourceProfile: profile),
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine($"[green]Background indexer profile set to {profile}.[/]");
        return 0;
    }

    private async Task<int> SetIndexerRuntimeAsync(
        string[] args,
        bool oneShot,
        CancellationToken cancellationToken)
    {
        var pauseOnBattery = false;
        var idleOnly = false;
        var cpuLimit = 0;
        var diskPauseMs = 0;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pause-on-battery":
                    pauseOnBattery = ParseBool(RequireArgument(args, ++i, "--pause-on-battery requires on or off."));
                    break;
                case "--idle-only":
                    idleOnly = ParseBool(RequireArgument(args, ++i, "--idle-only requires on or off."));
                    break;
                case "--cpu":
                    if (!int.TryParse(RequireArgument(args, ++i, "--cpu requires a percent."), NumberStyles.Integer, CultureInfo.InvariantCulture, out cpuLimit))
                        return RenderCommandError("--cpu requires an integer percent.", oneShot);
                    break;
                case "--disk-ms":
                    if (!int.TryParse(RequireArgument(args, ++i, "--disk-ms requires milliseconds."), NumberStyles.Integer, CultureInfo.InvariantCulture, out diskPauseMs))
                        return RenderCommandError("--disk-ms requires an integer millisecond value.", oneShot);
                    break;
                default:
                    return RenderCommandError($"Unknown runtime option: {args[i]}", oneShot);
            }
        }

        var runtime = new IndexerRuntimeOptions(pauseOnBattery, idleOnly, cpuLimit, diskPauseMs).Normalize();
        var response = await SendIndexerAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.SetRuntimeOptions,
                    RuntimeOptions: runtime),
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (response?.Success != true)
            return RenderCommandError(response?.Message ?? "Background indexer is not running.", oneShot);

        AnsiConsole.MarkupLine("[green]Background indexer runtime options updated.[/]");
        return 0;
    }

    private static void RenderValidationResult(IndexValidationResult validation)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Index validation[/]");
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Root", Markup.Escape(validation.Root));
        table.AddRow("Status", validation.Status == IndexValidationStatus.Passed
            ? "[green]Passed[/]"
            : $"[yellow]{Markup.Escape(validation.Status.ToString())}[/]");
        table.AddRow("Checked", validation.FilesChecked.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Matched", validation.FilesMatched.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Missing from index", validation.MissingFromIndex.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Changed", validation.ChangedSinceIndex.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Missing from disk", validation.MissingFromDisk.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Failed checks", validation.FailedChecks.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Message", Markup.Escape(validation.Message));
        AnsiConsole.Write(table);
    }

    private string ResolveIndexerRoot(IReadOnlyList<string> args) =>
        args.Count >= 2
            ? Path.GetFullPath(ExpandHome(args[1]))
            : Path.GetFullPath(_state.Root);

    private BackgroundIndexedLocation CreateBackgroundLocation(string root)
    {
        var location = new IndexedLocation(
            IndexPath.NormalizeRoot(root),
            _state.ToWalkerOptions(_extractorRegistry.SupportedExtensions),
            WatchEnabled: true);
        return BackgroundIndexedLocation.FromIndexedLocation(location);
    }

    private static IndexQueuePriority ParseIndexQueuePriority(string value) =>
        value.ToLowerInvariant() switch
        {
            "high" => IndexQueuePriority.High,
            "normal" => IndexQueuePriority.Normal,
            "low" => IndexQueuePriority.Low,
            _ => throw new ArgumentException("Priority must be high, normal, or low."),
        };

    private static Task<BackgroundIndexerResponse?> SendIndexerAsync(
        BackgroundIndexerRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        BackgroundIndexerClient.TrySendAsync(request, timeout, cancellationToken);

    private static string ResolveIndexerExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "FileSearch.Indexer.exe"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "FileSearch.Indexer",
                "bin",
                "Debug",
                "net10.0-windows10.0.19041.0",
                "FileSearch.Indexer.exe")),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "FileSearch.Indexer",
                "bin",
                "Debug",
                "net10.0-windows",
                "FileSearch.Indexer.exe")),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string RequireArgument(string[] args, int index, string message)
    {
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException(message);
        return args[index];
    }

    private static int RenderCommandError(string message, bool oneShot)
    {
        if (oneShot)
            Console.Error.WriteLine(message);
        else
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 2;
    }

    private void RenderSearchSummary(
        string queryText,
        int totalHits,
        int displayedHits,
        TimeSpan elapsed,
        bool indexed,
        ConcurrentQueue<string> statusMessages)
    {
        var mode = indexed ? "[green]indexed[/]" : "[yellow]live scan[/]";
        var summary =
            $"[bold]Query:[/] {Markup.Escape(queryText)}\n" +
            $"[bold]Root:[/] {Markup.Escape(_state.Root)}\n" +
            $"[bold]Mode:[/] {_state.Mode}  [bold]Target:[/] {Markup.Escape(FormatSearchTarget(_state.SearchTarget))}  [bold]Search:[/] {mode}\n" +
            $"[bold]Hits:[/] {totalHits:n0}  [bold]Displayed:[/] {displayedHits:n0}  [bold]Elapsed:[/] {elapsed.TotalSeconds:n1}s";

        if (!statusMessages.IsEmpty)
        {
            var messages = statusMessages.Distinct().Select(Markup.Escape);
            summary += "\n[bold]Status:[/] " + string.Join(" | ", messages);
        }

        AnsiConsole.Write(new Panel(new Markup(summary))
            .Header("[bold]Search complete[/]")
            .Border(BoxBorder.Rounded));
    }

    private void RenderHits(List<Hit> hits, int totalHits)
    {
        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matches found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Results[/]");
        table.AddColumn("#");
        table.AddColumn("File");
        table.AddColumn("Line");
        table.AddColumn("Match");

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            table.AddRow(
                (i + 1).ToString(CultureInfo.CurrentCulture),
                Markup.Escape(RelativePath(hit.Path)),
                hit.LineNumber.ToString("n0", CultureInfo.CurrentCulture),
                BuildHighlightedMarkup(hit));
        }

        AnsiConsole.Write(table);

        if (totalHits > hits.Count)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Showing first {hits.Count:n0} of {totalHits:n0} hits. Use [cyan]limit <number>[/] to change this.[/]");
        }
    }

    private void RenderHeader()
    {
        AnsiConsole.Write(new FigletText("FileSearch").Color(Color.Green));
        AnsiConsole.Write(new Rule("[bold green]interactive search CLI[/]").RuleStyle("green"));
    }

    private void RenderQuickHelp()
    {
        AnsiConsole.MarkupLine("Type [cyan]help[/] for commands. Type a query directly to search the current folder.");
        RenderRoot();
    }

    private void RenderHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Commands[/]");
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("[cyan]search[/] QUERY", "Search current root. Unknown input is treated as a query.");
        table.AddRow("[cyan]path[/] FOLDER", "Set the current search root.");
        table.AddRow("[cyan]mode plain|regex|boolean|unified[/]", "Choose query mode.");
        table.AddRow("[cyan]target contents|files|folders|names[/]", "Search file contents, file names, folder names, or both name types.");
        table.AddRow("[cyan]case on|off[/]", "Toggle case-sensitive matching.");
        table.AddRow("[cyan]recursive on|off[/]", "Toggle subfolder traversal.");
        table.AddRow("[cyan]hidden on|off[/]", "Include or skip hidden files.");
        table.AddRow("[cyan]docs on|off[/]", "Include Office/PDF/document extractors.");
        table.AddRow("[cyan]ocr on|off[/]", "Include image OCR and OCR fallbacks for scanned/embedded images.");
        table.AddRow("[cyan]known-only on|off[/]", "Restrict content searches to known extractor extensions.");
        table.AddRow("[cyan]include[/] GLOB;GLOB", "Set include glob filters, such as *.cs;*.xaml.");
        table.AddRow("[cyan]exclude[/] GLOB;GLOB", "Set exclude glob filters.");
        table.AddRow("[cyan]ext[/] .cs,.md", "Set included extensions. Use clear to remove.");
        table.AddRow("[cyan]exclude-ext[/] .dll,.exe", "Set excluded extensions. Use clear to remove.");
        table.AddRow("[cyan]exclude-dir[/] .git;node_modules", "Folders pruned from traversal. Use clear to search everything.");
        table.AddRow("[cyan]plain-ext[/] .log,.cfg", "Treat extra extensions as known plain text when known-only is on.");
        table.AddRow("[cyan]min-size 10kb[/] / [cyan]max-size 50mb[/]", "Set file size filters.");
        table.AddRow("[cyan]after yyyy-mm-dd[/] / [cyan]before yyyy-mm-dd[/]", "Set modified date filters.");
        table.AddRow("[cyan]index on|off[/]", "Allow indexed search when the current root is covered.");
        table.AddRow("[cyan]index build[/] FOLDER / [cyan]index rebuild[/] FOLDER", "Refresh the CSharpDB index for a folder. Folder is optional.");
        table.AddRow("[cyan]index stats[/] FOLDER", "Show index stats for a folder. Folder is optional.");
        table.AddRow("[cyan]index locations[/]", "Show all indexed roots in the database.");
        table.AddRow("[cyan]index failures[/]", "Show failed index extractions.");
        table.AddRow("[cyan]index failures export[/] PATH [[csv|json]]", "Export failed index extractions.");
        table.AddRow("[cyan]workflow list|run|dry-run|validate[/]", "List, validate, or run saved workflow JSON files.");
        table.AddRow("[cyan]indexer status|start|pause|resume[/]", "Control the background indexer process.");
        table.AddRow("[cyan]options[/]", "Show current REPL settings.");
        table.AddRow("[cyan]clear-filters[/]", "Reset filters to defaults.");
        table.AddRow("[cyan]exit[/]", "Quit the REPL.");
        AnsiConsole.Write(table);
    }

    private void RenderOptions()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Current options[/]");
        table.AddColumn("Option");
        table.AddColumn("Value");
        table.AddRow("Root", Markup.Escape(_state.Root));
        table.AddRow("Mode", _state.Mode.ToString());
        table.AddRow("Target", Markup.Escape(FormatSearchTarget(_state.SearchTarget)));
        table.AddRow("Case sensitive", Bool(_state.CaseSensitive));
        table.AddRow("Recursive", Bool(_state.Recursive));
        table.AddRow("Include hidden", Bool(_state.IncludeHidden));
        table.AddRow("Use index", Bool(_state.UseIndex));
        table.AddRow("Document extraction", Bool(_state.EnableDocumentExtraction));
        table.AddRow("Image OCR", Bool(_state.EnableImageOcr));
        table.AddRow("Known file types only", Bool(_state.SkipUnknownFileTypes));
        table.AddRow("Result limit", _state.ResultLimit.ToString("n0", CultureInfo.CurrentCulture));
        table.AddRow("Include globs", ListOrAll(_state.IncludeGlobs));
        table.AddRow("Exclude globs", ListOrNone(_state.ExcludeGlobs));
        table.AddRow("Include extensions", ListOrAll(_state.IncludeExtensions));
        table.AddRow("Exclude extensions", ListOrNone(_state.ExcludeExtensions));
        table.AddRow("Exclude folders", ListOrNone(_state.ExcludeDirectories));
        table.AddRow("Additional plain-text extensions", ListOrNone(_state.AdditionalPlainTextExtensions));
        table.AddRow("Min size", CliState.FormatBytes(_state.MinFileSizeBytes));
        table.AddRow("Max size", _state.MaxFileSizeBytes == 0 ? "unlimited" : CliState.FormatBytes(_state.MaxFileSizeBytes));
        table.AddRow("Modified after", FormatDate(_state.ModifiedAfterUtc));
        table.AddRow("Modified before", FormatDate(_state.ModifiedBeforeUtc));
        table.AddRow("Index database", Markup.Escape(_index.DatabasePath));
        AnsiConsole.Write(table);
    }

    private void RenderIndexStatus()
    {
        AnsiConsole.Write(new Panel(new Markup(
                $"[bold]Indexed search:[/] {Bool(_state.UseIndex)}\n" +
                $"[bold]Database:[/] {Markup.Escape(_index.DatabasePath)}\n" +
                "[grey]Use index build, index stats, index failures, index locations, or index clear for maintenance.[/]"))
            .Header("[bold]Index[/]")
            .Border(BoxBorder.Rounded));
    }

    private void RenderDatabasePath() =>
        AnsiConsole.MarkupLine($"[bold]Index database:[/] {Markup.Escape(_index.DatabasePath)}");

    private void RenderRoot() =>
        AnsiConsole.MarkupLine($"[bold]Root:[/] {Markup.Escape(_state.Root)}");

    private void SetRoot(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
        {
            RenderRoot();
            return;
        }

        var root = ResolveFolder(tokens, 1);
        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]Folder does not exist:[/] {Markup.Escape(root)}");
            return;
        }

        _state.Root = root;
        RenderRoot();
    }

    private void SetMode(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]Mode:[/] {_state.Mode}");
            return;
        }

        var value = tokens[1].ToLowerInvariant();
        _state.Mode = value switch
        {
            "plain" or "text" or "literal" => QueryMode.PlainText,
            "regex" or "regexp" => QueryMode.Regex,
            "bool" or "boolean" => QueryMode.Boolean,
            "unified" or "query" or "structured" => QueryMode.Unified,
            _ => throw new ArgumentException("Mode must be plain, regex, boolean, or unified."),
        };
        AnsiConsole.MarkupLine($"[green]Mode set to {_state.Mode}.[/]");
    }

    private void SetTarget(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]Target:[/] {Markup.Escape(FormatSearchTarget(_state.SearchTarget))}");
            return;
        }

        _state.SearchTarget = ParseSearchTarget(CommandLine.JoinArgs(tokens.Skip(1)));
        AnsiConsole.MarkupLine($"[green]Target set to {Markup.Escape(FormatSearchTarget(_state.SearchTarget))}.[/]");
    }

    private static void SetBool(
        IReadOnlyList<string> tokens,
        string label,
        Action<bool> set,
        Func<bool> get)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(label)}:[/] {Bool(get())}");
            return;
        }

        var value = ParseBool(tokens[1]);
        set(value);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to {(value ? "on" : "off")}.[/]");
    }

    private void SetLimit(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]Result limit:[/] {_state.ResultLimit:n0}");
            return;
        }

        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out var limit) ||
            limit < 1)
        {
            throw new ArgumentException("Limit must be a positive number.");
        }

        _state.ResultLimit = limit;
        AnsiConsole.MarkupLine($"[green]Result limit set to {limit:n0}.[/]");
    }

    private static void SetGlobList(IReadOnlyList<string> tokens, List<string> target, string label)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(label)}:[/] {ListOrNone(target)}");
            return;
        }

        target.Clear();
        var raw = CommandLine.JoinArgs(tokens.Skip(1));
        if (!raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
            target.AddRange(CliState.SplitList(raw));

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to:[/] {ListOrNone(target)}");
    }

    private static void SetExtensionList(
        IReadOnlyList<string> tokens,
        HashSet<string> target,
        string label)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(label)}:[/] {ListOrNone(target)}");
            return;
        }

        target.Clear();
        var raw = CommandLine.JoinArgs(tokens.Skip(1));
        if (!raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var extension in CliState.SplitList(raw).Select(CliState.NormalizeExtension))
                target.Add(extension);
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to:[/] {ListOrNone(target)}");
    }

    private static void SetNameList(
        IReadOnlyList<string> tokens,
        HashSet<string> target,
        string label)
    {
        if (tokens.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(label)}:[/] {ListOrNone(target)}");
            return;
        }

        target.Clear();
        var raw = CommandLine.JoinArgs(tokens.Skip(1));
        if (!raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
            target.UnionWith(CliState.SplitList(raw));

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to:[/] {ListOrNone(target)}");
    }

    private static void SetSize(IReadOnlyList<string> tokens, string label, Action<long> set)
    {
        if (tokens.Count < 2)
            throw new ArgumentException($"{label} needs a value, such as 10kb or 50mb.");

        if (!CliState.TryParseSize(tokens[1], out var bytes))
            throw new ArgumentException("Size must be a number with an optional kb, mb, or gb suffix.");

        set(bytes);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to {CliState.FormatBytes(bytes)}.[/]");
    }

    private static void SetDate(IReadOnlyList<string> tokens, string label, Action<DateTime?> set)
    {
        if (tokens.Count < 2)
            throw new ArgumentException($"{label} needs a value, such as 2026-06-09 or clear.");

        if (tokens[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            set(null);
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} cleared.[/]");
            return;
        }

        if (!DateTime.TryParse(
                tokens[1],
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal,
                out var date))
        {
            throw new ArgumentException("Date must be parseable, for example 2026-06-09.");
        }

        set(date.ToUniversalTime());
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} set to {date:d}.[/]");
    }

    private string ResolveFolder(IReadOnlyList<string> tokens, int startIndex)
    {
        if (tokens.Count <= startIndex)
            return Path.GetFullPath(_state.Root);

        var raw = ExpandHome(CommandLine.JoinArgs(tokens.Skip(startIndex)));
        return Path.IsPathFullyQualified(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(raw, _state.Root);
    }

    private void ApplyStartupArgs(string[] args)
    {
        if (args.Length == 0)
            return;

        var candidate = ExpandHome(CommandLine.JoinArgs(args));
        if (Directory.Exists(candidate))
            _state.Root = Path.GetFullPath(candidate);
    }

    private string BuildPrompt()
    {
        var root = _state.Root;
        var display = root.Length <= 42 ? root : $"...{root[^39..]}";
        return $"[bold green]filesearch[/] [grey]{Markup.Escape(display)}[/]> ";
    }

    private string? ReadInput()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        return AnsiConsole.Prompt(
            new TextPrompt<string>(BuildPrompt())
                .AllowEmpty());
    }

    private static string BuildSearchStatus(int totalHits, SearchProgress progress, bool indexed)
    {
        if (indexed)
            return $"Searching index, {totalHits:n0} hits";

        return $"Searching {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} files, {totalHits:n0} hits";
    }

    private static string BuildIndexStatus(string title, IndexProgress progress) =>
        $"{title}: {progress.FilesIndexed:n0} indexed, {progress.FilesSkippedUnchanged:n0} unchanged, " +
        $"{progress.FilesRemoved:n0} removed, {progress.FilesFailed:n0} failed, {progress.LinesIndexed:n0} lines";

    private static string BuildHighlightedMarkup(Hit hit)
    {
        const int MaxLength = 130;
        var content = hit.LineContent;
        var highlights = hit.Highlights
            .Where(span => span.Length > 0 && span.Start >= 0 && span.Start < content.Length)
            .OrderBy(span => span.Start)
            .ToArray();

        var sliceStart = 0;
        var sliceEnd = content.Length;
        if (content.Length > MaxLength)
        {
            var anchor = highlights.FirstOrDefault();
            sliceStart = anchor.Length > 0
                ? Math.Max(0, anchor.Start - 35)
                : 0;
            sliceEnd = Math.Min(content.Length, sliceStart + MaxLength);
            sliceStart = Math.Max(0, sliceEnd - MaxLength);
        }

        var builder = new System.Text.StringBuilder();
        if (sliceStart > 0)
            builder.Append("[grey]...[/]");

        var cursor = sliceStart;
        foreach (var span in highlights)
        {
            var start = Math.Max(span.Start, sliceStart);
            var end = Math.Min(span.Start + span.Length, sliceEnd);
            if (end <= cursor)
                continue;

            if (start > cursor)
                builder.Append(Markup.Escape(content[cursor..start]));

            builder.Append("[black on yellow]");
            builder.Append(Markup.Escape(content[start..end]));
            builder.Append("[/]");
            cursor = end;
        }

        if (cursor < sliceEnd)
            builder.Append(Markup.Escape(content[cursor..sliceEnd]));

        if (sliceEnd < content.Length)
            builder.Append("[grey]...[/]");

        return builder.ToString();
    }

    private string RelativePath(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_state.Root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch
        {
            return path;
        }
    }

    private static string Bool(bool value) =>
        value ? "[green]on[/]" : "[yellow]off[/]";

    private static bool ParseBool(string value) =>
        value.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" or "y" or "1" => true,
            "off" or "false" or "no" or "n" or "0" => false,
            _ => throw new ArgumentException("Value must be on or off."),
        };

    private static string ListOrAll(IEnumerable<string> values)
    {
        var list = values.ToArray();
        return list.Length == 0 ? "[grey]all[/]" : Markup.Escape(string.Join(", ", list));
    }

    private static string ListOrNone(IEnumerable<string> values)
    {
        var list = values.ToArray();
        return list.Length == 0 ? "[grey]none[/]" : Markup.Escape(string.Join(", ", list));
    }

    private static string FormatDate(DateTime? utc)
    {
        if (utc is null)
            return "[grey]none[/]";

        return Markup.Escape(utc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    private static string FormatExtractor(IndexFailureInfo failure) =>
        string.IsNullOrWhiteSpace(failure.ExtractorVersion)
            ? failure.ExtractorId
            : $"{failure.ExtractorId} v{failure.ExtractorVersion}";

    private string FormatFailurePath(IndexFailureInfo failure)
    {
        var path = RelativePath(failure.Path);
        return string.IsNullOrWhiteSpace(failure.MemberPath)
            ? path
            : $"{path}!{failure.MemberPath}";
    }

    private static string FormatFailureMessage(IndexFailureInfo failure) =>
        string.IsNullOrWhiteSpace(failure.IssueCode)
            ? failure.Error
            : $"{failure.IssueCode}: {failure.Error}";

    private static IndexFailureExportFormat GuessFailureExportFormat(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? IndexFailureExportFormat.Json
            : IndexFailureExportFormat.Csv;

    private static IndexFailureExportFormat ParseFailureExportFormat(string value) =>
        value.ToLowerInvariant() switch
        {
            "csv" => IndexFailureExportFormat.Csv,
            "json" => IndexFailureExportFormat.Json,
            _ => throw new ArgumentException("Failure export format must be csv or json."),
        };

    private static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    private const string OneShotSearchUsage =
        "Usage: filesearch search QUERY [--path FOLDER] [--mode plain|regex|boolean|unified] " +
        "[--target contents|files|folders|names] [--ocr] [--no-docs] [--known-only] " +
        "[--json|--jsonl|--csv|--markdown] [--output PATH] [--limit N]";

    private const string OneShotIndexUsage =
        "Usage: filesearch index build|rebuild|clear [FOLDER] OR locations|stats [FOLDER]|failures " +
        "[--ocr] [--no-docs] [--known-only] [--json|--jsonl|--csv|--markdown] [--output PATH]";

    private const string OneShotWorkflowUsage =
        "Usage: filesearch workflow list|folder|show NAME|validate NAME|run NAME [--dry-run] [--yes] [--no-hits]";

    private const string OneShotIndexerUsage =
        "Usage: filesearch indexer status|start|pause|resume|shutdown|compact|refresh [FOLDER]|validate [FOLDER]|semantic [FOLDER]";

    private enum OneShotOutputFormat
    {
        Json,
        JsonLines,
        Csv,
        Markdown,
    }

    private sealed record OneShotSearchOptions(
        string QueryText,
        CliState State,
        OneShotOutputFormat Format,
        string? OutputPath);

    private sealed record OneShotIndexOptions(
        string? TargetPath,
        CliState State,
        OneShotOutputFormat Format,
        string? OutputPath);

    private sealed record OneShotSearchDocument(
        string Query,
        string Root,
        string Mode,
        string Target,
        string Search,
        int TotalHits,
        int DisplayedHits,
        double ElapsedSeconds,
        IReadOnlyList<string> Status,
        IReadOnlyList<OneShotSearchHit> Hits);

    private sealed record OneShotSearchHit(
        string Path,
        int LineNumber,
        string Kind,
        double Score,
        long? SizeBytes,
        DateTime? ModifiedUtc,
        string Line);

    private sealed record OneShotIndexLocationsDocument(
        int Count,
        IReadOnlyList<OneShotIndexLocation> Locations);

    private sealed record OneShotIndexLocation(
        string Root,
        bool Exists,
        long FileCount,
        long LineCount,
        DateTime? IndexedUtc,
        string Profile,
        DateTime? LastFullScanUtc,
        string? VolumeKey,
        DateTime? LastFullValidationUtc,
        string LastValidationStatus,
        string LastValidationMessage,
        long LastValidationFilesChecked,
        long LastValidationMissingFromIndexCount,
        long LastValidationChangedCount,
        long LastValidationMissingFromDiskCount,
        long LastValidationFailedCount);

    private sealed record OneShotIndexStats(
        string Root,
        bool Exists,
        long FileCount,
        long LineCount,
        DateTime? IndexedUtc);

    private sealed record OneShotIndexFailuresDocument(
        int Count,
        IReadOnlyList<OneShotIndexFailure> Failures);

    private sealed record OneShotIndexActionDocument(
        string Action,
        string Root,
        string Status,
        string? Mode,
        double ElapsedSeconds,
        OneShotIndexProgress? Progress,
        OneShotIndexStats? Before,
        OneShotIndexStats? After,
        string? Message);

    private sealed record OneShotIndexProgress(
        long FilesEnumerated,
        long FilesIndexed,
        long FilesSkippedUnchanged,
        long FilesRemoved,
        long FilesFailed,
        long LinesIndexed);

    private sealed record OneShotIndexFailure(
        string Root,
        string Path,
        string? MemberPath,
        string FailureKind,
        string? IssueCode,
        string? Severity,
        string ExtractorId,
        string ExtractorVersion,
        long ExtractionAttemptCount,
        long RetryCount,
        DateTime? LastAttemptUtc,
        string Error);

    private sealed class CliWorkflowObserver(bool showHits) : IWorkflowObserver
    {
        public void OnStepStarted(WorkflowStep step, int depth)
        {
            var indent = new string(' ', Math.Max(0, depth) * 2);
            AnsiConsole.MarkupLine($"{Markup.Escape(indent)}[yellow]start[/] {Markup.Escape(step.DisplayName)} [grey]({Markup.Escape(step.Kind)})[/]");
        }

        public void OnStepCompleted(WorkflowStepOutcome outcome)
        {
            var color = outcome.Succeeded ? "green" : "red";
            var detail = string.IsNullOrWhiteSpace(outcome.Detail) ? "" : $" [grey]{Markup.Escape(outcome.Detail)}[/]";
            AnsiConsole.MarkupLine($"[{color}]done[/] {Markup.Escape(outcome.DisplayName)}{detail}");
        }

        public void OnHit(SearchStep step, Hit hit)
        {
            if (!showHits)
                return;

            var line = hit.LineContent.Length <= 160 ? hit.LineContent : hit.LineContent[..157] + "...";
            AnsiConsole.MarkupLine(
                $"[grey]{Markup.Escape(step.Id)}[/] {Markup.Escape(hit.Path)}:{hit.LineNumber:n0} {Markup.Escape(line)}");
        }

        public void OnLog(string message) =>
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    private sealed class CliWorkflowInteraction(bool assumeYes) : IWorkflowInteraction
    {
        public Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (assumeYes)
            {
                AnsiConsole.MarkupLine($"[yellow]confirmed[/] {Markup.Escape(confirmation.Title)}");
                return Task.FromResult(true);
            }

            var details = confirmation.Details.Count == 0
                ? ""
                : Environment.NewLine + string.Join(
                    Environment.NewLine,
                    confirmation.Details.Take(10).Select(detail => $"  {detail}"));
            AnsiConsole.Write(new Panel(Markup.Escape(confirmation.Description + details))
                .Header($"[bold yellow]{Markup.Escape(confirmation.Title)}[/]")
                .Border(BoxBorder.Rounded));

            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[yellow]Declined side-effecting workflow step because input is redirected. Pass --yes to approve unattended runs.[/]");
                return Task.FromResult(false);
            }

            return Task.FromResult(AnsiConsole.Confirm("Continue?", defaultValue: false));
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Snapshot — the command loop can null/dispose the source while this
        // handler runs on its own thread.
        var active = _activeCommand;
        if (active is null)
            return;

        // Swallow only the FIRST press (e.Cancel stays false on later ones):
        // a second Ctrl+C while a command is stuck in non-cancelable work
        // must keep its default behavior of terminating the process.
        if (active.IsCancellationRequested)
            return;

        e.Cancel = true;
        try
        {
            active.Cancel();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Canceling current command...[/]");
        }
        catch (ObjectDisposedException)
        {
            // The command finished between the snapshot and the cancel.
        }
    }
}
