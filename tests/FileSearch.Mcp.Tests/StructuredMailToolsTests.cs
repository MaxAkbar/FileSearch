using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Mcp;
using FileSearch.Mcp.Tools;
using ModelContextProtocol;

namespace FileSearch.Mcp.Tests;

public sealed class StructuredMailToolsTests
{
    [Fact]
    public async Task LiveUnifiedMailSearchShapesRealMetadataAndEnforcesAllowedRoots()
    {
        var token = TestContext.Current.CancellationToken;
        string fixture = Path.Combine(Path.GetTempPath(), "FileSearch.Mcp.Mail", Guid.NewGuid().ToString("N"));
        string allowed = Path.Combine(fixture, "allowed");
        string outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        try
        {
            string matchingPath = Path.Combine(allowed, "review.eml");
            string spoofPath = Path.Combine(allowed, "body-spoof.eml");
            const string matching = """
                From: Alice <alice@example.test>
                To: Bob <bob@example.test>
                Cc: Audit <audit@example.test>
                Subject: Annual Review
                Date: Sat, 07 Nov 2026 10:30:00 -0800
                Content-Type: text/plain; charset=utf-8

                Budget invoice due tomorrow.
                """;
            const string spoof = """
                From: Mallory <mallory@example.test>
                To: Bob <bob@example.test>
                Subject: Unrelated note
                Date: Sat, 07 Nov 2026 10:30:00 -0800
                Content-Type: text/plain; charset=utf-8

                From: Alice <alice@example.test>
                Subject: Annual Review
                Budget invoice due tomorrow.
                """;
            await File.WriteAllTextAsync(matchingPath, matching, token);
            await File.WriteAllTextAsync(spoofPath, spoof, token);
            await File.WriteAllTextAsync(Path.Combine(outside, "private.eml"), matching, token);

            var walker = new FileWalker();
            var registry = new ExtractorRegistry([new EmlExtractor()]);
            var live = new Searcher(walker, registry);
            string databasePath = Path.Combine(fixture, "unused-index.db");
            using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = databasePath }, walker, registry);
            var policy = new RootPolicy(new McpServerRails { ExplicitRoots = [allowed] }, index);
            var tools = new SearchTools(live, index, index, new QueryFactory(), policy);

            using var response = JsonDocument.Parse(await tools.SearchContentAsync(
                "from:alice subject:\"Annual Review\" sent:2026-11-07 invoice",
                [allowed], mode: "unified", includeExtensions: [".eml"], cancellationToken: token));
            var result = response.RootElement;
            Assert.Equal("Unified", result.GetProperty("mode").GetString());
            Assert.Equal("live", result.GetProperty("route").GetString());
            Assert.False(result.GetProperty("truncated").GetBoolean());
            Assert.False(result.GetProperty("timedOut").GetBoolean());
            Assert.Equal(1, result.GetProperty("returned").GetInt32());
            var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
            Assert.Equal(matchingPath, hit.GetProperty("path").GetString());
            Assert.Equal("Budget invoice due tomorrow.", hit.GetProperty("line").GetString());
            Assert.Equal("Content", hit.GetProperty("kind").GetString());
            var mail = hit.GetProperty("mailMessage");
            Assert.Equal("eml", mail.GetProperty("id").GetString());
            Assert.Equal("Annual Review", mail.GetProperty("subject").GetString());
            Assert.Equal("Alice <alice@example.test>", mail.GetProperty("from").GetString());
            Assert.Equal("Audit <audit@example.test>", mail.GetProperty("cc").GetString());
            Assert.Equal(new DateTime(2026, 11, 7, 18, 30, 0, DateTimeKind.Utc), mail.GetProperty("sentUtc").GetDateTime());

            // A field predicate cannot be satisfied by a forged header in the
            // body, even when the filename and body text otherwise qualify.
            using var rejectedSpoof = JsonDocument.Parse(await tools.SearchContentAsync(
                "from:alice subject:\"Annual Review\" invoice", [allowed], mode: "unified",
                includeGlobs: ["body-spoof.eml"], cancellationToken: token));
            Assert.Equal(0, rejectedSpoof.RootElement.GetProperty("returned").GetInt32());
            var denied = await Assert.ThrowsAsync<McpException>(() => tools.SearchContentAsync(
                "from:alice invoice", [outside], mode: "unified", cancellationToken: token));
            Assert.Contains("outside the allowed roots", denied.Message, StringComparison.Ordinal);
            Assert.Equal(matching, await File.ReadAllTextAsync(matchingPath, token));
            Assert.Equal(spoof, await File.ReadAllTextAsync(spoofPath, token));
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }
}
