using System.Text;
using System.Windows;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class PdfPreviewTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchPdfText-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public PdfPreviewTextTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0, 420, 320)]
    [InlineData(90, 320, 420)]
    [InlineData(180, 420, 320)]
    [InlineData(270, 320, 420)]
    public void CurrentPageFindKeepsLiteralPhraseAndExcludesCroppedText(int rotation, double width, double height)
    {
        string path = PdfFindFixtures.Write(Path.Combine(_root, "words.pdf"), rotation);
        var page = PdfPreviewText.Read(path, 1, width, height, Token);
        Assert.True(page.CanFind, page.UnavailableReason);
        Assert.False(page.IsPartial);
        Assert.DoesNotContain("OUTSIDE", page.Text, StringComparison.Ordinal);
        var found = PdfPreviewText.Find(page, "needle phrase", matchCase: true, Token);
        Assert.True(found.IsComplete);
        var match = Assert.Single(found.Matches);
        Assert.Equal("needle phrase", page.Text.Substring(match.Range.Start, match.Range.Length));
        Assert.NotEmpty(match.Regions);
        Assert.All(match.Regions.SelectMany(region => region), point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });
        Assert.Single(PdfPreviewText.Find(page, "NEEDLE PHRASE", matchCase: false, Token).Matches);
        Assert.Empty(PdfPreviewText.Find(page, "NEEDLE PHRASE", matchCase: true, Token).Matches);
        Assert.Empty(PdfPreviewText.Find(page, "needle OR phrase", matchCase: false, Token).Matches);
        Assert.Empty(PdfPreviewText.Find(page, "Hneedle", matchCase: true, Token).Matches);
    }

    [Fact]
    public void UnsupportedOrMismatchedGeometryLeavesFindUnavailable()
    {
        string path = PdfFindFixtures.Write(Path.Combine(_root, "no-text.pdf"), includeText: false);
        var empty = PdfPreviewText.Read(path, 1, 420, 320, Token);
        Assert.False(empty.CanFind);
        Assert.Contains("embedded text", empty.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(PdfPreviewText.Find(empty, "anything", matchCase: false, Token).Matches);

        path = PdfFindFixtures.Write(Path.Combine(_root, "words.pdf"));
        var wrongAspect = PdfPreviewText.Read(path, 1, 320, 420, Token);
        Assert.False(wrongAspect.CanFind);
        Assert.Contains("does not match", wrongAspect.UnavailableReason, StringComparison.Ordinal);
        Assert.False(PdfPreviewText.Read(path, 1, double.NaN, 320, Token).CanFind);
        Assert.False(PdfPreviewText.Read(path, 1, 420, 0, Token).CanFind);
        Assert.False(PdfPreviewText.Read(path, 2, 420, 320, Token).CanFind);
    }

    [Fact]
    public void ReadRejectsFileOverExistingSizeBudgetBeforeParsing()
    {
        string path = Path.Combine(_root, "large.pdf");
        using (var stream = File.Create(path)) stream.SetLength(DocumentPreviewService.MaxPdfBytes + 1);
        var result = PdfPreviewText.Read(path, 1, 420, 320, Token);
        Assert.False(result.CanFind);
        Assert.Contains("100 MB", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void CropBeyondMediaIsRejectedEvenWhenAspectRatiosAgree()
    {
        string path = Path.Combine(_root, "outside-crop.pdf");
        WriteTextPdf(path, "needle", "[-1000 -10 11000 110]");
        var page = PdfPreviewText.Read(path, 1, 10000, 100, Token);
        Assert.False(page.CanFind);
        Assert.Contains("crop", page.UnavailableReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("/Invalid")]
    public void UnqualifiedCustomUserUnitsLeaveThePagePreviewUsableWithoutFind(string userUnit)
    {
        string path = Path.Combine(_root, "custom-units.pdf");
        WriteTextPdf(path, "needle", userUnit: userUnit);
        var page = PdfPreviewText.Read(path, 1, 10000, 100, Token);
        Assert.False(page.CanFind);
        Assert.Contains("UserUnit", page.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDefaultUserUnitRetainsOrdinaryGeometry()
    {
        string path = Path.Combine(_root, "default-units.pdf");
        WriteTextPdf(path, "needle", userUnit: "1");
        var page = PdfPreviewText.Read(path, 1, 10000, 100, Token);
        Assert.True(page.CanFind, page.UnavailableReason);
        Assert.Single(PdfPreviewText.Find(page, "needle", true, Token).Matches);
    }

    [Fact]
    public void CancellationDoesNotReturnPartialFindSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfPreviewText.Read("not-opened.pdf", 1, 420, 320, cancellation.Token));
        var page = Synthetic("needle", [new(0, 6, Rectangle(0.1, 0.1))]);
        Assert.Throws<OperationCanceledException>(() => PdfPreviewText.Find(page, "needle", false, cancellation.Token));
    }

    [Fact]
    public void Utf16RangesMapWholeGlyphsIncludingCompositeUnicodeValues()
    {
        var emoji = Rectangle(0.1, 0.1);
        var ligature = Rectangle(0.3, 0.1);
        var accent = Rectangle(0.5, 0.1);
        var page = Synthetic("😀 fi é", [new(0, 2, emoji), new(3, 2, ligature), new(6, 2, accent)]);
        var first = Assert.Single(PdfPreviewText.Find(page, "😀 fi", true, Token).Matches);
        Assert.Equal(new PreviewTextMatch(0, 5), first.Range);
        Assert.Collection(first.Regions, region => Assert.Same(emoji, region), region => Assert.Same(ligature, region));
        var insideLigature = Assert.Single(PdfPreviewText.Find(page, "i", true, Token).Matches);
        Assert.Equal(new PreviewTextMatch(4, 1), insideLigature.Range);
        Assert.Same(ligature, Assert.Single(insideLigature.Regions));
        var combining = Assert.Single(PdfPreviewText.Find(page, "é", true, Token).Matches);
        Assert.Same(accent, Assert.Single(combining.Regions));
        Assert.Empty(PdfPreviewText.Find(page, "\uDE00", true, Token).Matches);
    }

    [Fact]
    public void PartialPageAndMatchCapRemainExplicit()
    {
        var polygon = Rectangle(0.1, 0.1);
        var glyphs = Enumerable.Range(0, PreviewTextFind.MaxMatches + 1).Select(index => new PdfPreviewGlyph(index * 2, 1, polygon)).ToArray();
        var page = Synthetic(string.Concat(Enumerable.Repeat("x ", glyphs.Length)), glyphs);
        var capped = PdfPreviewText.Find(page, "x", true, Token);
        Assert.Equal(PreviewTextFind.MaxMatches, capped.Matches.Count);
        Assert.False(capped.IsComplete);
        Assert.Contains("10,000", capped.LimitReason, StringComparison.Ordinal);
        Assert.Equal(new PreviewTextMatch((PreviewTextFind.MaxMatches - 1) * 2, 1), capped.Matches[^1].Range);

        var partial = PdfPreviewText.Find(page with { IsPartial = true }, "absent", true, Token);
        Assert.Empty(partial.Matches);
        Assert.False(partial.IsComplete);
        Assert.Contains("this PDF page", partial.LimitReason, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeGlyphBudgetStopsAtDeclaredBoundary()
    {
        string path = Path.Combine(_root, "many.pdf");
        WriteTextPdf(path, new string('x', PdfPreviewText.MaxGlyphs + 1));
        var page = PdfPreviewText.Read(path, 1, 10000, 100, Token);
        Assert.True(page.CanFind, page.UnavailableReason);
        Assert.True(page.IsPartial);
        Assert.Equal(PdfPreviewText.MaxGlyphs, page.Glyphs.Count);
        Assert.Equal(PdfPreviewText.MaxGlyphs, page.Text.Length);
        Assert.False(PdfPreviewText.Find(page, "xx", true, Token).IsComplete);
    }

    private static PdfPreviewPage Synthetic(string text, IReadOnlyList<PdfPreviewGlyph> glyphs) => new(text, glyphs, false, null);
    private static IReadOnlyList<Point> Rectangle(double x, double y) => Array.AsReadOnly(new[]
    {
        new Point(x, y), new Point(x + 0.05, y), new Point(x + 0.05, y + 0.05), new Point(x, y + 0.05),
    });

    private static void WriteTextPdf(string path, string text, string? cropBox = null, string? userUnit = null)
    {
        string content = "BT /F1 0.1 Tf 10 50 Td (" + text + ") Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10000 100] " +
                (cropBox is null ? "" : "/CropBox " + cropBox + " ") + (userUnit is null ? "" : "/UserUnit " + userUnit + " ") +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
