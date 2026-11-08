using System.IO.Compression;
using System.Security.Cryptography;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ArchiveMemberIdentityTests : IDisposable
{
    [Fact]
    public void MemberNameMarkersSelectTheMemberWithoutInventingAnInnerTextHit()
    {
        var anchor = new SourceAnchor(SourceAnchorKind.Archive, "archive member needle.txt", MemberPath: "needle.txt");
        var hit = new Hit("bundle.zip", 100, "=== needle.txt ===", [new MatchSpan(4, 6)], Anchor: anchor,
            Locator: SourceLocator.FromAnchor(anchor, 100));
        Assert.Null(ArchivePreviewService.CreateMemberHit("needle.txt", "unused.txt", hit));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.ArchiveIdentity", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ArchiveMemberIdentityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task DuplicateExactNamesAreExcludedAndNeverResolveToTheFirstEntry()
    {
        string path = CreateArchive(("duplicate.txt", "first"), ("duplicate.txt", "second"), ("unique.txt", "valid"));
        var preview = await ArchivePreviewService.LoadAsync(path, null, null, Token);
        Assert.Equal("unique.txt", Assert.Single(preview.Parts!).Id);
        Assert.Contains("ambiguous", preview.Summary);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.LoadAsync(path, null, "duplicate.txt", Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.OpenMemberAsync(path, "duplicate.txt", Token));
    }

    [Fact]
    public async Task CaseDistinctNamesRemainExactAndMissingSelectionNeverFallsBack()
    {
        string path = CreateArchive(("Report.txt", "upper"), ("report.txt", "lower"));
        Assert.Equal(2, (await ArchivePreviewService.LoadAsync(path, null, null, Token)).Parts!.Count);
        using (var upper = await ArchivePreviewService.OpenMemberAsync(path, "Report.txt", Token))
            Assert.Equal("upper", await File.ReadAllTextAsync(upper.Path, Token));
        using (var lower = await ArchivePreviewService.OpenMemberAsync(path, "report.txt", Token))
            Assert.Equal("lower", await File.ReadAllTextAsync(lower.Path, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.LoadAsync(path, null, "REPORT.txt", Token));
        var missing = new Hit(path, 1, "missing", [], Locator: new SourceLocator(ArchiveMember: "removed.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.LoadAsync(path, missing, null, Token));
    }

    [Fact]
    public async Task ChangedArchiveRejectsOldHitBeforePreviewOrTemporaryExtraction()
    {
        string path = CreateArchive(("member.txt", "original"));
        string before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, Token)));
        var fingerprint = ArchivePreviewService.GetFingerprint(path);
        var hit = new Hit(path, 1, "original", [], Anchor: new SourceAnchor(SourceAnchorKind.Archive,
            "member.txt", MemberPath: "member.txt", ArchiveFingerprint: fingerprint));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.LoadAsync(path, hit, null, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.OpenMemberAsync(path, "member.txt", Token, fingerprint));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, Token))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberHitRestoresInnerAnchorAndLocalLineWithoutLeakingOuterIdentity(bool indexed)
    {
        string path = CreateArchive(("folder/report.txt", "first\nneedle\nlast"));
        var inner = new SourceAnchor(SourceAnchorKind.Text, "line 2", Line: 2);
        var anchor = new SourceAnchor(SourceAnchorKind.Archive, "archive member folder/report.txt", Line: 2,
            MemberPath: "folder/report.txt", ArchiveFingerprint: ArchivePreviewService.GetFingerprint(path), ArchiveContentAnchor: inner);
        var locator = SourceLocator.FromAnchor(anchor, 700);
        var hit = new Hit(path, 700, "needle", [new MatchSpan(0, 6)], Anchor: indexed ? null : anchor,
            Locator: indexed ? locator : null, Snippet: indexed ? new SearchSnippet("needle", locator: locator) : null);
        using var lease = await ArchivePreviewService.OpenMemberAsync(path, "folder/report.txt", Token);
        var child = ArchivePreviewService.CreateMemberHit("folder/report.txt", lease.Path, hit);
        Assert.NotNull(child);
        Assert.Equal(lease.Path, child.Path);
        Assert.Equal(2, child.LineNumber);
        Assert.Equal(inner, child.Anchor);
        Assert.Null(child.Locator!.ArchiveMember);
        Assert.Null(child.Locator.ArchiveFingerprint);
        Assert.Equal(hit.Highlights, child.Highlights);
        Assert.Null(ArchivePreviewService.CreateMemberHit("another.txt", lease.Path, hit));
    }

    [Fact]
    public async Task PdfMemberUsesWholeMatchingPageAndKeepsOuterSourceUnchanged()
    {
        string pdf = Path.Combine(_root, "pages.pdf");
        DocumentPreviewServiceTests.WritePdf(pdf);
        string path = Path.Combine(_root, "pdf.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntryFromFile(pdf, "reports/pages.pdf");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path, Token));
        var inner = SourceAnchor.PdfPage(2);
        var outer = new SourceAnchor(SourceAnchorKind.Archive, "reports/pages.pdf", Line: 3, Page: 2,
            MemberPath: "reports/pages.pdf", ArchiveFingerprint: ArchivePreviewService.GetFingerprint(path), ArchiveContentAnchor: inner);
        var hit = new Hit(path, 999, "matching page", [], Anchor: outer);
        string owned;
        using (var lease = await ArchivePreviewService.OpenMemberAsync(path, "reports/pages.pdf", Token, outer.ArchiveFingerprint))
        {
            owned = lease.Path;
            var child = ArchivePreviewService.CreateMemberHit("reports/pages.pdf", owned, hit)!;
            var preview = await DocumentPreviewService.LoadAsync(owned, Token, child,
                page: DocumentPreviewService.FindInitialPdfPage([], child));
            Assert.Equal(2, preview.Page);
            Assert.Equal(2, preview.PageCount);
            Assert.NotNull(preview.Image);
            Assert.Equal("archive member reports/pages.pdf · page 2", SourceLocationFormatter.Format(outer, null));
        }
        Assert.False(File.Exists(owned));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path, Token)));
    }

    [Fact]
    public async Task SpreadsheetMemberRetainsMatchingSheetAndSparseCell()
    {
        PreviewFixtures.Create(_root);
        string path = Path.Combine(_root, "workbook.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntryFromFile(Path.Combine(_root, "workbook.xlsx"), "data/workbook.xlsx");
        var inner = new SourceAnchor(SourceAnchorKind.Excel, "Data!B501:BB501", Sheet: "Data", Cell: "B501:BB501");
        var outer = new SourceAnchor(SourceAnchorKind.Archive, "data/workbook.xlsx", Line: 2, Sheet: "Data", Cell: inner.Cell,
            MemberPath: "data/workbook.xlsx", ArchiveContentAnchor: inner);
        var hit = new Hit(path, 900, "[Data] First\tMatching cell\t3", [new MatchSpan(13, 8)], Anchor: outer);
        using var lease = await ArchivePreviewService.OpenMemberAsync(path, "data/workbook.xlsx", Token);
        var child = ArchivePreviewService.CreateMemberHit("data/workbook.xlsx", lease.Path, hit)!;
        var preview = await DocumentPreviewService.LoadAsync(lease.Path, Token, child);
        Assert.Equal("Data", preview.Workbook!.InitialSheet);
        Assert.Equal(501, preview.Workbook.InitialRow);
        Assert.Equal(52, preview.Workbook.InitialColumn);
    }

    private string CreateArchive(params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open());
            writer.Write(entry.Text);
        }
        return path;
    }
}
