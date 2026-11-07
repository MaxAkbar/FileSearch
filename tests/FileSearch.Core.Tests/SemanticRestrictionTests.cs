using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class SemanticRestrictionTests : IAsyncDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "FileSearch.SemanticRestrictions", Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingModelInfo Model = new("restriction-test", "1", 2);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public SemanticRestrictionTests() => Directory.CreateDirectory(_work);
    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_work, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, CancellationToken.None); }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestrictionIsAppliedBeforeFileAndChunkCandidateCaps(bool fileVectors, bool persistent)
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        for (int i = 0; i < 55; i++) File.WriteAllText(Path.Combine(root, $"decoy-{i:D2}.txt"), "unrelated high-scoring passage");
        string target = Path.Combine(root, "target.cs"); File.WriteAllText(target, "database recovery implementation");
        using var index = await BuildAsync(root);
        var vectors = await VectorsAsync(index, root, fileVectors, persistent);
        var matches = await SearchAsync(index, vectors, root, "ext:cs semantic:\"database recovery\"");
        var match = Assert.Single(matches);
        Assert.Equal(target, match.Path);
        Assert.Equal("database recovery implementation", match.DisplayText);
        Assert.Equal(target, await index.GetFilePathAsync(match.FileId!.Value, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyEligibilityCannotFallBackToUnrestrictedChunks(bool fileVectors)
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "report.txt"), "database recovery");
        using var index = await BuildAsync(root);
        var vectors = await VectorsAsync(index, root, fileVectors, persistent: true);
        Assert.Empty(await SearchAsync(index, vectors, root, "ext:cs semantic:\"database recovery\""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedFolderScopeFindsVectorsOwnedByItsParentIndex(bool fileVectors)
    {
        string root = Path.Combine(_work, "docs"), nested = Path.Combine(root, "client"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "outside.txt"), "database recovery outside requested scope");
        string target = Path.Combine(nested, "target.cs"); File.WriteAllText(target, "database recovery implementation");
        using var index = await BuildAsync(root);
        var vectors = await VectorsAsync(index, root, fileVectors, persistent: true);
        Assert.Equal(target, Assert.Single(await SearchAsync(index, vectors, nested, "semantic:\"database recovery\"")).Path);
    }

    [Fact]
    public async Task SupersededFileVectorsCannotReturnOldIndexedContent()
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        string target = Path.Combine(root, "target.cs"); File.WriteAllText(target, "database recovery old");
        using var index = await BuildAsync(root);
        var vectors = await VectorsAsync(index, root, fileVectors: true, persistent: true);
        Assert.Single(await SearchAsync(index, vectors, root, "semantic:\"database recovery\""));
        File.WriteAllText(target, "replacement passage with different indexed content");
        await index.UpsertFileAsync(root, target, new WalkerOptions(), Token);
        Assert.Empty(await SearchAsync(index, vectors, root, "semantic:\"database recovery\""));
        await index.ClearAsync(root, Token);
        Assert.Empty(await SearchAsync(index, vectors, root, "semantic:\"database recovery\""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEmptyAndInvalidFileIdSetsMatchNothing(bool persistent)
    {
        IVectorIndex vectors = persistent ? new FileVectorIndex(new VectorIndexOptions { IndexPath = Path.Combine(_work, "vectors.json") }) : new InMemoryVectorIndex();
        await vectors.UpsertAsync([new VectorDocument("chunk", VectorDocumentKind.ContentChunk, 1, [1L], new float[] { 1, 0 }, Model, "1", "hash")], Token);
        Assert.Single(await vectors.SearchAsync(new float[] { 1, 0 }, 10, Token));
        Assert.Empty(await vectors.SearchAsync(new float[] { 1, 0 }, 10, Token, fileIds: []));
        Assert.Empty(await vectors.SearchAsync(new float[] { 1, 0 }, 10, Token, fileIds: [0L, -1L]));
    }

    [Theory]
    [InlineData("type:code")]
    [InlineData("ext:cs")]
    [InlineData("name:target")]
    [InlineData("path:client")]
    [InlineData("folder:client")]
    [InlineData("size:>10b")]
    [InlineData("modified:2026")]
    public async Task FileEligibilityHonorsUnifiedFiltersAndHiddenParents(string filter)
    {
        string root = Path.Combine(_work, "docs"), client = Path.Combine(root, "client"), secret = Path.Combine(client, "secret");
        Directory.CreateDirectory(secret);
        string target = Path.Combine(client, "target.cs"); File.WriteAllText(target, "database recovery implementation");
        string decoy = Path.Combine(root, "decoy.txt"); File.WriteAllText(decoy, "x"); File.SetLastWriteTimeUtc(decoy, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(target, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        string hidden = Path.Combine(client, "hidden.cs"); File.Copy(target, hidden); File.SetAttributes(hidden, FileAttributes.Hidden);
        File.Copy(target, Path.Combine(secret, "target.cs")); File.SetAttributes(secret, FileAttributes.Hidden);
        using var index = await BuildAsync(root, walk: new WalkerOptions { IncludeHidden = true });
        var vectors = await VectorsAsync(index, root, fileVectors: true, persistent: true);
        Assert.Equal(target, Assert.Single(await SearchAsync(index, vectors, root, filter + " semantic:\"database recovery\"")).Path);
    }

    [Fact]
    public async Task WalkerFiltersAndOverlappingRootsConstrainTheEligibleFileIds()
    {
        string root = Path.Combine(_work, "docs"), child = Path.Combine(root, "client"), excluded = Path.Combine(child, "excluded");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(root, "root.cs"), "database recovery");
        string target = Path.Combine(child, "target.cs"); File.WriteAllText(target, "database recovery");
        File.WriteAllText(Path.Combine(child, "skip.cs"), "database recovery");
        File.WriteAllText(Path.Combine(child, "small.cs"), "x");
        File.WriteAllText(Path.Combine(child, "other.txt"), "database recovery");
        File.WriteAllText(Path.Combine(excluded, "nested.cs"), "database recovery");
        using var index = await BuildAsync(root);
        var expression = new QueryFactory().Build("database recovery", QueryMode.Semantic, false);
        var walk = new WalkerOptions
        {
            IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
            ExcludeGlobs = ["skip*"],
            ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "excluded" },
            IncludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "client" },
            MinFileSizeBytes = 10,
        };
        var request = new SearchRequest(expression, [root, child], walk, UseIndex: true);
        Assert.Equal(target, Assert.Single(await index.GetEligibleFilesAsync(request, Token)).Path);
        var direct = await index.GetEligibleFilesAsync(request with { Roots = [child], WalkerOptions = walk with { Recursive = false } }, Token);
        Assert.Equal(target, Assert.Single(direct).Path);
        Assert.Empty(await index.GetEligibleFilesAsync(request with { Roots = [] }, Token));
        Assert.Empty(await index.GetEligibleFilesAsync(request with { Roots = [root + "-sibling"] }, Token));
        await index.BuildOrRefreshAsync(new IndexRequest(child, new WalkerOptions()), Token);
        var eligible = await index.GetEligibleFilesAsync(request with { Roots = [child] }, Token);
        Assert.Contains(eligible, file => file.Path == target);
        Assert.All(eligible, file => Assert.StartsWith(child + Path.DirectorySeparatorChar, file.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrphanedChunkCannotPublishAnOutdatedSourceLocation()
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        string target = Path.Combine(root, "target.cs"); File.WriteAllText(target, "current indexed content");
        using var index = await BuildAsync(root);
        long id = (await index.GetFileIdAsync(root, target, Token))!.Value;
        var vectors = new InMemoryVectorIndex();
        await vectors.UpsertAsync([new VectorDocument("orphan", VectorDocumentKind.ContentChunk, id, [1_000_000L], new float[] { 1, 0 },
            Model, "1", "old-hash", new SourceLocator(StartLine: 9999), root, target)], Token);
        Assert.Empty(await SearchAsync(index, vectors, root, "semantic:recovery"));
    }

    [Fact]
    public async Task SemanticFileLimitPreservesTwoMessagesInOneStore()
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "mail.pst"); File.WriteAllText(path, "controlled extractor fixture");
        using var index = await BuildAsync(root, new MessageExtractor());
        var vectors = await VectorsAsync(index, root, fileVectors: true, persistent: true);
        var request = new SearchRequest(new QueryFactory().Build("recovery", QueryMode.Semantic, false), [root], new WalkerOptions(),
            UseIndex: true, Mode: QueryMode.Semantic, SemanticOptions: new SemanticSearchOptions(0, 1));
        var provider = new SemanticCandidateProvider(new Embedder(), vectors, index);
        var candidates = new List<SearchCandidate>();
        var plan = new QueryPlanner().CreatePlan(request);
        await foreach (var candidate in provider.FindAsync(plan, Token)) candidates.Add(candidate);
        var results = new WeightedResultFusion().Fuse(plan, candidates);
        Assert.Equal(2, results.Count);
        Assert.Equal(new[] { "first", "second" }, results.Select(result => result.BestCandidate!.Locator!.MailMessage!.Id));
        Assert.All(results, result => Assert.Equal(path, result.Path));
    }

    [Theory]
    [InlineData(FileSearch.Core.Databases.DatabaseKind.Sqlite)]
    [InlineData(FileSearch.Core.Databases.DatabaseKind.CSharpDb)]
    public async Task SemanticDatabaseResultRetainsItsExactRecordAfterFusion(FileSearch.Core.Databases.DatabaseKind kind)
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        string path = await DatabaseTestFiles.CreateAsync(root, "records.db", kind, 3, Token);
        using var index = await BuildAsync(root, new DatabaseExtractor());
        long id = (await index.GetFileIdAsync(root, path, Token))!.Value;
        var unit = Assert.Single(await index.GetContentUnitsForFileAsync(id, Token), unit => unit.Locator.Database is { RowNumber: 3, Column: "Notes" });
        var hit = await FusedHitAsync(index, root, unit);
        Assert.Equal(path, hit.Path);
        Assert.Equal(unit.Locator.Database, hit.Database);
        Assert.Equal(kind, hit.Database!.Engine);
        Assert.Equal(DatabaseTestFiles.TableName, hit.Database.Table);
    }

    [Fact]
    public async Task SemanticArchiveResultRetainsItsMemberAfterFusion()
    {
        string root = Path.Combine(_work, "docs"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "source.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("folder/evidence.txt").Open()); writer.Write("database recovery");
        }
        using var index = await BuildAsync(root, new ZipExtractor());
        long id = (await index.GetFileIdAsync(root, path, Token))!.Value;
        var unit = Assert.Single(await index.GetContentUnitsForFileAsync(id, Token), unit => unit.Text == "database recovery");
        var hit = await FusedHitAsync(index, root, unit);
        Assert.Equal(path, hit.Path);
        Assert.Equal("folder/evidence.txt", hit.Locator!.ArchiveMember);
    }

    private static async Task<Hit> FusedHitAsync(CSharpDbFileIndex index, string root, ContentUnit unit)
    {
        var vectors = new InMemoryVectorIndex();
        await vectors.UpsertAsync([new VectorDocument("selected", VectorDocumentKind.ContentChunk, unit.FileId, [unit.Id], new float[] { 1, 0 }, Model, "1", unit.ContentHash, unit.Locator, root)], Token);
        var provider = new SemanticCandidateProvider(new Embedder(), vectors, index);
        var pipeline = new HybridRetrievalPipeline(new QueryPlanner(), [provider], new WeightedResultFusion(), new LocalHeuristicReranker());
        var request = new SearchRequest(new QueryFactory().Build("database recovery", QueryMode.Semantic, false), [root], new WalkerOptions(), UseIndex: true, Mode: QueryMode.Semantic);
        var hits = new List<Hit>();
        await foreach (var hit in new HybridSearcher(pipeline).SearchAsync(request, Token)) hits.Add(hit);
        return Assert.Single(hits);
    }

    private async Task<CSharpDbFileIndex> BuildAsync(string root, ITextExtractor? extractor = null, WalkerOptions? walk = null)
    {
        var plain = new PlainTextExtractor();
        var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(_work, "index.db") }, new FileWalker(), new ExtractorRegistry(extractor is null ? [plain] : [extractor, plain], plain));
        await index.BuildOrRefreshAsync(new IndexRequest(root, walk ?? new WalkerOptions()), Token);
        return index;
    }

    private async Task<IVectorIndex> VectorsAsync(CSharpDbFileIndex index, string root, bool fileVectors, bool persistent)
    {
        IVectorIndex vectors = persistent ? new FileVectorIndex(new VectorIndexOptions { IndexPath = Path.Combine(_work, "vectors.json") }) : new InMemoryVectorIndex();
        var documents = new List<VectorDocument>();
        foreach (long fileId in await index.GetFileIdsForRootAsync(root, Token))
        {
            string path = (await index.GetFilePathAsync(fileId, Token))!;
            var units = await index.GetContentUnitsForFileAsync(fileId, Token);
            float[] vector = Path.GetExtension(path) == ".cs" ? [0.8f, 0.6f] : [1, 0];
            if (fileVectors) documents.Add(new VectorDocument($"file-{fileId}", VectorDocumentKind.File, fileId, units.Select(unit => unit.Id).ToArray(), vector, Model, "1", "hash", root: root, filePath: path));
            foreach (var unit in units) documents.Add(new VectorDocument($"chunk-{unit.Id}", VectorDocumentKind.ContentChunk, fileId, [unit.Id], vector, Model, "1", "hash", unit.Locator, root, path));
        }
        await vectors.UpsertAsync(documents, Token);
        return vectors;
    }

    private static async Task<List<SearchCandidate>> SearchAsync(CSharpDbFileIndex index, IVectorIndex vectors, string root, string text)
    {
        var request = new SearchRequest(new UnifiedQueryParser().Parse(text), [root], new WalkerOptions(), UseIndex: true, Mode: QueryMode.Unified);
        var provider = new SemanticCandidateProvider(new Embedder(), vectors, index);
        var matches = new List<SearchCandidate>();
        await foreach (var candidate in provider.FindAsync(new QueryPlanner().CreatePlan(request), Token)) matches.Add(candidate);
        return matches;
    }

    private sealed class Embedder : ITextEmbedder
    {
        public Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(TextEmbedderAvailability.Available);
        public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(new TextEmbedding(new float[] { 1, 0 }, Model));
    }

    private sealed class MessageExtractor : ITextExtractor
    {
        public string ExtractorId => "message-fixture";
        public string ExtractorVersion => "1";
        public IReadOnlyCollection<string> SupportedExtensions => [".pst"];
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            int line = 0;
            foreach (string id in new[] { "first", "second" })
            {
                var mail = new MailMessageMetadata(id, "Same subject", "sender", "to", "", null, "Inbox", "unchanged-store");
                yield return new TextLine(++line, "database recovery " + id, new SourceAnchor(SourceAnchorKind.Text, id, MailMessage: mail));
            }
        }
    }
}
