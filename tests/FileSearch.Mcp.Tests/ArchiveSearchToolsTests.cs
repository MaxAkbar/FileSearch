using System.IO.Compression;
using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Mcp;
using FileSearch.Mcp.Tools;

namespace FileSearch.Mcp.Tests;

public sealed class ArchiveSearchToolsTests
{
    [Fact]
    public async Task LiveAndIndexedToolsRetainExactArchiveAndMemberLocations()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "FileSearch.Mcp.Archive", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, "allowed");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "documents.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("folder/record.txt").Open());
                writer.Write("intro\nneedle archive\n");
            }
            byte[] original = await File.ReadAllBytesAsync(path, Token);
            var walker = new FileWalker();
            var registry = new ExtractorRegistry([new ZipExtractor()]);
            using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(sandbox, "index.db") }, walker, registry);
            await index.BuildOrRefreshAsync(new IndexRequest(root, new WalkerOptions()), Token);
            var tools = new SearchTools(new Searcher(walker, registry), index, index, new QueryFactory(),
                new RootPolicy(new McpServerRails { ExplicitRoots = [root] }, index));
            string[] responses =
            [
                await tools.SearchContentAsync("needle", [root], includeExtensions: [".zip"], cancellationToken: Token),
                await tools.SearchIndexAsync("needle", [root], includeExtensions: [".zip"], cancellationToken: Token),
            ];
            foreach (string response in responses)
            {
                using var json = JsonDocument.Parse(response);
                var hit = Assert.Single(json.RootElement.GetProperty("hits").EnumerateArray());
                Assert.Equal(path, hit.GetProperty("path").GetString());
                var locator = hit.GetProperty("locator");
                Assert.Equal("folder/record.txt", locator.GetProperty("archiveMember").GetString());
                Assert.False(string.IsNullOrWhiteSpace(locator.GetProperty("archiveFingerprint").GetString()));
                Assert.Equal(2, locator.GetProperty("archiveContentAnchor").GetProperty("line").GetInt32());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
