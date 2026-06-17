using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ZipExtractorTests : IDisposable
{
    private readonly string _path;

    public ZipExtractorTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void SupportedExtensions_IncludesZip()
    {
        var extractor = new ZipExtractor();
        Assert.Contains(".zip", extractor.SupportedExtensions);
    }

    [Fact]
    public async Task Extracts_TextEntries_AndSkipsBinary()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "readme.txt", "hello world\nsecond line\n");
            AddEntry(archive, "data.bin", "binary-extension-skipped");
            AddEntry(archive, "code.cs", "public class Foo { }\n");
        }

        var lines = await ReadAllAsync(new ZipExtractor());

        Assert.Contains(lines, l => l.Content.Contains("=== readme.txt ==="));
        Assert.Contains(lines, l => l.Content == "hello world");
        Assert.Contains(lines, l => l.Content.Contains("=== code.cs ==="));
        Assert.Contains(lines, l => l.Content.Contains("public class Foo"));
        Assert.DoesNotContain(lines, l => l.Content.Contains("=== data.bin"));
    }

    [Fact]
    public async Task ReportsSkippedMembersAndReasons()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "readme.txt", "hello world\n");
            AddEntry(archive, "data.bin", "binary-extension-skipped");
            AddEntry(archive, "nested.zip", "nested archive bytes");
            AddEntry(archive, "big.txt", new string('a', 4096));
        }

        var extractor = new ZipExtractor(new ZipArchivePolicy(maxEntryBytes: 1024, maxTotalBytes: 1024 * 1024, maxEntries: 100));
        var (lines, issues) = await ReadAllWithIssuesAsync(extractor);

        Assert.Contains(lines, l => l.Content == "hello world");
        Assert.Contains(issues.Issues, i => i.MemberPath == "data.bin" && i.Code == "archive_member_unsupported_type");
        Assert.Contains(issues.Issues, i => i.MemberPath == "nested.zip" && i.Code == "archive_member_nested_archive_disabled");
        Assert.Contains(issues.Issues, i => i.MemberPath == "big.txt" && i.Code == "archive_member_too_large");
    }

    [Fact]
    public async Task SkipsEntriesWhoseHeaderExceedsPerEntryCap()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "big.txt", new string('a', 4096) + "\nbig needle\n");
            AddEntry(archive, "small.txt", "small needle\n");
        }

        var extractor = new ZipExtractor(maxEntryBytes: 1024, maxTotalBytes: 1024 * 1024, maxEntries: 100);
        var lines = await ReadAllAsync(extractor);

        Assert.DoesNotContain(lines, l => l.Content.Contains("big needle"));
        Assert.Contains(lines, l => l.Content == "small needle");
    }

    [Fact]
    public async Task StopsWhenArchiveDecompressionBudgetIsExhausted()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "first.txt", new string('a', 1100) + "\n");
            AddEntry(archive, "second.txt", "late needle\n");
        }

        // first.txt passes the per-entry cap but eats the whole archive
        // budget mid-entry; second.txt must never be scanned.
        var extractor = new ZipExtractor(maxEntryBytes: 2048, maxTotalBytes: 1000, maxEntries: 100);
        var lines = await ReadAllAsync(extractor);

        Assert.Contains(lines, l => l.Content.Contains("=== first.txt ==="));
        Assert.DoesNotContain(lines, l => l.Content.Contains("late needle"));
    }

    [Fact]
    public async Task ReportsTruncatedMemberWhenArchiveBudgetIsExhaustedMidEntry()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "first.txt", new string('a', 1100) + "\nlate text\n");
        }

        var extractor = new ZipExtractor(new ZipArchivePolicy(maxEntryBytes: 2048, maxTotalBytes: 1000, maxEntries: 100));
        var (_, issues) = await ReadAllWithIssuesAsync(extractor);

        Assert.Contains(issues.Issues, i => i.MemberPath == "first.txt" && i.Code == "archive_member_truncated");
    }

    [Fact]
    public async Task StopsAfterMaxEntries()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "one.txt", "first needle\n");
            AddEntry(archive, "two.txt", "second needle\n");
            AddEntry(archive, "three.txt", "third needle\n");
        }

        var extractor = new ZipExtractor(maxEntryBytes: 1024, maxTotalBytes: 1024 * 1024, maxEntries: 2);
        var lines = await ReadAllAsync(extractor);

        Assert.Contains(lines, l => l.Content == "first needle");
        Assert.Contains(lines, l => l.Content == "second needle");
        Assert.DoesNotContain(lines, l => l.Content.Contains("third"));
    }

    [Fact]
    public async Task ReportsEncryptedArchiveMembersFromCentralDirectoryFlags()
    {
        WriteSingleEntryZip(_path, "secret.txt", "secret text\n", flags: 0x0001, compressionMethod: 0);

        var (lines, issues) = await ReadAllWithIssuesAsync(new ZipExtractor());

        Assert.Empty(lines);
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("secret.txt", issue.MemberPath);
        Assert.Equal("archive_member_encrypted", issue.Code);
    }

    [Fact]
    public async Task ReportsUnsupportedCompressionFromCentralDirectoryMetadata()
    {
        WriteSingleEntryZip(_path, "unsupported.txt", "text\n", flags: 0, compressionMethod: 99);

        var (lines, issues) = await ReadAllWithIssuesAsync(new ZipExtractor());

        Assert.Empty(lines);
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("unsupported.txt", issue.MemberPath);
        Assert.Equal("archive_member_unsupported_compression", issue.Code);
    }

    [Fact]
    public void ArchivePolicyKeepsNestedArchiveDepthAtZero()
    {
        var policy = new ZipArchivePolicy();

        Assert.Equal(0, policy.MaxNestedArchiveDepth);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZipArchivePolicy(maxNestedArchiveDepth: 1));
    }

    private async Task<List<TextLine>> ReadAllAsync(ZipExtractor extractor)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);
        return lines;
    }

    private async Task<(List<TextLine> Lines, ListExtractionIssueSink Issues)> ReadAllWithIssuesAsync(ZipExtractor extractor)
    {
        var lines = new List<TextLine>();
        var issues = new ListExtractionIssueSink();
        await foreach (var line in extractor.ExtractAsync(_path, issues, CancellationToken.None))
            lines.Add(line);
        return (lines, issues);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteSingleEntryZip(
        string path,
        string name,
        string content,
        ushort flags,
        ushort compressionMethod)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        using var stream = File.Create(path);
        var localHeaderOffset = stream.Position;

        WriteUInt32(stream, 0x04034b50);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, compressionMethod);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, (uint)contentBytes.Length);
        WriteUInt32(stream, (uint)contentBytes.Length);
        WriteUInt16(stream, (ushort)nameBytes.Length);
        WriteUInt16(stream, 0);
        stream.Write(nameBytes);
        stream.Write(contentBytes);

        var centralDirectoryOffset = stream.Position;
        WriteUInt32(stream, 0x02014b50);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, compressionMethod);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, (uint)contentBytes.Length);
        WriteUInt32(stream, (uint)contentBytes.Length);
        WriteUInt16(stream, (ushort)nameBytes.Length);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, (uint)localHeaderOffset);
        stream.Write(nameBytes);

        var centralDirectorySize = stream.Position - centralDirectoryOffset;
        WriteUInt32(stream, 0x06054b50);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, (uint)centralDirectorySize);
        WriteUInt32(stream, (uint)centralDirectoryOffset);
        WriteUInt16(stream, 0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
