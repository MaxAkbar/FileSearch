using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Databases;
using FileSearch.Core.Tests;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Services;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;
using ICSharpCode.AvalonEdit;
using Microsoft.Web.WebView2.Wpf;

internal static class FilePreviewSmoke
{
    public static async Task RunAsync(string output, bool inspect)
    {
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "fixtures");
        var root = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        PreviewFixtures.Create(root);
        await DatabaseTestFiles.CreateAsync(root, "records.sqlite", DatabaseKind.Sqlite, 275, CancellationToken.None);
        await DatabaseTestFiles.CreateAsync(root, "records.csdb", DatabaseKind.CSharpDb, 275, CancellationToken.None);
        File.AppendAllText(Path.Combine(root, "message.eml"), new string('x', 2_100_000) + "<p>Full-size message ending</p>");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.SeaGreen, null, new Rect(0, 0, 480, 280));
            drawing.DrawText(new FormattedText("FileSearch image preview", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 30, Brushes.White, 1), new Point(35, 115));
        }
        var bitmap = new RenderTargetBitmap(480, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var image = File.Create(Path.Combine(root, "image.png"))) encoder.Save(image);
        File.WriteAllText(Path.Combine(root, "guide.md"), "# FileSearch preview\n\n**Formatted Markdown**, rendered locally.\n\n| Format | Preview |\n| --- | --- |\n| PDF | Pages and zoom |\n| Code | Syntax highlighting |\n\n<script>window.previewUnsafe=true</script>\n\n![blocked](https://example.invalid/blocked.png)");
        File.WriteAllText(Path.Combine(root, "source.cs"), "using System;\n\npublic static class PreviewExample\n{\n    // The source file remains unchanged.\n    public static string Message => \"FileSearch preview\";\n}\n");
        File.WriteAllText(Path.Combine(root, "image.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='480' height='240'><rect width='480' height='240' fill='#167dc4'/><text x='30' y='130' font-size='30' fill='white'>FileSearch SVG preview</text></svg>");
        using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(Path.Combine(root, "office.docx"), DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var part = word.AddMainDocumentPart();
            part.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("FileSearch installed Office handler")))));
        }
        File.WriteAllBytes(Path.Combine(root, "binary.filesearchunknown"), [0, 1, 0, 255]);
        var launcher = new FakeFileLauncher();
        var settings = new FakeSettingsService();
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain], plain);
        var previewService = new FilePreviewService(registry);
        var searcher = new Searcher(new FileWalker(), registry);
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), previewService, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher, new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var files = Directory.GetFiles(root).Select(path => new FileResultViewModel(path, launcher)).ToArray();
        foreach (var file in files.Where(file => file.Extension == "pdf"))
            file.AddHit(new Hit(file.FullPath, 8, "partial passage on the second page", [], Anchor: SourceAnchor.PdfPage(2)));
        files.Single(file => file.Extension == "xlsx").AddHit(new Hit("workbook.xlsx", 2, "[Data] First\tMatching cell\t3", [new MatchSpan(13, 8)], Anchor: new SourceAnchor(SourceAnchorKind.Excel, "Data!B501:BB501", Sheet: "Data", Cell: "B501:BB501")));
        files.Single(file => file.Extension == "zip").AddHit(new Hit("members.zip", 4, "Matching archive member", [], Anchor: new SourceAnchor(SourceAnchorKind.Archive, "folder/match.txt", MemberPath: "folder/match.txt")));
        files.Single(file => file.Extension == "epub").AddHit(new Hit("book.epub", 2, "[Book/two.xhtml] Matching chapter", []));
        foreach (var file in files.Where(file => file.Extension is "sqlite" or "csdb"))
        {
            await foreach (var line in new DatabaseExtractor().ExtractAsync(file.FullPath, CancellationToken.None))
                if (line.Content.Contains("needle", StringComparison.Ordinal)) file.AddHit(new Hit(file.FullPath, line.Number, line.Content, [new MatchSpan(0, 6)], Anchor: line.Anchor));
        }
        foreach (var file in files) search.Files.Add(file);
        search.SearchPath = root;
        search.PreviewPaneWidth = 580;
        search.SelectedFile = files.First();
        var window = new MainWindow { DataContext = main, Width = 1400, Height = 900, Title = "FileSearch preview verification" };
        var rows = new List<object>();
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var tabs = Descendants(window).OfType<TabControl>().Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == "Result preview views");
        tabs.SelectedIndex = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var view = Descendants(tabs).OfType<FileDocumentPreview>().Single();
        int? nativeProcessId = null;
        bool checkedCrashRecovery = false;
        foreach (var file in files)
        {
            search.SelectedFile = file;
            await Wait(view);
            if (file.Extension is "sqlite" or "csdb")
            {
                var database = Descendants(view).OfType<DatabasePreviewControl>().Single();
                if (view.PreviewState != "Database" || database.PreviewState != "Ready" || database.CurrentLocation?.RowNumber != 275 || database.CurrentLocation.Column != "Notes")
                    throw new InvalidOperationException("Database preview did not open the matching record: " + file.FileName);
                var grid = Descendants(database).OfType<System.Windows.Controls.DataGrid>().Single();
                if (!grid.IsReadOnly || !grid.Items.Cast<System.Data.DataRowView>().Any(row => row.Row.ItemArray.Any(value => value?.ToString()?.Contains("End of record", StringComparison.Ordinal) == true))) throw new InvalidOperationException("Complete database record was not available.");
                AssertVisibleDatabaseCell(grid);
                ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var popup = window.OwnedWindows.Cast<Window>().Single();
                await Wait((FileDocumentPreview)popup.Content);
                var expandedDb = Descendants(popup).OfType<DatabasePreviewControl>().Single();
                if (expandedDb.CurrentLocation?.RowNumber != 275 || expandedDb.CurrentLocation.Column != "Notes") throw new InvalidOperationException("Expand lost its database location.");
                popup.Close();
                var selector = Descendants(database).OfType<ComboBox>().Single(control => System.Windows.Automation.AutomationProperties.GetName(control) == "Database table");
                selector.SelectedItem = selector.Items.Cast<DatabaseTable>().Single(table => table.Name == "EmptyTable");
                for (int attempt = 0; database.PreviewState == "Loading" && attempt < 100; attempt++) await Task.Delay(25);
                if (database.PreviewState != "Empty") throw new InvalidOperationException("Empty database table did not render a useful state.");
                rows.Add(new { database = file.FileName, matchingRow = 275, matchingColumn = "Notes", completeRecord = true, expandedLocation = true });
            }
            if (file.Extension == "docx" && (await DocumentPreviewService.LoadAsync(file.FullPath, CancellationToken.None)).Kind == DocumentPreviewKind.Shell && view.PreviewState != "Shell")
                throw new InvalidOperationException("The installed Office handler did not render the fixture.");
            if (file.Extension is "csv" or "xlsx" or "ods" && view.PreviewState != "Spreadsheet") throw new InvalidOperationException("Spreadsheet view failed: " + file.FileName);
            if (file.Extension is "epub" or "eml" or "odt" or "odp" or "rtf" && view.PreviewState != "Html") throw new InvalidOperationException("Formatted view failed: " + file.FileName);
            if (file.Extension is "webp" or "gif" or "tiff" && view.PreviewState != "Image") throw new InvalidOperationException("Image view failed: " + file.FileName);
            if (file.Extension == "xlsx")
            {
                var grid = Descendants(view).OfType<SpreadsheetPreviewControl>().Single();
                if (grid.CurrentPosition.InitialSheet != "Data" || grid.CurrentPosition.InitialRow != 501 || grid.CurrentPosition.InitialColumn != 52) throw new InvalidOperationException("Matching spreadsheet cell was not selected.");
                ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var expandedGrid = window.OwnedWindows.Cast<Window>().Single();
                await Wait((FileDocumentPreview)expandedGrid.Content);
                if (Descendants(expandedGrid).OfType<SpreadsheetPreviewControl>().Single().CurrentPosition.InitialColumn != 52) throw new InvalidOperationException("Expanded sheet lost its cell.");
                expandedGrid.Close();
            }
            if (file.Extension == "zip")
            {
                var child = Descendants(view).OfType<FileDocumentPreview>().Single();
                await Wait(child);
                if (!Descendants(child).OfType<TextEditor>().Single().Text.Contains("End of whole member")) throw new InvalidOperationException("Archive did not show the whole matching member.");
                var selector = (ComboBox)view.FindName("PartSelector");
                selector.SelectedIndex = 0;
                await Wait(view);
                child = Descendants(view).OfType<FileDocumentPreview>().Single();
                await Wait(child);
                if (!Descendants(child).OfType<TextEditor>().Single().Text.Contains("First member")) throw new InvalidOperationException("Archive member selection failed.");
            }
            if (file.Extension == "tiff")
            {
                ((Button)view.FindName("NextPage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(view);
                if (((TextBlock)view.FindName("PageLabel")).Text != "2 / 2") throw new InvalidOperationException("TIFF frame navigation failed.");
            }
            if (file.Extension is "epub" or "odp")
            {
                var selector = (ComboBox)view.FindName("PartSelector");
                selector.SelectedIndex = 1;
                await Wait(view);
                var browser = Descendants(view).OfType<WebView2>().Single();
                var text = await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                if (!text.Contains(file.Extension == "epub" ? "End of whole chapter" : "Whole second slide")) throw new InvalidOperationException("Chapter or slide selection failed.");
            }
            if (file.Extension == "eml")
            {
                var browser = Descendants(view).OfType<WebView2>().Single();
                if (await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('Full-size message ending')") != "true") throw new InvalidOperationException("The complete large message was not rendered.");
                if (await browser.CoreWebView2.ExecuteScriptAsync("typeof window.previewUnsafe") != "\"undefined\"") throw new InvalidOperationException("Mail script executed.");
            }
            if (view.PreviewState == "Shell")
            {
                var native = typeof(FileDocumentPreview).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                nativeProcessId = ((Process)native.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!).Id;
                if (!checkedCrashRecovery)
                {
                    using (var process = Process.GetProcessById(nativeProcessId.Value)) process.Kill();
                    var clock = Stopwatch.StartNew();
                    while (view.PreviewState != "Unavailable" && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
                    if (view.PreviewState != "Unavailable") throw new InvalidOperationException("Handler failure did not recover to a fallback.");
                    tabs.SelectedIndex = 0;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    tabs.SelectedIndex = 1;
                    await Wait(view);
                    if (view.PreviewState != "Shell") throw new InvalidOperationException("Handler preview could not be retried.");
                    native = typeof(FileDocumentPreview).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                    nativeProcessId = ((Process)native.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!).Id;
                    rows.Add(new { handlerCrashRecovery = true });
                    checkedCrashRecovery = true;
                }
            }
            string? htmlTitle = null;
            if (Path.GetExtension(file.FullPath) == ".md")
            {
                var browser = Descendants(view).OfType<WebView2>().FirstOrDefault();
                if (browser is null)
                {
                    File.WriteAllText(output + ".state.txt", view.PreviewState + " " + ((TextBlock)view.FindName("Status")).Text);
                    if (inspect) await Task.Delay(TimeSpan.FromMinutes(2));
                    throw new InvalidOperationException("Markdown browser unavailable: " + view.PreviewState);
                }
                var clock = Stopwatch.StartNew();
                do
                {
                    htmlTitle = await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('h1')?.textContent");
                    if (htmlTitle.Contains("FileSearch preview")) break;
                    await Task.Delay(50);
                } while (clock.Elapsed < TimeSpan.FromSeconds(10));
                if (!htmlTitle.Contains("FileSearch preview") || browser.CoreWebView2.Settings.IsScriptEnabled)
                {
                    var source = await browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                    File.WriteAllText(output + ".markdown-debug.json", JsonSerializer.Serialize(new { htmlTitle, source, url = browser.Source?.ToString(), state = view.PreviewState }));
                    if (inspect) await Task.Delay(TimeSpan.FromMinutes(2));
                    throw new InvalidOperationException("Markdown was not rendered safely.");
                }
                var unsafeScript = await browser.CoreWebView2.ExecuteScriptAsync("typeof window.previewUnsafe");
                if (unsafeScript != "\"undefined\"") throw new InvalidOperationException("Document script ran.");
            }
            if (file.FullPath.EndsWith("source.cs", StringComparison.Ordinal))
            {
                var editor = Descendants(view).OfType<TextEditor>().Single();
                if (!editor.IsReadOnly || editor.SyntaxHighlighting is null || !editor.Text.Contains("PreviewExample"))
                    throw new InvalidOperationException("Code preview was not populated.");
            }
            if (file.FullPath.EndsWith(".pdf", StringComparison.Ordinal))
            {
                if (((TextBlock)view.FindName("PageLabel")).Text != "2 / 2") throw new InvalidOperationException("The partial hit did not open its actual page.");
                var scroll = (ScrollViewer)((ContentControl)view.FindName("Body")).Content;
                var fitted = (Viewbox)scroll.Content;
                if (fitted.ActualWidth > scroll.ViewportWidth + 1 || fitted.ActualHeight > scroll.ViewportHeight + 1)
                    throw new InvalidOperationException("Fit did not show the complete PDF page.");
                ((Button)view.FindName("PreviousPage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(view);
                if (((TextBlock)view.FindName("PageLabel")).Text != "1 / 2") throw new InvalidOperationException("PDF paging failed.");
                ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var pdfWindow = window.OwnedWindows.Cast<Window>().Single();
                var pdfView = (FileDocumentPreview)pdfWindow.Content;
                await Wait(pdfView);
                if (((TextBlock)pdfView.FindName("PageLabel")).Text != "1 / 2") throw new InvalidOperationException("Expanded preview did not preserve the current page.");
                pdfWindow.Close();
                rows.Add(new { wholeMatchingPage = 2, fitCompletePage = true, expandedPagePreserved = 1 });
            }
            rows.Add(new { file = file.FileName, state = view.PreviewState, htmlTitle });
        }
        // A selection replaced while Markdown is loading must not publish its stale browser.
        search.SelectedFile = files.Single(file => file.Extension == "md");
        search.SelectedFile = files.Single(file => file.Extension == "cs");
        await Wait(view);
        if (view.PreviewState != "Text") throw new InvalidOperationException("Stale preview replaced the latest selection.");
        if (nativeProcessId is { } pid)
        {
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("Native preview process was not cleaned up."); }
            catch (ArgumentException) { }
            rows.Add(new { nativeProcessCleanup = true });
        }
        ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var expanded = window.OwnedWindows.Cast<Window>().Single();
        if (expanded.Content is not FileDocumentPreview) throw new InvalidOperationException("Expand did not reuse the shared preview.");
        expanded.Close();
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), previewService, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = new QuickSearchWindow { DataContext = quick, Title = "Quick Search preview verification", Width = 900, Height = 750 };
        quickWindow.Show();
        foreach (var file in files) quick.Results.Add(file);
        quick.SelectedResult = files.Single(file => file.Extension == "cs");
        await quick.PreviewResultCommand.ExecuteAsync(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var quickView = Descendants(quickWindow).OfType<FileDocumentPreview>().Single();
        await Wait(quickView);
        if (quickView.PreviewState != "Text") throw new InvalidOperationException("Quick Search did not preview its selection.");
        quick.SelectedResult = files.Single(file => file.Extension == "png");
        await Wait(quickView);
        if (quickView.PreviewState != "Image") throw new InvalidOperationException("Quick Search did not follow the new selection.");
        quick.SelectedResult = files.Single(file => file.Extension == "pdf");
        await Wait(quickView);
        if (((TextBlock)quickView.FindName("PageLabel")).Text != "2 / 2") throw new InvalidOperationException("Quick Search did not open the matching PDF page.");
        rows.Add(new { quickMatchingPage = 2 });
        foreach (var file in files.Where(file => file.Extension is "xlsx" or "zip" or "epub" or "eml" or "gif" or "tiff" or "odt" or "ods" or "odp" or "rtf" or "webp" or "sqlite" or "csdb"))
        {
            quick.SelectedResult = file;
            try { await Wait(quickView); }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Quick preview did not settle for {file.FileName}: state={quickView.PreviewState}, loaded={quickView.IsLoaded}, visible={quickView.IsVisible}, selected={quickView.Result?.FullPath}.", error);
            }
            if (quickView.PreviewState is "Unavailable" or "Details") throw new InvalidOperationException("Quick Search viewer failed: " + file.FileName);
            if (file.Extension is "sqlite" or "csdb")
            {
                if (Descendants(quickView).OfType<DatabasePreviewControl>().Single().CurrentLocation?.RowNumber != 275) throw new InvalidOperationException("Quick Search did not select the database record.");
                AssertVisibleDatabaseCell(Descendants(quickView).OfType<System.Windows.Controls.DataGrid>().Single());
            }
            rows.Add(new { quickFile = file.FileName, state = quickView.PreviewState });
        }
        rows.Add(new { quickSelection = "Image", sharedControl = true });
        quick.SelectedResult = files.Single(file => file.Extension == "xlsx");
        quickWindow.Width = 760;
        quickWindow.Height = 540;
        await Wait(quickView);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var visibleGrid = Descendants(quickView).OfType<System.Windows.Controls.DataGrid>().Single();
        if (visibleGrid.ActualHeight < 55 || !Descendants(visibleGrid).OfType<DataGridRow>().Any(row => row.ActualHeight >= 16)) throw new InvalidOperationException("Quick Search did not leave room for spreadsheet rows.");
        var matchingCell = Descendants(visibleGrid).OfType<System.Windows.Controls.DataGridCell>().FirstOrDefault(cell => cell.IsSelected && cell.Column.Header?.ToString() == "AZ");
        if (matchingCell is null || matchingCell.TransformToAncestor(visibleGrid).TransformBounds(new Rect(matchingCell.RenderSize)) is var bounds && (bounds.Left < 0 || bounds.Right > visibleGrid.ActualWidth + 2))
        {
            File.WriteAllText(output + ".grid.json", JsonSerializer.Serialize(new { width = visibleGrid.ActualWidth, current = visibleGrid.CurrentCell.Column?.Header?.ToString(), selected = visibleGrid.SelectedCells.Select(cell => cell.Column.Header?.ToString()).ToArray(),
                cells = Descendants(visibleGrid).OfType<System.Windows.Controls.DataGridCell>().Select(cell => new { header = cell.Column.Header?.ToString(), cell.IsSelected, bounds = cell.TransformToAncestor(visibleGrid).TransformBounds(new Rect(cell.RenderSize)).ToString() }).ToArray(),
                scrolls = Descendants(visibleGrid).OfType<ScrollViewer>().Select(scroll => new { scroll.HorizontalOffset, scroll.ScrollableWidth, scroll.ViewportWidth, scroll.CanContentScroll }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            if (inspect) await Task.Delay(TimeSpan.FromMinutes(1));
            throw new InvalidOperationException("Matching cell was not scrolled into the visible viewport.");
        }
        rows.Add(new { quickDefaultSizeVisibleSheet = true, gridHeight = visibleGrid.ActualHeight });
        await VerifyDatabaseOpenAsync(root, searcher, files, window, quick, quickWindow, rows);
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        if (inspect)
        {
            quick.SelectedResult = files.Single(file => file.Extension == "xlsx");
            quickWindow.Height = 750;
            search.SelectedFile = files.Single(file => file.Extension == "eml");
            await Wait(quickView);
            await Wait(view);
            await Task.Delay(TimeSpan.FromMinutes(3));
        }
        quickWindow.Close();
        window.Close();
    }

    private static async Task VerifyDatabaseOpenAsync(string root, Searcher searcher, FileResultViewModel[] files,
        Window mainWindow, QuickSearchViewModel quick, Window quickWindow, List<object> rows)
    {
        // Exercise the real Open command: fake launchers cannot catch missing Windows file associations.
        var directory = Path.Combine(root, "filename-open");
        Directory.CreateDirectory(directory);
        foreach (var file in files.Where(file => file.Extension is "sqlite" or "csdb"))
            File.Copy(file.FullPath, Path.Combine(directory, file.Extension + ".db"));
        var request = new SearchRequest(MatchAllQuery.Instance, [directory], new WalkerOptions { IncludeGlobs = ["*.db"] }, SearchTarget: SearchTarget.FileNames);
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, CancellationToken.None)) hits.Add(hit);
        if (hits.Count != 2 || hits.Any(hit => hit.Kind != HitKind.Metadata || hit.Database is not null))
            throw new InvalidOperationException("The *.db regression did not produce both filename-only database results.");
        var launcher = new FileLauncher();
        foreach (var hit in hits)
        {
            var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hit.Path));
            var result = new FileResultViewModel(hit.Path, launcher);
            result.AddHit(hit);
            mainWindow.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var before = Application.Current.Windows.Cast<Window>().ToArray();
            await result.OpenCommand.ExecuteAsync(null);
            var popup = Application.Current.Windows.Cast<Window>().Except(before).Single();
            await Wait((FileDocumentPreview)popup.Content);
            var database = Descendants(popup).OfType<DatabasePreviewControl>().Single();
            if (database.PreviewState != "Ready" || database.CurrentLocation?.RowNumber != 1)
                throw new InvalidOperationException("Opening a filename-only database did not show its first record.");
            popup.Close();

            quick.Results.Add(result);
            quick.SelectedResult = result;
            quickWindow.Show();
            quickWindow.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            before = Application.Current.Windows.Cast<Window>().ToArray();
            await quick.OpenResultCommand.ExecuteAsync(null);
            popup = Application.Current.Windows.Cast<Window>().Except(before).Single();
            await Wait((FileDocumentPreview)popup.Content);
            if (quickWindow.IsVisible || !popup.IsVisible || popup.Owner == quickWindow ||
                Descendants(popup).OfType<DatabasePreviewControl>().Single().PreviewState != "Ready")
                throw new InvalidOperationException("The database viewer did not survive Quick Search dismissal.");
            popup.Close();
            if (!original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hit.Path))))
                throw new InvalidOperationException("Opening the database changed its source bytes.");
            rows.Add(new { filenameOpen = Path.GetFileName(hit.Path), builtInViewer = true, quickSearchOpen = true, sourceUnchanged = true });
        }

        foreach (var file in files.Where(file => file.Extension is "sqlite" or "csdb"))
        {
            var result = new FileResultViewModel(file.FullPath, launcher);
            result.AddHits(file.Hits);
            var before = Application.Current.Windows.Cast<Window>().ToArray();
            await result.OpenAtLineAsync(file.Hits.Single().LineNumber);
            var popup = Application.Current.Windows.Cast<Window>().Except(before).Single();
            await Wait((FileDocumentPreview)popup.Content);
            var database = Descendants(popup).OfType<DatabasePreviewControl>().Single();
            if (database.PreviewState != "Ready" || database.CurrentLocation?.RowNumber != 275 || database.CurrentLocation.Column != "Notes")
                throw new InvalidOperationException("Open at line lost its database record location.");
            AssertVisibleDatabaseCell(Descendants(database).OfType<System.Windows.Controls.DataGrid>().Single());
            popup.Close();
            rows.Add(new { databaseOpenAtLine = file.FileName, matchingRow = 275, matchingColumn = "Notes" });
        }

        var unsupported = Path.Combine(directory, "unsupported.db");
        File.WriteAllText(unsupported, "This is not a SQLite or CSharpDB database.");
        var openWindows = Application.Current.Windows.Cast<Window>().ToArray();
        launcher.Open(unsupported);
        var fallback = Application.Current.Windows.Cast<Window>().Except(openWindows).Single();
        var preview = (FileDocumentPreview)fallback.Content;
        await Wait(preview);
        if (preview.PreviewState != "Unavailable") throw new InvalidOperationException("An unsupported database did not show a safe preview error.");
        fallback.Close();
        rows.Add(new { unsupportedDatabaseOpen = "Unavailable", appRemainedOpen = mainWindow.IsVisible });
        quickWindow.Show();
    }

    private static async Task Wait(FileDocumentPreview view)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (view.IsLoaded && view.IsVisible && view.PreviewState is not "Empty" and not "Loading") return;
        } while (clock.Elapsed < TimeSpan.FromSeconds(25));
        throw new TimeoutException("Preview did not settle.");
    }
    private static void AssertVisibleDatabaseCell(System.Windows.Controls.DataGrid grid)
    {
        var cell = Descendants(grid).OfType<System.Windows.Controls.DataGridCell>().FirstOrDefault(cell => cell.IsSelected && cell.Column.Header?.ToString() == "Notes" && cell.DataContext is System.Data.DataRowView row && (long)row["r"] == 275);
        if (cell is null) throw new InvalidOperationException("The matching database cell was not realized and selected.");
        var bounds = cell.TransformToAncestor(grid).TransformBounds(new Rect(cell.RenderSize));
        if (bounds.Top < 0 || bounds.Bottom > grid.ActualHeight + 2 || bounds.Left < 0 || bounds.Right > grid.ActualWidth + 2)
            throw new InvalidOperationException("The matching database cell was not scrolled into view.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
