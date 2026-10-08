#pragma warning disable xUnit1051 // Cancellation and process-restart recovery are exercised with explicit tokens below.
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace FileSearch.Core.Tests;

public sealed class ReplacementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filesearch-replace-" + Guid.NewGuid().ToString("N"));
    private readonly ReplacementOptions _options;
    private readonly ReplacementService _service;
    public ReplacementServiceTests()
    {
        Directory.CreateDirectory(_root);
        _options = new() { BackupDirectory = Path.Combine(_root, "backups") };
        _service = new(_options);
    }
    public void Dispose() { _service.Dispose(); Directory.Delete(_root, true); }
    private string FilePath(string name) => Path.Combine(_root, name);
    private ReplacementRequest Request(string find = "needle", string replacement = "thread", ReplacementTarget target = ReplacementTarget.Contents) =>
        new([_root], new WalkerOptions(), find, replacement, target);
    private async Task<ReplacementPlan> Preview(ReplacementRequest? request = null) => await _service.PreviewAsync(request ?? Request(), CancellationToken.None);
    private async Task<ReplacementBatchResult> Apply(ReplacementPlan plan) => await _service.ApplyAsync(plan, plan.Items.Where(item => item.CanApply).Select(item => item.Id).ToHashSet(), CancellationToken.None);

    [Fact]
    public async Task LiteralPreviewDoesNotWriteAndApplyUndoSurviveRestart()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "Needle\r\nneedle\nNEEDLE\r");
        var original = await File.ReadAllBytesAsync(path);
        var plan = await Preview();
        Assert.Equal(3, Assert.Single(plan.Items).ChangeCount);
        var excerpt = Assert.Single(plan.Items[0].Changes);
        Assert.Equal("thread\r\nthread\nthread\r", excerpt.After);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(_options.BackupDirectory));
        Assert.Equal(1, (await Apply(plan)).SucceededCount);
        Assert.Equal("thread\r\nthread\nthread\r", await File.ReadAllTextAsync(path));
        using var restarted = new ReplacementService(_options);
        Assert.True(await restarted.CanUndoAsync(CancellationToken.None));
        Assert.Equal(1, (await restarted.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(await restarted.CanUndoAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task PreservesEncodingBomAndMixedLineEndings(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            "utf32le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true),
        };
        var path = FilePath("a.txt");
        await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes("é needle\r\nneedle\nfin\r")).ToArray());
        Assert.Equal(1, (await Apply(await Preview())).SucceededCount);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes("é thread\r\nthread\nfin\r")).ToArray(), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RegexCaptureSubstitutionAndEmptyReplacement()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "one=12 two=34");
        var plan = await Preview(Request(@"(\w+)=(\d+)", "$2:$1") with { UseRegex = true });
        Assert.Equal(2, Assert.Single(plan.Items).ChangeCount);
        Assert.Equal(1, (await Apply(plan)).SucceededCount);
        Assert.Equal("12:one 34:two", await File.ReadAllTextAsync(path));
        Assert.Equal(1, (await Apply(await Preview(Request("12:one", "")))).SucceededCount);
        Assert.Equal(" 34:two", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CaseSensitiveNoOpAndInvalidRegex()
    {
        await File.WriteAllTextAsync(FilePath("a.txt"), "Needle needle");
        Assert.Equal(1, Assert.Single((await Preview(Request() with { MatchCase = true })).Items).ChangeCount);
        Assert.Empty((await Preview(Request("needle", "needle") with { MatchCase = true })).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => Preview(Request("", "x")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Preview(Request("[", "x") with { UseRegex = true }));
    }

    [Fact]
    public async Task CheckedSubsetAndStalePreview()
    {
        var first = FilePath("a.txt"); var second = FilePath("b.txt");
        await File.WriteAllTextAsync(first, "needle"); await File.WriteAllTextAsync(second, "needle");
        var plan = await Preview();
        var selected = Assert.Single(plan.Items, item => item.Path == first);
        await File.WriteAllTextAsync(first, "externally changed");
        var result = await _service.ApplyAsync(plan, new HashSet<string> { selected.Id }, CancellationToken.None);
        Assert.Equal(0, result.SucceededCount);
        Assert.Contains("Changed since preview", Assert.Single(result.Outcomes).Message);
        Assert.Equal("needle", await File.ReadAllTextAsync(second));
        Assert.Equal("externally changed", await File.ReadAllTextAsync(first));
    }

    [Fact]
    public async Task UndoRefusesChangedContent()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "needle"); await Apply(await Preview());
        await File.WriteAllTextAsync(path, "external edit");
        var result = await _service.UndoAsync(CancellationToken.None);
        Assert.Equal(0, result.SucceededCount); Assert.Equal("external edit", await File.ReadAllTextAsync(path));
        Assert.NotEmpty(Directory.GetFiles(_options.BackupDirectory, "*.original", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SkipsBinaryUnsupportedEncodingAndUnsupportedFormats()
    {
        await File.WriteAllBytesAsync(FilePath("binary.txt"), [0, 1, 2]);
        await File.WriteAllBytesAsync(FilePath("ansi.txt"), [0x80, 0x96]);
        await File.WriteAllTextAsync(FilePath("file.pdf"), "needle");
        var plan = await Preview(); Assert.Equal(3, plan.Items.Count); Assert.All(plan.Items, item => Assert.False(item.CanApply));
    }

    [Fact]
    public async Task FiltersRecursionGlobsAndExcludedDirectories()
    {
        Directory.CreateDirectory(FilePath("bin")); Directory.CreateDirectory(FilePath("child"));
        await File.WriteAllTextAsync(FilePath("a.txt"), "needle"); await File.WriteAllTextAsync(FilePath("b.md"), "needle");
        await File.WriteAllTextAsync(FilePath("bin/c.txt"), "needle"); await File.WriteAllTextAsync(FilePath("child/d.txt"), "needle");
        var plan = await Preview(Request() with { WalkerOptions = new() { Recursive = false, IncludeGlobs = ["*.txt"] } });
        Assert.Equal(FilePath("a.txt"), Assert.Single(plan.Items).Path);
        var recursive = await Preview(Request() with { WalkerOptions = new() { IncludeGlobs = ["*.txt"] } });
        Assert.Equal(2, recursive.Items.Count);
    }

    [Fact]
    public async Task NamesPreserveExtensionsAndSupportIncludingThem()
    {
        var path = FilePath("needle.needle"); await File.WriteAllTextAsync(path, "body");
        await File.WriteAllTextAsync(FilePath(".needle.needle"), "dotfile");
        await Apply(await Preview(Request(target: ReplacementTarget.Names)));
        Assert.True(File.Exists(FilePath("thread.needle")));
        Assert.True(File.Exists(FilePath(".thread.needle")));
        await _service.UndoAsync(CancellationToken.None);
        await Apply(await Preview(Request(target: ReplacementTarget.Names) with { IncludeExtensions = true }));
        Assert.True(File.Exists(FilePath("thread.thread")));
        Assert.True(File.Exists(FilePath(".thread.thread")));
    }

    [Fact]
    public async Task NestedRenamesAndUndoUseCorrectOrder()
    {
        Directory.CreateDirectory(FilePath("needle/needle"));
        await File.WriteAllTextAsync(FilePath("needle/needle/needle.txt"), "original");
        var result = await Apply(await Preview(Request(target: ReplacementTarget.Names)));
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath("thread/thread/thread.txt")));
        using var restarted = new ReplacementService(_options);
        Assert.Equal(3, (await restarted.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath("needle/needle/needle.txt")));
    }

    [Fact]
    public async Task UndoSkipsDependentRenamesWhenTheParentDestinationIsOccupied()
    {
        Directory.CreateDirectory(FilePath("needle/needle"));
        await File.WriteAllTextAsync(FilePath("needle/needle/needle.txt"), "original");
        await Apply(await Preview(Request(target: ReplacementTarget.Names)));
        Directory.CreateDirectory(FilePath("needle"));
        var undo = await _service.UndoAsync(CancellationToken.None);
        Assert.Equal(0, undo.SucceededCount); Assert.Equal(3, undo.Outcomes.Count);
        Assert.True(File.Exists(FilePath("thread/thread/thread.txt")));
        Assert.True(await _service.CanUndoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LockedAndReadOnlyFilesAreSkippedWithoutChangingThem()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "needle");
        var plan = await Preview();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(Assert.Single((await Preview()).Items).CanApply);
            Assert.Equal(0, (await Apply(plan)).SucceededCount);
        }
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Assert.False(Assert.Single((await Preview()).Items).CanApply); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.Equal("needle", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CaseOnlyRenameCanBeUndone()
    {
        await File.WriteAllTextAsync(FilePath("needle.txt"), "original");
        Assert.Equal(1, (await Apply(await Preview(Request("needle", "NEEDLE", ReplacementTarget.Names)))).SucceededCount);
        Assert.Contains(FilePath("NEEDLE.txt"), Directory.GetFiles(_root));
        Assert.Equal(1, (await _service.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.Contains(FilePath("needle.txt"), Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("../escape")]
    [InlineData("")]
    [InlineData("bad.")]
    [InlineData("bad ")]
    public async Task RejectsInvalidNames(string replacement)
    {
        Directory.CreateDirectory(FilePath("needle"));
        Assert.False(Assert.Single((await Preview(Request("needle", replacement, ReplacementTarget.Names))).Items).CanApply);
    }

    [Fact]
    public async Task DetectsDuplicateExistingAndLateCollisions()
    {
        await File.WriteAllTextAsync(FilePath("one.txt"), "1"); await File.WriteAllTextAsync(FilePath("two.txt"), "2");
        var duplicate = await Preview(Request("one|two", "same", ReplacementTarget.Names) with { UseRegex = true });
        Assert.All(duplicate.Items, item => Assert.False(item.CanApply));
        await File.WriteAllTextAsync(FilePath("same.txt"), "existing");
        Assert.False(Assert.Single((await Preview(Request("one", "same", ReplacementTarget.Names))).Items).CanApply);
        var plan = await Preview(Request("one", "late", ReplacementTarget.Names));
        await File.WriteAllTextAsync(FilePath("late.txt"), "external");
        Assert.Equal(0, (await Apply(plan)).SucceededCount);
        Assert.Equal("external", await File.ReadAllTextAsync(FilePath("late.txt")));
    }

    [Fact]
    public async Task ProtectsIndexedRootsAndTheirAncestors()
    {
        Directory.CreateDirectory(FilePath("needle/child"));
        var plan = await Preview(Request(target: ReplacementTarget.Names) with { ProtectedRoots = [FilePath("needle/child")] });
        Assert.Contains("protected", Assert.Single(plan.Items).SkipReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationLeavesCompletedItemsUndoable()
    {
        for (var i = 0; i < 40; i++) await File.WriteAllTextAsync(FilePath($"{i:D2}.txt"), "needle");
        var plan = await Preview();
        using var cancellation = new CancellationTokenSource();
        var task = _service.ApplyAsync(plan, plan.Items.Select(item => item.Id).ToHashSet(), cancellation.Token);
        // Observe recovery creation without holding a handle on a file being atomically replaced.
        while (!task.IsCompleted && (!Directory.Exists(_options.BackupDirectory) ||
            Directory.GetFiles(_options.BackupDirectory, "*.original", SearchOption.AllDirectories).Length == 0)) await Task.Delay(1);
        cancellation.Cancel();
        var result = await task;
        Assert.True(result.Cancelled); Assert.InRange(result.SucceededCount, 1, 39);
        Assert.Equal(result.SucceededCount, (await _service.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.All(Directory.GetFiles(_root, "*.txt"), path => Assert.Equal("needle", File.ReadAllText(path)));
    }

    [Fact]
    public async Task PartialRenameFailureDoesNotPreventOtherItemsOrUndo()
    {
        await File.WriteAllTextAsync(FilePath("a-needle.txt"), "a"); await File.WriteAllTextAsync(FilePath("b-needle.txt"), "b");
        var plan = await Preview(Request(target: ReplacementTarget.Names));
        await File.WriteAllTextAsync(FilePath("a-thread.txt"), "occupied");
        var result = await Apply(plan); Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, (await _service.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.True(File.Exists(FilePath("a-needle.txt"))); Assert.True(File.Exists(FilePath("b-needle.txt")));
    }

    [Fact]
    public async Task PreparedJournalRecoversCommittedContentAfterCrash()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "needle"); await Apply(await Preview());
        var latest = Path.Combine(_options.BackupDirectory, "latest.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(latest))!;
        json["Entries"]![0]!["State"] = "Prepared";
        await File.WriteAllTextAsync(latest, json.ToJsonString());
        using var restarted = new ReplacementService(_options);
        Assert.Equal(1, (await restarted.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PreviewIncludesMatchesBeyondTheBeginningOfALargeFile()
    {
        await File.WriteAllTextAsync(FilePath("a.txt"), new string('x', 20000) + "needle");
        var plan = await Preview(); var change = Assert.Single(Assert.Single(plan.Items).Changes);
        Assert.Contains("needle", change.Before); Assert.Contains("thread", change.After);
    }

    [Fact]
    public async Task FailedBatchKeepsThePreviousUndoTarget()
    {
        var first = FilePath("a.txt"); await File.WriteAllTextAsync(first, "needle"); await Apply(await Preview());
        await File.WriteAllTextAsync(FilePath("b.txt"), "needle"); var stale = await Preview();
        await File.WriteAllTextAsync(FilePath("b.txt"), "changed"); Assert.Equal(0, (await Apply(stale)).SucceededCount);
        Assert.Equal(1, (await _service.UndoAsync(CancellationToken.None)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(first));
    }

    [Fact]
    public async Task BackupFailureLeavesSourceUntouched()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "needle");
        var plan = await Preview(); await File.WriteAllTextAsync(_options.BackupDirectory, "blocks directory creation");
        await Assert.ThrowsAsync<IOException>(() => Apply(plan));
        Assert.Equal("needle", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RestartFinishesJournalAfterUndoAlreadyCommitted()
    {
        var path = FilePath("a.txt"); await File.WriteAllTextAsync(path, "needle"); await Apply(await Preview());
        var latest = Path.Combine(_options.BackupDirectory, "latest.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(latest))!;
        json["Entries"]![0]!["State"] = "Undoing";
        await File.WriteAllTextAsync(path, "needle"); await File.WriteAllTextAsync(latest, json.ToJsonString());
        using var restarted = new ReplacementService(_options);
        await restarted.UndoAsync(CancellationToken.None);
        Assert.False(await restarted.CanUndoAsync(CancellationToken.None)); Assert.Equal("needle", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WordProtectionPreventsReplacement()
    {
        var path = FilePath("a.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart(); main.Document = new Document(new Body(new Paragraph(new Run(new Text("needle")))));
            var settings = main.AddNewPart<DocumentSettingsPart>(); settings.Settings = new Settings(new DocumentProtection { Enforcement = true, Edit = DocumentProtectionValues.ReadOnly });
            main.Document.Save(); settings.Settings.Save();
        }
        var plan = await Preview(); Assert.False(Assert.Single(plan.Items).CanApply); Assert.Contains("Protected", plan.Items[0].SkipReason!);
    }

    [Fact]
    public async Task WordMatchesAcrossStyledRunsAndHeaderWithoutFlattening()
    {
        var path = FilePath("a.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new RunProperties(new Bold()), new Text("nee")), new Run(new RunProperties(new Italic()), new Text("dle end"))),
                new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("needle table"))))))));
            var header = main.AddNewPart<HeaderPart>(); header.Header = new Header(new Paragraph(new Run(new Text("needle header"))));
            var footer = main.AddNewPart<FooterPart>(); footer.Footer = new Footer(new Paragraph(new Run(new Text("needle footer"))));
            main.Document.Save(); header.Header.Save(); footer.Footer.Save();
        }
        var plan = await Preview(); Assert.Equal(4, Assert.Single(plan.Items).ChangeCount);
        Assert.Equal(1, (await Apply(plan)).SucceededCount);
        using (var document = WordprocessingDocument.Open(path, false))
        {
            var runs = document.MainDocumentPart!.Document!.Descendants<Run>().ToArray();
            Assert.Equal("thread", Assert.Single(runs[0].Elements<Text>()).Text);
            Assert.NotNull(runs[0].RunProperties!.Bold); Assert.NotNull(runs[1].RunProperties!.Italic);
            Assert.Equal(" end", Assert.Single(runs[1].Elements<Text>()).Text);
            Assert.Equal(SpaceProcessingModeValues.Preserve, Assert.Single(runs[1].Elements<Text>()).Space!.Value);
            Assert.Contains("thread header", document.MainDocumentPart.HeaderParts.Single().Header!.InnerText);
            Assert.Contains("thread table", document.MainDocumentPart.Document.Descendants<Table>().Single().InnerText);
            Assert.Contains("thread footer", document.MainDocumentPart.FooterParts.Single().Footer!.InnerText);
        }
        Assert.Equal(1, (await _service.UndoAsync(CancellationToken.None)).SucceededCount);
    }

    [Fact]
    public async Task WordDoesNotMatchAcrossBreaksOrEditFieldResults()
    {
        var path = FilePath("a.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("nee"), new Break(), new Text("dle"))),
                new Paragraph(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                    new Run(new FieldCode("MERGEFIELD name")), new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                    new Run(new Text("needle")), new Run(new FieldChar { FieldCharType = FieldCharValues.End })),
                new Paragraph(new Run(new Text("needle"))), new Paragraph()));
            main.Document.Save();
        }
        Assert.Equal(1, Assert.Single((await Preview()).Items).ChangeCount);
        Assert.Equal(1, (await Apply(await Preview())).SucceededCount);
        using var updated = WordprocessingDocument.Open(path, false);
        var paragraphs = updated.MainDocumentPart!.Document!.Descendants<Paragraph>().ToArray();
        Assert.Equal("needle", string.Concat(paragraphs[0].Descendants<Text>().Select(text => text.Text)));
        Assert.Equal("needle", string.Concat(paragraphs[1].Descendants<Text>().Select(text => text.Text)));
        Assert.Equal("thread", paragraphs[2].InnerText);
    }

    [Fact]
    public async Task ExcelTextFormulaOptInAndCellTypes()
    {
        var path = FilePath("a.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Data"); sheet.Cell("A1").Value = "needle";
            sheet.Cell("A2").Value = 42; sheet.Cell("A3").Value = new DateTime(2024, 1, 2);
            sheet.Cell("B1").FormulaA1 = "\"needle\""; sheet.Cell("A1").Style.Font.Bold = true;
            workbook.SaveAs(path);
        }
        Assert.Equal(1, Assert.Single((await Preview()).Items).ChangeCount);
        var plan = await Preview(Request() with { IncludeFormulas = true }); Assert.Equal(2, Assert.Single(plan.Items).ChangeCount);
        Assert.Equal(1, (await Apply(plan)).SucceededCount);
        using var updated = new XLWorkbook(path);
        var cells = updated.Worksheet("Data"); Assert.Equal("thread", cells.Cell("A1").GetString()); Assert.True(cells.Cell("A1").Style.Font.Bold);
        Assert.Equal(42, cells.Cell("A2").GetDouble()); Assert.Equal(XLDataType.DateTime, cells.Cell("A3").DataType);
        Assert.Equal("\"thread\"", cells.Cell("B1").FormulaA1);
    }

    [Fact]
    public async Task ExcelSharedStringCopyDoesNotModifyOtherCells()
    {
        var path = FilePath("a.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart(); workbook.Workbook = new S.Workbook();
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell(new S.CellValue("0")) { CellReference = "A1", DataType = S.CellValues.SharedString },
                new S.Cell(new S.CellFormula("\"needle\""), new S.CellValue("0")) { CellReference = "B1", DataType = S.CellValues.String })));
            workbook.Workbook.Append(new S.Sheets(new S.Sheet { Name = "Data", SheetId = 1, Id = workbook.GetIdOfPart(sheet) }));
            var strings = workbook.AddNewPart<SharedStringTablePart>(); strings.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("needle")));
            workbook.Workbook.Save(); sheet.Worksheet.Save(); strings.SharedStringTable.Save();
        }
        Assert.Equal(1, (await Apply(await Preview())).SucceededCount);
        using var updated = SpreadsheetDocument.Open(path, false);
        Assert.Equal("needle", updated.WorkbookPart!.SharedStringTablePart!.SharedStringTable!.InnerText);
        Assert.Equal("thread", updated.WorkbookPart.WorksheetParts.Single().Worksheet!.Descendants<S.Cell>().First().InlineString!.InnerText);
    }

    [Fact]
    public async Task ExcelUnsupportedFormulasRemainUntouchedAndOrdinaryFormulasRecalculate()
    {
        var path = FilePath("a.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart(); workbook.Workbook = new S.Workbook();
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell(new S.CellFormula("\"needle\""), new S.CellValue("needle")) { CellReference = "A1", DataType = S.CellValues.String },
                new S.Cell(new S.CellFormula("\"needle\"") { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 0, Reference = "B1:B2" }) { CellReference = "B1" },
                new S.Cell(new S.CellFormula("\"needle\"") { FormulaType = S.CellFormulaValues.Array, Reference = "C1:C2" }) { CellReference = "C1" },
                new S.Cell(new S.CellFormula("\"needle\"")) { CellReference = "D1", CellMetaIndex = 1 })));
            workbook.Workbook.Append(new S.Sheets(new S.Sheet { Name = "Data", SheetId = 1, Id = workbook.GetIdOfPart(sheet) }));
            workbook.Workbook.Save(); sheet.Worksheet.Save();
        }
        var plan = await Preview(Request() with { IncludeFormulas = true });
        var item = Assert.Single(plan.Items); Assert.Equal(1, item.ChangeCount);
        Assert.Equal(3, item.Changes.Count(change => change.Location.Contains("skipped", StringComparison.Ordinal)));
        Assert.Equal(1, (await Apply(plan)).SucceededCount);
        using var updated = SpreadsheetDocument.Open(path, false);
        var cells = updated.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.Cell>().ToArray();
        Assert.Equal("\"thread\"", cells[0].CellFormula!.Text); Assert.Null(cells[0].CellValue);
        Assert.All(cells.Skip(1), cell => Assert.Equal("\"needle\"", cell.CellFormula!.Text));
        Assert.True(updated.WorkbookPart.Workbook!.CalculationProperties!.FullCalculationOnLoad!.Value);
        Assert.True(updated.WorkbookPart.Workbook.CalculationProperties.ForceFullCalculation!.Value);
    }

    [Fact]
    public async Task PowerPointSlideAndNotesStyledText()
    {
        var path = FilePath("a.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var main = document.AddPresentationPart(); var slide = main.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.RunProperties { Bold = true }, new A.Text("nee")), new A.Run(new A.Text("dle"))))))));
            var notes = slide.AddNewPart<NotesSlidePart>();
            var noteBody = new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("needle notes"))));
            notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(noteBody))));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = main.GetIdOfPart(slide) }));
            main.Presentation.Save(); slide.Slide.Save(); notes.NotesSlide.Save();
        }
        Assert.Equal(2, Assert.Single((await Preview()).Items).ChangeCount);
        Assert.Equal(1, (await Apply(await Preview())).SucceededCount);
        using var updated = PresentationDocument.Open(path, false);
        var part = updated.PresentationPart!.SlideParts.Single();
        Assert.Equal("thread", string.Concat(part.Slide!.Descendants<A.Text>().Select(text => text.Text)));
        Assert.True(part.Slide.Descendants<A.RunProperties>().First().Bold!.Value);
        Assert.Contains("thread notes", part.NotesSlidePart!.NotesSlide!.InnerText);
    }
}
