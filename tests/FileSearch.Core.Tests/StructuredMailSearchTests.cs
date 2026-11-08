using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class StructuredMailSearchTests
{
    [Fact]
    public async Task MessageFieldsAndBooleanQueriesAgreeAcrossLiveAndIndexedSearch()
    {
        using var files = new SyntheticMailFiles();
        await files.BuildAsync();
        (string Query, string[] Ids)[] cases =
        [
            ("from:alice", ["first", "third"]),
            ("to:carol", ["first"]),
            ("cc:audit", ["first"]),
            ("recipient:audit", ["first"]),
            ("subject:\"Project Approval\"", ["first", "second"]),
            ("from:alice budget", ["first", "third"]),
            ("from:alice AND subject:\"Project Approval\"", ["first"]),
            ("from:alice OR from:bob", ["first", "second", "third"]),
            ("NOT from:alice", ["second"]),
            ("subject:\"Project Approval\" AND NOT from:alice", ["second"]),
            ("budget OR from:bob", ["first", "second", "third"]),
            ("sent:2026-02-03", ["first"]),
            ("sent:2026-01-01..2026-12-31", ["first"]),
            ("from:alice type:email", ["first", "third"]),
            ("from:alice name:missing", []),
            ("from:mallory budget", []),
        ];

        foreach (var test in cases)
        {
            var request = files.Request(test.Query);
            var live = await HitsAsync(files.Live.SearchAsync(request, Token));
            var indexed = await HitsAsync(files.Index.SearchAsync(request with { UseIndex = true }, Token));
            Assert.Equal(test.Ids, live.Select(hit => hit.MailMessage!.Id).OrderBy(id => id));
            Assert.Equal(test.Ids, indexed.Select(hit => hit.MailMessage!.Id).OrderBy(id => id));
            Assert.Equal(live.OrderBy(hit => hit.ResultKey).Select(Identity), indexed.OrderBy(hit => hit.ResultKey).Select(Identity));
            Assert.All(live, hit => Assert.Equal(HitKind.Content, hit.Kind));
            Assert.All(indexed, hit => Assert.Equal(HitRoute.Indexed, hit.Route));
            Assert.All(indexed, hit => Assert.NotNull(hit.Locator!.MailMessage));
        }
    }

    [Fact]
    public async Task MessageSentDatesAndPhysicalFileDatesRemainIndependent()
    {
        using var files = new SyntheticMailFiles();
        await files.BuildAsync();
        (string Query, string[] Ids)[] cases =
        [
            ("from:alice sent:2026-02-03 modified:2026", ["first"]),
            ("from:alice sent:2026-02-03 modified:2025", []),
            ("from:alice size:>1gb", []),
        ];
        foreach (var test in cases)
        {
            var request = files.Request(test.Query);
            Assert.Equal(test.Ids, (await HitsAsync(files.Live.SearchAsync(request, Token))).Select(hit => hit.MailMessage!.Id));
            Assert.Equal(test.Ids, (await HitsAsync(files.Index.SearchAsync(request with { UseIndex = true }, Token))).Select(hit => hit.MailMessage!.Id));
        }

        var excluded = files.Request("from:alice") with
        {
            WalkerOptions = files.Walk with { ModifiedBeforeUtc = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc) },
        };
        Assert.Empty(await HitsAsync(files.Live.SearchAsync(excluded, Token)));
        Assert.Empty(await HitsAsync(files.Index.SearchAsync(excluded with { UseIndex = true }, Token)));
    }

    [Fact]
    public async Task FieldOnlyQueriesUseLexicalProvidersAndNeverReturnFilenameCandidates()
    {
        using var files = new SyntheticMailFiles();
        await files.BuildAsync();
        var request = files.Request("from:alice");
        var plan = new QueryPlanner().CreatePlan(request);
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Lexical));
        Assert.False(plan.HasEnabledProvider(CandidateProviderKind.Metadata));

        var liveProvider = new LexicalCandidateProvider(files.Live);
        Assert.True((await liveProvider.GetAvailabilityAsync(plan, Token)).IsAvailable);
        var liveCandidates = await CandidatesAsync(liveProvider.FindAsync(plan, Token));
        Assert.Equal(new[] { "first", "third" }, liveCandidates.Select(candidate => candidate.Locator?.MailMessage?.Id ?? candidate.Anchor!.MailMessage!.Id).OrderBy(id => id));

        var indexedPlan = new QueryPlanner().CreatePlan(request with { UseIndex = true });
        var coverage = new IndexCoverageService(files.Index);
        var indexedProvider = new IndexedLexicalCandidateProvider(files.Index, coverage);
        Assert.True((await indexedProvider.GetAvailabilityAsync(indexedPlan, Token)).IsAvailable);
        Assert.Equal(2, (await CandidatesAsync(indexedProvider.FindAsync(indexedPlan, Token))).Count);

        // Force metadata providers into the plan as well, so the providers'
        // own guards are tested independently from planner suppression.
        var metadataPlan = new SearchPlan(request,
            [new SearchProviderPlan(CandidateProviderKind.Metadata, RetrievalLayer.Instant)]);
        Assert.False((await new MetadataCandidateProvider(files.Live).GetAvailabilityAsync(metadataPlan, Token)).IsAvailable);
        Assert.False((await new IndexedMetadataCandidateProvider(files.Index, coverage)
            .GetAvailabilityAsync(metadataPlan with { Request = request with { UseIndex = true } }, Token)).IsAvailable);
        Assert.False(MetadataSearchSpec.TryCreate(request, out _));
    }

    [Fact]
    public async Task MailFieldsWithFilenameTargetsFailClosedAndExplainTheRequiredTarget()
    {
        using var files = new SyntheticMailFiles();
        var statuses = new List<string>();
        var request = files.Request("from:alice") with { SearchTarget = SearchTarget.FileNames, Status = statuses.Add };
        Assert.Empty(await HitsAsync(files.Live.SearchAsync(request, Token)));
        Assert.Contains(statuses, status => status.Contains("Contents", StringComparison.Ordinal));
        var plan = new QueryPlanner().CreatePlan(request);
        Assert.Equal(CandidateProviderKind.None, plan.EnabledProviders);
        Assert.Contains(plan.Explanations, explanation => explanation.Code == "mail-content-target");
    }

    [Fact]
    public async Task IndexedSnippetsKeepMessageContextAndBodyHighlights()
    {
        using var files = new SyntheticMailFiles();
        await files.BuildAsync();
        var request = files.Request("from:alice budget") with { UseIndex = true };
        var hit = (await HitsAsync(files.Index.SearchAsync(request, Token))).Single(hit => hit.MailMessage!.Id == "first");
        Assert.NotEmpty(hit.Highlights);
        var snippet = await new ContentUnitSnippetGenerator(files.Index).GenerateAsync(request, hit, Token);
        Assert.Contains("Budget", snippet.Text, StringComparison.Ordinal);
        Assert.NotEmpty(snippet.Highlights);
        Assert.Equal("first", snippet.Locator!.MailMessage!.Id);
        Assert.DoesNotContain("Subject:", snippet.Text, StringComparison.Ordinal);
        foreach (var unitId in snippet.ContentUnitIds)
            Assert.Equal("first", (await files.Index.GetContentUnitAsync(unitId, Token))!.Locator.MailMessage!.Id);
    }

    [Theory]
    [InlineData("message.msg")]
    [InlineData("bodies.pst")]
    [InlineData("sample.ost")]
    public async Task RealOutlookFilesPreserveStructuredFieldsAndSourceIdentity(string name)
    {
        using var files = new OutlookTestFiles();
        var beforeBytes = await File.ReadAllBytesAsync(files.PathFor(name), Token);
        var walker = new FileWalker();
        var registry = new ExtractorRegistry([new MsgExtractor(), new OutlookStoreExtractor()]);
        var options = new SearchOptions { MaxHitsPerFile = 1 };
        var walk = new WalkerOptions { IncludeGlobs = [name] };
        var live = new Searcher(walker, registry, options);
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = files.PathFor("structured-index.db") }, walker, registry, options);
        var extractor = registry.GetFor(files.PathFor(name))!;
        var messages = new List<MailMessageMetadata>();
        await foreach (var line in extractor.ExtractAsync(files.PathFor(name), Token))
            if (!messages.Any(message => message.Id == line.Anchor!.MailMessage!.Id)) messages.Add(line.Anchor!.MailMessage!);
        var selected = messages.First(message => !string.IsNullOrEmpty(message.Subject));
        string escaped = selected.Subject.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var expression = new UnifiedQueryParser().Parse($"subject:\"{escaped}\"");
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, walk), Token);
        var request = new SearchRequest(expression, [files.Root], walk, Mode: QueryMode.Unified);
        var liveHits = await HitsAsync(live.SearchAsync(request, Token));
        var indexHits = await HitsAsync(index.SearchAsync(request with { UseIndex = true }, Token));
        var expected = messages.Where(message => message.Subject.Contains(selected.Subject, StringComparison.OrdinalIgnoreCase)).Select(message => message.Id).OrderBy(id => id);
        Assert.Equal(expected, liveHits.Select(hit => hit.MailMessage!.Id).OrderBy(id => id));
        Assert.Equal(liveHits.OrderBy(hit => hit.ResultKey).Select(Identity), indexHits.OrderBy(hit => hit.ResultKey).Select(Identity));
        Assert.All(indexHits, hit => Assert.Contains(selected.Subject, hit.MailMessage!.Subject, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(indexHits.Count, indexHits.Select(hit => hit.ResultKey).Distinct().Count());
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(files.PathFor(name), Token));
    }

    [Fact]
    public async Task OlderMailMetadataRequiresRefreshAndReextractsAnUnchangedMessage()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("message.msg");
        var sourceBytes = await File.ReadAllBytesAsync(path, Token);
        var modified = File.GetLastWriteTimeUtc(path);
        var walker = new FileWalker();
        var walk = new WalkerOptions { IncludeGlobs = ["message.msg"] };
        var options = new FileIndexOptions { DatabasePath = files.PathFor("upgrade-index.db") };
        var searchOptions = new SearchOptions { MaxHitsPerFile = 1 };
        var request = new SearchRequest(new UnifiedQueryParser().Parse("subject:\"MIME registry\""), [files.Root], walk, UseIndex: true);
        using (var oldIndex = new CSharpDbFileIndex(options, walker, new ExtractorRegistry([new LegacyMsgExtractor()]), searchOptions))
        {
            await oldIndex.BuildOrRefreshAsync(new IndexRequest(files.Root, walk), Token);
            Assert.Null(Assert.Single(await HitsAsync(oldIndex.SearchAsync(request, Token))).MailMessage!.SentUtc);
        }

        using var updated = new CSharpDbFileIndex(options, walker, new ExtractorRegistry([new MsgExtractor()]), searchOptions);
        var stale = await updated.GetCoverageAsync(request, Token);
        Assert.Equal(IndexCoverageStatus.Incompatible, stale.Status);
        Assert.Contains("extractor versions", stale.Message, StringComparison.OrdinalIgnoreCase);
        await updated.BuildOrRefreshAsync(new IndexRequest(files.Root, walk), Token);
        Assert.Equal(IndexCoverageStatus.Covered, (await updated.GetCoverageAsync(request, Token)).Status);
        var refreshed = Assert.Single(await HitsAsync(updated.SearchAsync(request, Token)));
        Assert.NotNull(refreshed.MailMessage!.SentUtc);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static object Identity(Hit hit) => new { hit.Path, hit.ResultKey, hit.LineNumber, hit.LineContent, hit.MailMessage };

    private static async Task<List<Hit>> HitsAsync(IAsyncEnumerable<Hit> source)
    {
        var hits = new List<Hit>();
        await foreach (var hit in source) hits.Add(hit);
        return hits;
    }

    private static async Task<List<SearchCandidate>> CandidatesAsync(IAsyncEnumerable<SearchCandidate> source)
    {
        var candidates = new List<SearchCandidate>();
        await foreach (var candidate in source) candidates.Add(candidate);
        return candidates;
    }

    private sealed class SyntheticMailFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FileSearch.StructuredMail", Guid.NewGuid().ToString("N"));
        public WalkerOptions Walk { get; } = new() { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pst", ".txt" } };
        public Searcher Live { get; }
        public CSharpDbFileIndex Index { get; }

        public SyntheticMailFiles()
        {
            Directory.CreateDirectory(Root);
            var store = Path.Combine(Root, "alice-budget-report.pst");
            File.WriteAllText(store, "synthetic store fixture");
            File.SetLastWriteTimeUtc(store, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(Root, "alice-budget.txt"), "Budget body common from:alice subject:Project Approval");
            var walker = new FileWalker();
            var registry = new ExtractorRegistry([new SyntheticMailExtractor(), new PlainTextExtractor()]);
            var options = new SearchOptions { MaxHitsPerFile = 1 };
            Live = new Searcher(walker, registry, options);
            Index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(Root, "index.db") }, walker, registry, options);
        }

        public Task BuildAsync() => Index.BuildOrRefreshAsync(new IndexRequest(Root, Walk), Token);
        public SearchRequest Request(string text) => new(new UnifiedQueryParser().Parse(text), [Root], Walk, RawQuery: text, Mode: QueryMode.Unified);

        public void Dispose()
        {
            Index.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class SyntheticMailExtractor : ITextExtractor
    {
        public string ExtractorId => "tests.structured-mail";
        public string ExtractorVersion => "1";
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pst"];
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            MailMessageMetadata[] messages =
            [
                new("first", "Project Approval", "Alice <alice@example.test>", "Bob <bob@example.test>; Carol <carol@example.test>", "Audit <audit@example.test>", new DateTime(2026, 2, 3, 20, 0, 0, DateTimeKind.Utc), "Inbox", "unchanged-store", 1)
                { SentUtc = new DateTime(2026, 2, 3, 20, 0, 0, DateTimeKind.Utc) },
                new("second", "Project Approval", "Bob <bob@example.test>", "Alice <alice@example.test>", "", new DateTime(2025, 2, 3, 20, 0, 0, DateTimeKind.Utc), "Sent", "unchanged-store", 4)
                { SentUtc = new DateTime(2025, 2, 3, 20, 0, 0, DateTimeKind.Utc) },
                new("third", "Other proposal", "Alice <alice@example.test>", "Bob <bob@example.test>", "", null, "Inbox", "unchanged-store", 7),
            ];
            int number = 0;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var anchor = new SourceAnchor(SourceAnchorKind.Email, message.Subject, MailMessage: message);
                yield return new TextLine(++number, "Subject: " + message.Subject, anchor);
                yield return new TextLine(++number, "From: " + message.From, anchor);
                yield return new TextLine(++number, "Budget body common from:alice subject:Project Approval", anchor);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class LegacyMsgExtractor : ITextExtractor
    {
        private readonly MsgExtractor _current = new();
        public string ExtractorId => _current.ExtractorId;
        public string ExtractorVersion => "1";
        public IReadOnlyCollection<string> SupportedExtensions => _current.SupportedExtensions;
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var line in _current.ExtractAsync(path, cancellationToken))
                yield return line with { Anchor = line.Anchor! with { MailMessage = line.Anchor.MailMessage! with { SentUtc = null } } };
        }
    }
}
