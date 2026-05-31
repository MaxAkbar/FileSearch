using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ExcelExtractorTests : IDisposable
{
    private readonly string _path;

    public ExcelExtractorTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void SupportedExtensions_IncludesXlsx()
    {
        var extractor = new ExcelExtractor();
        Assert.Contains(".xlsx", extractor.SupportedExtensions);
    }

    [Fact]
    public async Task Extracts_CellValues_PerRow()
    {
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Data");
            sheet.Cell(1, 1).Value = "Name";
            sheet.Cell(1, 2).Value = "Age";
            sheet.Cell(2, 1).Value = "Alice";
            sheet.Cell(2, 2).Value = 30;
            sheet.Cell(3, 1).Value = "Bob";
            sheet.Cell(3, 2).Value = 42;
            workbook.SaveAs(_path);
        }

        var extractor = new ExcelExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("[Data]", l.Content));
        Assert.Contains("Alice", lines[1].Content);
        Assert.Contains("Bob", lines[2].Content);
    }

    [Fact]
    public async Task IncludesSheetName_InContent()
    {
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("FirstSheet").Cell(1, 1).Value = "one";
            workbook.AddWorksheet("SecondSheet").Cell(1, 1).Value = "two";
            workbook.SaveAs(_path);
        }

        var extractor = new ExcelExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);

        Assert.Contains(lines, l => l.Content.Contains("FirstSheet") && l.Content.Contains("one"));
        Assert.Contains(lines, l => l.Content.Contains("SecondSheet") && l.Content.Contains("two"));
    }
}
