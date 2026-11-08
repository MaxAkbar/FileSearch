using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

internal static class PdfPreviewFindSmoke
{
    private const string Phrase = "needle phrase";

    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        string root = Path.Combine(Path.GetDirectoryName(output)!, "pdf-find-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pagesPath = PdfFindFixtures.WriteTwoPages(Path.Combine(root, "pages.pdf"));
        string changingPath = PdfFindFixtures.WriteTwoPages(Path.Combine(root, "changing.pdf"));
        string ocrPath = PdfFindFixtures.Write(Path.Combine(root, "ocr-region.pdf"), crop: false, includeText: false);
        string[] rotations = new[] { 0, 90, 180, 270 }.Select(rotation =>
            PdfFindFixtures.Write(Path.Combine(root, "crop-" + rotation + ".pdf"), rotation, rotateText: rotation == 270)).ToArray();
        string[] sources = [pagesPath, changingPath, ocrPath, .. rotations];
        var hashes = sources.ToDictionary(path => path, Hash);
        DateTime changingModified = File.GetLastWriteTimeUtc(changingPath);
        var launcher = new FakeFileLauncher();
        var settings = new FakeSettingsService(); settings.Current.UseIndex = false;
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain], plain);
        var preview = new FilePreviewService(registry);
        var searcher = new Searcher(new FileWalker(), registry);
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), preview, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher,
            new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(),
            new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var files = sources.ToDictionary(path => path, path => new FileResultViewModel(path, launcher));
        files[pagesPath].AddHit(new Hit(pagesPath, 1, Phrase, [new MatchSpan(0, Phrase.Length)],
            SizeBytes: new FileInfo(pagesPath).Length, ModifiedUtc: File.GetLastWriteTimeUtc(pagesPath), Anchor: SourceAnchor.PdfPage(1)));
        foreach (var file in files.Values) search.Files.Add(file);
        search.SearchPath = root; search.PreviewPaneWidth = 580;
        var window = Offscreen(new MainWindow { DataContext = main, Width = 1440, Height = 900 });
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), preview, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = Offscreen(new QuickSearchWindow { DataContext = quick, Width = 640, Height = 420 });
        var checks = new List<object>();
        try
        {
            window.Show(); await Settle();
            var tabs = Descendants(window).OfType<TabControl>().Single(control => AutomationProperties.GetName(control) == "Result preview views");
            tabs.SelectedIndex = 1; search.SelectedFile = files[pagesPath]; await Settle();
            var view = Descendants(tabs).OfType<FileDocumentPreview>().Single();
            await WaitPreview(view, files[pagesPath]);
            Require(view.PreviewMatchCount == 3 && Query(view).Text == Phrase, "Native PDF hit was not seeded across the loaded page.");
            Require(!ActiveBounds(view).IsEmpty && Descendants(view).OfType<System.Windows.Controls.Image>().Any(image => image.Source is BitmapSource),
                "PDF Find replaced the complete page bitmap or failed to provide a native overlay.");
            checks.Add(new { check = "main-native-pdf-seed", matches = view.PreviewMatchCount, page = Page(view), bounds = ActiveBounds(view).ToString() });
            view.ShowPreviewFind();
            await Find(view, Phrase, true); Require(view.PreviewMatchCount == 2, "PDF Match case included the uppercase phrase.");
            await view.MovePreviewMatchAsync(-1); Require(view.CurrentPreviewMatchNumber == 2, "PDF previous did not wrap.");
            Require(Route(Query(view), Key.F3, ModifierKeys.None), "PDF F3 escaped the local Find scope.");
            await WaitUntil(() => view.CurrentPreviewMatchNumber == 1);
            Require(Route(Query(view), Key.F3, ModifierKeys.Shift), "PDF Shift+F3 escaped the local Find scope.");
            await WaitUntil(() => view.CurrentPreviewMatchNumber == 2);
            await Find(view, "john OR smith"); Require(view.PreviewMatchCount == 1, "PDF Find treated OR as an operator.");
            checks.Add(new { check = "main-pdf-literal-case-wrap", caseSensitive = 2, literalOperators = true, keyboardRouting = "scripted" });

            await Find(view, Phrase);
            Click(view, "NextPage");
            await WaitUntil(() => Page(view) == "2 / 2" && view.PreviewState == "Pdf" && view.PreviewMatchCount == 1);
            Require(view.PreviewFindStatusText.Contains("page", StringComparison.OrdinalIgnoreCase) && view.PreviewFindStatusText.Contains('2'),
                "PDF Find did not explain the current page scope.");
            checks.Add(new { check = "pdf-current-page-scope", matches = view.PreviewMatchCount, page = Page(view), status = view.PreviewFindStatusText });
            Save(window, ImagePath(output, "page-two"));

            Click(view, "PreviousPage");
            search.SelectedFile = files[rotations[0]];
            await WaitPreview(view, files[rotations[0]]);
            await Task.Delay(250);
            Require(Query(view).Text.Length == 0 && view.PreviewMatchCount == 0 && ReferenceEquals(view.Result, files[rotations[0]]),
                "A late PDF page reload published old matches into a different file.");
            checks.Add(new { check = "pdf-page-source-race", state = view.PreviewFindState });

            foreach (string path in rotations)
            {
                search.SelectedFile = files[path]; await WaitPreview(view, files[path]);
                view.ShowPreviewFind(); await Find(view, "H", true);
                Require(view.PreviewMatchCount == 1 && !ActiveBounds(view).IsEmpty, "Rotated/cropped PDF lost its visible glyph overlay: " + path);
                await Find(view, "OUTSIDE", true);
                Require(view.PreviewMatchCount == 0, "Find included a glyph completely outside the visible CropBox.");
                await Find(view, "H", true);
                checks.Add(new { check = "pdf-rotation-crop", file = Path.GetFileName(path), bounds = ActiveBounds(view).ToString() });
                if (path == rotations[1]) Save(window, ImagePath(output, "rotation-90"));
            }

            search.SelectedFile = files[pagesPath]; await WaitPreview(view, files[pagesPath]);
            view.ShowPreviewFind(); await Find(view, Phrase);
            Click(view, "NextPage");
            await WaitUntil(() => Page(view) == "2 / 2" && view.PreviewState == "Pdf" && view.PreviewMatchCount == 1);
            Invoke(view, "SetZoom", .8d); await Settle();
            var scroll = Scroll(view);
            scroll.ScrollToHorizontalOffset(scroll.ExtentWidth * .6 - scroll.ViewportWidth / 2);
            scroll.ScrollToVerticalOffset(scroll.ExtentHeight * .6 - scroll.ViewportHeight / 2);
            await Settle();
            var beforeView = CaptureView(view);
            var expanded = Expand(window, view); var expandedView = (FileDocumentPreview)expanded.Content;
            try
            {
                await WaitPreview(expandedView, files[pagesPath]);
                Require(Page(expandedView) == "2 / 2" && expandedView.PreviewMatchCount == 1 && expandedView.CurrentPreviewMatchNumber == 1 &&
                    Query(expandedView).Text == Phrase && expandedView.IsPreviewFindVisible, "PDF Expand lost its page or completed Find state.");
                var afterView = CaptureView(expandedView);
                Require(!afterView.Fit && Math.Abs(beforeView.Zoom - afterView.Zoom) < .00001 &&
                    Math.Abs(beforeView.CenterX - afterView.CenterX) < .02 && Math.Abs(beforeView.CenterY - afterView.CenterY) < .02,
                    "PDF Expand did not preserve manual zoom and normalized viewport center.");
                checks.Add(new { check = "expanded-pdf-manual-view", before = beforeView, after = afterView, page = Page(expandedView) });
                Save(expanded, ImagePath(output, "expanded-zoom"));
            }
            finally { expanded.Close(); }

            view.ClosePreviewFind();
            Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "Fit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            expanded = Expand(window, view); expandedView = (FileDocumentPreview)expanded.Content;
            try
            {
                await WaitPreview(expandedView, files[pagesPath]);
                Require(CaptureView(expandedView).Fit && !expandedView.IsPreviewFindVisible && expandedView.PreviewMatchCount == 0 &&
                    expandedView.PreviewFindState == "Idle" && Page(expandedView) == "2 / 2", "Expand restarted closed PDF Find or lost Fit/page state.");
                checks.Add(new { check = "expanded-pdf-closed-fit", fit = true, page = Page(expandedView), matches = expandedView.PreviewMatchCount });
            }
            finally { expanded.Close(); }

            var info = new FileInfo(ocrPath);
            var location = new SourceLocator(Page: 1, X: 100, Y: 80, Width: 120, Height: 40, SourceWidth: 500, SourceHeight: 400);
            var ocrFile = new FileResultViewModel(ocrPath, launcher);
            var ocrHit = new Hit(ocrPath, 1, "recognized line", [new MatchSpan(0, 10)], SizeBytes: info.Length,
                ModifiedUtc: info.LastWriteTimeUtc, Locator: location);
            ocrFile.AddHit(ocrHit);
            search.Files.Add(ocrFile); search.SelectedFile = ocrFile;
            await WaitPreview(view, ocrFile, canFind: false);
            Require(OcrRegion(view) == new Rect(.2, .2, .24, .1) && StatusText(view).Contains("Selected OCR line region", StringComparison.Ordinal),
                "A current OCR hit did not retain its correctly normalized whole-line region.");
            Require(!view.CanFindInPreview && view.PreviewMatchCount == 0, "A scanned page silently started new OCR or claimed native text Find coverage.");
            checks.Add(new { check = "existing-pdf-ocr-line-region", region = OcrRegion(view).ToString(), nativeFindAvailable = view.CanFindInPreview,
                sourceIdentityChecked = true, freshOcrStarted = false });
            Save(window, ImagePath(output, "ocr-region"));

            var staleOcr = new FileResultViewModel(ocrPath, launcher); staleOcr.AddHit(ocrHit with { SizeBytes = info.Length + 1 });
            search.Files.Add(staleOcr); search.SelectedFile = staleOcr; await WaitPreview(view, staleOcr, canFind: false);
            Require(OcrRegion(view) is null && StatusText(view).Contains("refresh", StringComparison.OrdinalIgnoreCase), "A stale OCR source identity still drew a region.");
            checks.Add(new { check = "stale-pdf-ocr-rejected", region = "none", status = StatusText(view) });

            search.SelectedFile = files[changingPath]; await WaitPreview(view, files[changingPath]);
            view.ShowPreviewFind(); await Find(view, Phrase);
            try
            {
                // WinRT may retain a mapped section of the rendered PDF. Exercise
                // source-version invalidation through metadata without truncating
                // or rewriting a file whose complete page is still displayed.
                File.SetLastWriteTimeUtc(changingPath, changingModified.AddMinutes(1));
                await view.FindInPreviewAsync(); await Settle();
                Require(view.PreviewState == "Pdf" && view.PreviewFindState == "Unavailable" && !view.CanFindInPreview && view.PreviewMatchCount == 0 &&
                    ActiveBounds(view).IsEmpty, "A changed source retained stale native PDF highlights or destroyed the whole-page preview.");
                checks.Add(new { check = "changed-pdf-source-invalidates", view.PreviewFindState, view.PreviewState, timestampOnly = true });
            }
            finally { File.SetLastWriteTimeUtc(changingPath, changingModified); }

            quick.Results.Add(files[pagesPath]); quickWindow.Show(); quick.SelectedResult = files[pagesPath];
            await quick.PreviewResultCommand.ExecuteAsync(null); await Settle();
            var quickView = Descendants(quickWindow).OfType<FileDocumentPreview>().Single();
            await WaitPreview(quickView, files[pagesPath]);
            quickView.ShowPreviewFind(); await Find(quickView, Phrase);
            Require(quickView.PreviewMatchCount == 3 && quickWindow.ActualWidth == 640 && quickWindow.ActualHeight >= 540 &&
                Query(quickView).ActualWidth > 0 && !ActiveBounds(quickView).IsEmpty, "Quick PDF Find lost matches or minimum-width controls.");
            checks.Add(new { check = "quick-pdf-find-layout", matches = quickView.PreviewMatchCount, quickWindow.ActualWidth, quickWindow.ActualHeight });
            Save(quickWindow, ImagePath(output, "quick-minimum"));
            quick.IsPreviewVisible = false; await Settle();
            await quick.PreviewResultCommand.ExecuteAsync(null);
            await WaitPreview(quickView, files[pagesPath]);
            Require(quickView.IsPreviewFindVisible && Query(quickView).Text == Phrase && quickView.PreviewMatchCount == 3 &&
                quickWindow.MinHeight >= 540 && quickWindow.ActualHeight >= 540,
                "Reopening the same Quick PDF preview lost its open Find query, matches or expanded layout.");
            checks.Add(new { check = "quick-pdf-hide-reopen", matches = quickView.PreviewMatchCount,
                query = Query(quickView).Text, quickWindow.MinHeight, quickWindow.ActualHeight });
            Save(quickWindow, ImagePath(output, "quick-reopened"));
            Require(Route(Query(quickView), Key.Escape, ModifierKeys.None), "Quick PDF Escape escaped local Find.");
            await Settle();
            Require(!quickView.IsPreviewFindVisible && quickWindow.MinHeight == 420 && quickWindow.ActualHeight == 420,
                "Closing Quick PDF Find did not restore its compact height.");
            checks.Add(new { check = "quick-pdf-compact-restored", quickWindow.ActualHeight, quickWindow.MinHeight });

            bool unchanged = sources.All(path => Hash(path) == hashes[path]);
            Require(unchanged, "PDF preview Find changed source bytes.");
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                passed = true, fixtureRoot = root, sourcesUnchanged = unchanged, checks,
                installedModelsRequired = false, powerToysRequired = false,
                scope = "Scripted generated-fixture real main/Quick/expanded native PDF Find, existing OCR-region, source identity and viewport checks. Not fresh OCR recognition, physical input, screen-reader, OS high-contrast or performance qualification.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(output + ".failure.json", JsonSerializer.Serialize(new
            {
                error = error.ToString(), checks,
                previews = System.Windows.Application.Current.Windows.Cast<Window>().SelectMany(host => Descendants(host).OfType<FileDocumentPreview>())
                    .Select(view => new { source = view.Result?.FullPath, view.PreviewState, view.PreviewFindState, view.PreviewFindStatusText,
                        view.PreviewMatchCount, view.CurrentPreviewMatchNumber, page = Page(view), status = StatusText(view) }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Save(window, ImagePath(output, "failure-main"));
            throw;
        }
        finally
        {
            File.SetLastWriteTimeUtc(changingPath, changingModified);
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            foreach (Window owned in quickWindow.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            quickWindow.Close(); window.Close();
        }
    }

    private static async Task Find(FileDocumentPreview view, string query, bool matchCase = false)
    {
        Query(view).Text = query; ((CheckBox)view.FindName("PreviewFindCase")).IsChecked = matchCase;
        await view.FindInPreviewAsync(); await Settle();
        Require(view.PreviewFindState is "Ready" or "NoMatches" or "Partial", "PDF Find failed: " + view.PreviewFindStatusText);
    }
    private static async Task WaitPreview(FileDocumentPreview view, FileResultViewModel file, bool canFind = true)
    {
        await WaitUntil(() => ReferenceEquals(view.Result, file) && view.PreviewState == "Pdf" && (!canFind || view.CanFindInPreview));
        await Settle();
    }
    private static Window Expand(Window owner, FileDocumentPreview view)
    { Click(view, "ExpandButton"); return owner.OwnedWindows.Cast<Window>().Single(); }
    private static void Click(FileDocumentPreview view, string name) => ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static TextBox Query(FileDocumentPreview view) => (TextBox)view.FindName("PreviewFindQuery");
    private static string Page(FileDocumentPreview view) => ((TextBlock)view.FindName("PageLabel")).Text;
    private static string StatusText(FileDocumentPreview view) => ((TextBlock)view.FindName("Status")).Text;
    private static object? Field(FileDocumentPreview view, string name) => typeof(FileDocumentPreview).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);
    private static void Invoke(FileDocumentPreview view, string name, params object[] args) => typeof(FileDocumentPreview).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args);
    private static Rect ActiveBounds(FileDocumentPreview view)
    { var overlay = Field(view, "_pdfOverlay"); return overlay is null ? Rect.Empty : (Rect)overlay.GetType().GetProperty("ActiveBounds")!.GetValue(overlay)!; }
    private static Rect? OcrRegion(FileDocumentPreview view)
    { var overlay = Field(view, "_pdfOverlay"); return overlay is null ? null : (Rect?)overlay.GetType().GetProperty("OcrRegion")!.GetValue(overlay); }
    private static ScrollViewer Scroll(FileDocumentPreview view) => (ScrollViewer)Field(view, "_pdfScroll")!;
    private sealed record ViewState(bool Fit, double Zoom, double CenterX, double CenterY);
    private static ViewState CaptureView(FileDocumentPreview view)
    {
        var scroll = Scroll(view);
        return new((bool)Field(view, "_fitImage")!, (double)Field(view, "_zoom")!,
            (scroll.HorizontalOffset + scroll.ViewportWidth / 2) / scroll.ExtentWidth,
            (scroll.VerticalOffset + scroll.ViewportHeight / 2) / scroll.ExtentHeight);
    }
    private static bool Route(object source, Key key, ModifierKeys modifiers)
    {
        object?[] args = [source, key, modifiers, false];
        bool reserved = (bool)typeof(FileDocumentPreview).GetMethod("TryRoutePreviewFindKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
        return reserved && (bool)args[3]!;
    }
    private static T Offscreen<T>(T window) where T : Window
    { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitUntil(Func<bool> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)); while (!condition()) await Task.Delay(25, deadline.Token); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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
