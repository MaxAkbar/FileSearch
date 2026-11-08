using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClosedXML.Excel;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Services;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class ArchiveDocumentSmoke
{
    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        string root = Path.Combine(Path.GetDirectoryName(output)!, "archive-document-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PreviewFixtures.Create(root);
        // The shared preview fixture intentionally omits OOXML packaging parts.
        // A content extractor needs a genuine workbook, not just preview XML.
        string workbookPath = Path.Combine(root, "searchable-workbook.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Intro").Cell(1, 1).Value = "Introduction";
            var data = workbook.AddWorksheet("Data");
            data.Cell(501, 2).Value = "First";
            data.Cell(501, 52).Value = "Matching cell";
            data.Cell(501, 53).Value = 3;
            data.Cell(501, 54).Value = "Final column";
            workbook.SaveAs(workbookPath);
        }
        string pdf = PdfFindFixtures.WriteTwoPages(Path.Combine(root, "two-pages.pdf"));
        string pdfZip = Path.Combine(root, "pdf-bundle.zip"), sheetZip = Path.Combine(root, "sheet-bundle.zip");
        using (var zip = ZipFile.Open(pdfZip, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(pdf, "reports/two-pages.pdf");
            using var writer = new StreamWriter(zip.CreateEntry("notes.txt").Open());
            writer.Write("A different archive member without the matched phrase.");
        }
        using (var zip = ZipFile.Open(sheetZip, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(workbookPath, "data/workbook.xlsx");
        string duplicates = Path.Combine(root, "duplicate-members.zip");
        using (var zip = ZipFile.Open(duplicates, ZipArchiveMode.Create))
        {
            foreach (var text in new[] { "first", "second" })
            { using var writer = new StreamWriter(zip.CreateEntry("report.txt").Open()); writer.Write(text); }
        }
        string[] sources = [pdf, workbookPath, pdfZip, sheetZip, duplicates];
        var hashes = sources.ToDictionary(path => path, Hash);
        var checks = new List<object>();
        var temporaryPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string scratch = Path.Combine(root, "extraction-scratch");
        var extractor = ZipExtractor.CreateForIsolatedHost(scratch);
        var registry = new ExtractorRegistry([extractor]);
        var searcher = new Searcher(new FileWalker(), registry);
        var pdfHit = await FindHit(searcher, root, "pdf-bundle.zip", "second page needle phrase");
        var sheetHit = await FindHit(searcher, root, "sheet-bundle.zip", "Matching cell");
        Require(pdfHit.Anchor?.ArchiveContentAnchor?.Page == 2 &&
            sheetHit.Anchor?.ArchiveContentAnchor?.Sheet == "Data", "Real ZIP extraction lost PDF or sheet source metadata.");
        Require(!Directory.Exists(scratch) || Directory.GetFileSystemEntries(scratch).Length == 0, "Document extraction left scratch members behind.");
        checks.Add(new { check = "real-zip-pdf-xlsx-extraction", pdfMember = pdfHit.Anchor!.MemberPath,
            pdfPage = pdfHit.Anchor.ArchiveContentAnchor!.Page, sheetMember = sheetHit.Anchor!.MemberPath, sheet = "Data", scratchClean = true });

        var launcher = new FakeFileLauncher();
        var settings = new FakeSettingsService(); settings.Current.UseIndex = false;
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var preview = new FilePreviewService(registry);
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), preview, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher,
            new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(),
            new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var pdfFile = new FileResultViewModel(pdfZip, launcher); pdfFile.AddHit(pdfHit);
        var sheetFile = new FileResultViewModel(sheetZip, launcher); sheetFile.AddHit(sheetHit);
        search.Files.Add(pdfFile); search.Files.Add(sheetFile); search.SearchPath = root; search.PreviewPaneWidth = 650;
        var window = Offscreen(new MainWindow { DataContext = main, Width = 1440, Height = 900 });
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), preview, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = Offscreen(new QuickSearchWindow { DataContext = quick, Width = 760, Height = 680 });
        DateTime pdfModified = File.GetLastWriteTimeUtc(pdfZip);
        try
        {
            window.Show(); await Settle();
            var tabs = Descendants(window).OfType<TabControl>().Single(control => AutomationProperties.GetName(control) == "Result preview views");
            tabs.SelectedIndex = 1; search.SelectedFile = pdfFile;
            await Settle();
            var view = Descendants(tabs).OfType<FileDocumentPreview>().First();
            var child = await WaitChild(view, "Pdf"); Track(child);
            Require(Page(child) == "2 / 2" && child.PreferredHit?.Anchor?.Kind == SourceAnchorKind.Pdf,
                "Main archive PDF did not select the matching second page.");
            checks.Add(new { check = "main-matching-whole-pdf-page", page = Page(child), innerKind = "Pdf" });
            Save(window, ImagePath(output, "main-pdf-page-two"));

            Click(child, "PreviousPage");
            await Until(() => child.PreviewState == "Pdf" && Page(child) == "1 / 2" && child.CanFindInPreview);
            child.ShowPreviewFind(); Query(child).Text = "needle phrase"; await child.FindInPreviewAsync();
            await child.MovePreviewMatchAsync(1); Invoke(child, "SetZoom", .8d); await Settle();
            Require(child.CurrentPreviewMatchNumber == 2, "Archive child Find did not select the second current-page match.");
            Click(view, "ExpandButton"); await Settle();
            var expanded = window.OwnedWindows.Cast<Window>().Single();
            try
            {
                var expandedChild = await WaitChild((FileDocumentPreview)expanded.Content, "Pdf"); Track(expandedChild);
                Require(Page(expandedChild) == "1 / 2" && expandedChild.IsPreviewFindVisible && expandedChild.CurrentPreviewMatchNumber == 2 &&
                    Query(expandedChild).Text == "needle phrase" && !(bool)Field(expandedChild, "_fitImage")! &&
                    Math.Abs((double)Field(expandedChild, "_zoom")! - .8d) < .00001,
                    "Expand lost the archive child's current PDF page, Find or manual zoom.");
                checks.Add(new { check = "expanded-current-pdf-page-find-zoom", page = Page(expandedChild), match = 2, zoom = .8 });
                Save(expanded, ImagePath(output, "expanded-pdf-find"));
            }
            finally { expanded.Close(); }

            ((ComboBox)view.FindName("PartSelector")).SelectedValue = "notes.txt";
            child = await WaitChild(view, "Text"); Track(child);
            Require(child.PreferredHit is null, "A manually selected different member inherited the PDF hit.");
            checks.Add(new { check = "different-member-discards-old-hit", member = "notes.txt", staleHit = false });

            search.SelectedFile = sheetFile;
            child = await WaitChild(view, "Spreadsheet"); Track(child);
            var sheet = Descendants(child).OfType<SpreadsheetPreviewControl>().Single();
            Require(sheet.CurrentPosition.InitialSheet == "Data" && sheet.CurrentPosition.InitialRow == 501 && sheet.CurrentPosition.InitialColumn == 52,
                "Archive workbook lost its matched sparse cell.");
            checks.Add(new { check = "main-matching-sheet-cell", sheet = "Data", row = 501, column = 52 });
            var address = Descendants(sheet).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Cell address");
            address.Text = "C400";
            Descendants(sheet).OfType<Button>().Single(button => Equals(button.Content, "Go to cell")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Click(view, "ExpandButton"); await Settle();
            expanded = window.OwnedWindows.Cast<Window>().Single();
            try
            {
                var expandedChild = await WaitChild((FileDocumentPreview)expanded.Content, "Spreadsheet"); Track(expandedChild);
                var current = Descendants(expandedChild).OfType<SpreadsheetPreviewControl>().Single().CurrentPosition;
                Require(current.InitialSheet == "Data" && current.InitialRow == 400 && current.InitialColumn == 3,
                    "Expand reverted the archive workbook to its original search cell.");
                checks.Add(new { check = "expanded-current-sheet-cell", sheet = "Data", row = 400, column = 3 });
                Save(expanded, ImagePath(output, "expanded-sheet"));
            }
            finally { expanded.Close(); }

            quick.Results.Add(pdfFile); quickWindow.Show(); quick.SelectedResult = pdfFile;
            await quick.PreviewResultCommand.ExecuteAsync(null); await Settle();
            var quickView = (FileDocumentPreview)quickWindow.FindName("QuickFileDocumentPreview");
            var quickChild = await WaitChild(quickView, "Pdf"); Track(quickChild);
            Require(Page(quickChild) == "2 / 2", "Quick Search lost the archive's exact matching PDF page.");
            quickChild.ShowPreviewFind(); Query(quickChild).Text = "needle phrase"; await quickChild.FindInPreviewAsync(); await Settle();
            Require(quickChild.PreviewMatchCount == 1 && Query(quickChild).ActualWidth > 0,
                "Quick archive PDF Find failed or lost its controls.");
            Save(quickWindow, ImagePath(output, "quick-pdf"));
            checks.Add(new { check = "quick-matching-pdf-find", page = Page(quickChild), matches = 1 });
            Click(quickView, "ExpandButton"); await Settle();
            expanded = quickWindow.OwnedWindows.Cast<Window>().Single();
            try
            {
                var expandedChild = await WaitChild((FileDocumentPreview)expanded.Content, "Pdf"); Track(expandedChild);
                Require(Page(expandedChild) == "2 / 2" && expandedChild.IsPreviewFindVisible && expandedChild.PreviewMatchCount == 1,
                    "Quick Expand lost archive member/page/Find state.");
                checks.Add(new { check = "quick-expanded-member-page-find", page = Page(expandedChild), matches = 1 });
            }
            finally { expanded.Close(); }
            quick.IsPreviewVisible = false; await Settle();

            search.SelectedFile = pdfFile; await WaitChild(view, "Pdf");
            search.SelectedFile = sheetFile; await WaitChild(view, "Spreadsheet");
            File.SetLastWriteTimeUtc(pdfZip, pdfModified.AddMinutes(1));
            search.SelectedFile = pdfFile;
            await Until(() => view.PreviewState == "Unavailable");
            Require(Field(view, "_archiveChild") is null, "A changed ZIP retained a stale member preview.");
            File.SetLastWriteTimeUtc(pdfZip, pdfModified);
            checks.Add(new { check = "changed-zip-rejects-stale-hit", state = "Unavailable", timestampOnly = true });

            var ambiguous = new FileResultViewModel(duplicates, launcher);
            ambiguous.AddHit(new Hit(duplicates, 1, "first", [], Anchor: new SourceAnchor(SourceAnchorKind.Archive,
                "report.txt", MemberPath: "report.txt", ArchiveFingerprint: Fingerprint(duplicates))));
            search.Files.Add(ambiguous); search.SelectedFile = ambiguous;
            await Until(() => view.PreviewState == "Unavailable");
            Require(Field(view, "_archiveChild") is null, "An ambiguous member opened the first duplicate.");
            checks.Add(new { check = "duplicate-member-unavailable", state = "Unavailable" });
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { error = error.ToString(), checks }, new JsonSerializerOptions { WriteIndented = true }));
            Save(window, ImagePath(output, "failure"));
            throw;
        }
        finally
        {
            File.SetLastWriteTimeUtc(pdfZip, pdfModified);
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            foreach (Window owned in quickWindow.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            quickWindow.Close(); window.Close();
        }
        await Until(() => temporaryPreviews.All(path => !File.Exists(path)));
        Require(hashes.All(pair => Hash(pair.Key) == pair.Value), "A source archive/document was modified.");
        checks.Add(new { check = "source-and-preview-cleanup", sourceBytesUnchanged = true, temporaryPreviewsCleaned = temporaryPreviews.Count });
        File.WriteAllText(output, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));

        void Track(FileDocumentPreview child) { if (child.Result is { } result) temporaryPreviews.Add(result.FullPath); }
    }

    private static async Task<Hit> FindHit(Searcher searcher, string root, string glob, string phrase)
    {
        var hits = new List<Hit>();
        var status = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await foreach (var hit in searcher.SearchAsync(new SearchRequest(new TermQuery(phrase), [root],
            new WalkerOptions { IncludeGlobs = [glob], Recursive = false }, Status: status.Enqueue), CancellationToken.None)) hits.Add(hit);
        Require(hits.Count == 1, $"Expected one real archive hit for {glob}/{phrase}; found {hits.Count}. " +
            string.Join(" | ", status));
        return hits[0];
    }
    private static async Task<FileDocumentPreview> WaitChild(FileDocumentPreview parent, string state)
    {
        await Until(() => parent.PreviewState == "Archive" && Field(parent, "_archiveChild") is FileDocumentPreview child &&
            child.PreviewState == state && (state != "Pdf" || child.CanFindInPreview));
        await Settle();
        return (FileDocumentPreview)Field(parent, "_archiveChild")!;
    }
    private static object? Field(FileDocumentPreview view, string name) => typeof(FileDocumentPreview).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view);
    private static void Invoke(FileDocumentPreview view, string name, params object[] arguments) => typeof(FileDocumentPreview).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, arguments);
    private static void Click(FileDocumentPreview view, string name) => ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static TextBox Query(FileDocumentPreview view) => (TextBox)view.FindName("PreviewFindQuery");
    private static string Page(FileDocumentPreview view) => ((TextBlock)view.FindName("PageLabel")).Text;
    private static async Task Until(Func<bool> predicate) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35)); while (!predicate()) await Task.Delay(25, deadline.Token); }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Fingerprint(string path) { var info = new FileInfo(path); return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"); }
    private static T Offscreen<T>(T window) where T : Window { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static string ImagePath(string output, string suffix) => Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + suffix + ".png");
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Save(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
