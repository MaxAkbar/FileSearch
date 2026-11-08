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
using ICSharpCode.AvalonEdit;
using Microsoft.Web.WebView2.Wpf;

internal static class DocumentPreviewFindSmoke
{
    private const string Phrase = "renewal 東京";

    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        string root = Path.Combine(Path.GetDirectoryName(output)!, "document-find-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string textPath = Path.Combine(root, "passages.txt"), otherPath = Path.Combine(root, "other.txt");
        string markdownPath = Path.Combine(root, "formatted.md"), mailPath = Path.Combine(root, "message.eml");
        string cappedPath = Path.Combine(root, "many.log"), truncatedPath = Path.Combine(root, "long.log");
        string truncatedMarkdownPath = Path.Combine(root, "long.md");
        string[] lines = Enumerable.Range(1, 600).Select(number => "Ordinary source line " + number).ToArray();
        lines[1] = Phrase; lines[449] = Phrase; lines[579] = "RENEWAL 東京";
        lines[589] = "john OR smith"; lines[590] = "john smith";
        File.WriteAllText(textPath, string.Join('\n', lines));
        File.WriteAllText(otherPath, "Different source without the requested phrase.\nFinal whole-file line.");
        File.WriteAllText(markdownPath, "# Whole formatted preview\n\nFirst **renewal** 東京.\n\nSecond renewal 東京.\n\nUPPER RENEWAL 東京.\n\nLiteral john OR smith.\n");
        using (var message = new MimeKit.MimeMessage())
        {
            message.Subject = "Read-only Find";
            var body = new MimeKit.BodyBuilder { HtmlBody =
                "<h1>Whole mail preview</h1><p>First renewal 東京.</p><p>Second renewal 東京.</p><p>RENEWAL 東京.</p>" +
                "<p>Literal john OR smith.</p><img src='cid:inline-image'>" +
                "<div contenteditable='true'>Original editable attribute must be removed</div><script>window.previewUnsafe=true</script><img src='https://example.invalid/tracker.png'>" };
            var image = body.LinkedResources.Add("inline.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="),
                new MimeKit.ContentType("image", "png"));
            image.ContentId = "inline-image";
            message.Body = body.ToMessageBody();
            await message.WriteToAsync(mailPath, CancellationToken.None);
        }
        File.WriteAllText(cappedPath, string.Concat(Enumerable.Repeat("z ", 10_020)));
        File.WriteAllText(truncatedPath, "visible boundary phrase\n" + new string('x', 250_100) + "\nhidden tail phrase");
        File.WriteAllText(truncatedMarkdownPath, "# Partial formatted preview\n\nFirst renewal 東京.\n\nSecond renewal 東京.\n\n" +
            new string('x', 250_100) + "\n\nUnloaded renewal 東京.\n");
        string[] sources = [textPath, otherPath, markdownPath, mailPath, cappedPath, truncatedPath, truncatedMarkdownPath];
        var hashes = sources.ToDictionary(path => path, Hash);
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
        files[textPath].AddHit(new Hit(textPath, 450, Phrase, [new MatchSpan(0, Phrase.Length)]));
        foreach (var file in files.Values) search.Files.Add(file);
        search.SearchPath = root; search.PreviewPaneWidth = 580;
        var window = Offscreen(new MainWindow { DataContext = main, Width = 1440, Height = 900 });
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), preview, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = Offscreen(new QuickSearchWindow { DataContext = quick, Width = 760, Height = 540 });
        var checks = new List<object>();
        try
        {
            window.Show(); await Settle();
            var tabs = Descendants(window).OfType<TabControl>().Single(control => AutomationProperties.GetName(control) == "Result preview views");
            tabs.SelectedIndex = 1;
            search.SelectedFile = files[textPath]; await Settle();
            var view = Descendants(tabs).OfType<FileDocumentPreview>().Single();
            await WaitPreviewAsync(view, files[textPath], "Text");
            var editor = Editor(view);
            Require(editor.IsReadOnly && editor.Text.Contains(lines[^1], StringComparison.Ordinal), "Text preview lost its whole loaded source.");
            Require(Query(view).Text == Phrase && view.PreviewMatchCount == 3 && view.CurrentPreviewMatchNumber == 2,
                "Preferred late hit was not seeded or did not select its matching line.");
            Require(editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber == 450 && editor.SelectedText == Phrase,
                "Seeded text Find did not select the original late line.");
            checks.Add(new { check = "main-seeded-late-hit", matches = view.PreviewMatchCount, current = view.CurrentPreviewMatchNumber, line = 450 });

            await CheckTextFindAsync(view, checks, "main");
            Save(window, ImagePath(output, "main-text"));
            await FindAsync(view, Phrase, true);
            view.ClosePreviewFind();
            Require(Query(view).Text == Phrase && Case(view).IsChecked == true && !view.IsPreviewFindVisible &&
                view.PreviewMatchCount == 0, "Closing text Find discarded its query/case or retained matches.");
            ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var closedExpanded = window.OwnedWindows.Cast<Window>().Single();
            try
            {
                var closedView = (FileDocumentPreview)closedExpanded.Content;
                await WaitPreviewAsync(closedView, files[textPath], "Text");
                Require(Query(closedView).Text == Phrase && Case(closedView).IsChecked == true && !closedView.IsPreviewFindVisible &&
                    closedView.PreviewMatchCount == 0 && closedView.PreviewFindState == "Idle" && Editor(closedView).SelectionLength == 0,
                    "Expand reran closed text Find or lost its retained query/case.");
                checks.Add(new { check = "expanded-closed-text-state", query = Query(closedView).Text,
                    matchCase = Case(closedView).IsChecked, matches = closedView.PreviewMatchCount, state = closedView.PreviewFindState });
            }
            finally { closedExpanded.Close(); }

            view.ShowPreviewFind();
            await FindAsync(view, Phrase, true);
            await view.MovePreviewMatchAsync(1);
            Require(view.CurrentPreviewMatchNumber == 2, "Text match navigation did not reach the second case-sensitive match.");
            ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var expanded = window.OwnedWindows.Cast<Window>().Single();
            var expandedView = (FileDocumentPreview)expanded.Content;
            await WaitPreviewAsync(expandedView, files[textPath], "Text");
            Require(expandedView.IsPreviewFindVisible && Query(expandedView).Text == Phrase && Case(expandedView).IsChecked == true &&
                expandedView.PreviewMatchCount == 2 && expandedView.CurrentPreviewMatchNumber == 2 && Editor(expandedView).SelectedText == Phrase,
                "Expand did not retain text Find query, case, active match and selected text.");
            Save(expanded, ImagePath(output, "expanded-text"));
            checks.Add(new { check = "expanded-text-state", matches = expandedView.PreviewMatchCount, current = expandedView.CurrentPreviewMatchNumber, selected = Editor(expandedView).SelectedText });
            expanded.Close();

            search.SelectedFile = files[markdownPath]; await WaitPreviewAsync(view, files[markdownPath], "Html");
            await CheckHtmlFindAsync(view, checks, "main-markdown");
            Save(window, ImagePath(output, "main-html"));
            await SaveBrowserAsync(view, ImagePath(output, "main-markdown-browser"));
            await CheckExpandedHtmlAsync(window, view, files[markdownPath], output, "markdown", checks);

            search.SelectedFile = files[cappedPath]; await WaitPreviewAsync(view, files[cappedPath], "Text");
            await FindAsync(view, "z");
            Require(view.PreviewMatchCount == 10_000 && view.PreviewFindState == "Partial" &&
                view.PreviewFindStatusText.Contains("Partial", StringComparison.OrdinalIgnoreCase), "The match cap was not visibly partial.");
            checks.Add(new { check = "text-match-cap", matches = view.PreviewMatchCount, state = view.PreviewFindState });
            Query(view).Text = "z";
            var obsolete = view.FindInPreviewAsync();
            Query(view).Text = "replacement phrase";
            await obsolete;
            Require(view.PreviewMatchCount == 0 && view.PreviewFindState == "Idle" && Editor(view).SelectionLength == 0,
                "Editing Find inputs allowed obsolete matches or selection to remain.");
            checks.Add(new { check = "edited-query-invalidates", state = view.PreviewFindState });

            Query(view).Text = "z";
            var stopping = view.FindInPreviewAsync();
            bool stoppedDuringSearch = !stopping.IsCompleted;
            if (stoppedDuringSearch) view.CancelPreviewFind();
            await stopping;
            if (stoppedDuringSearch)
                Require(view.PreviewMatchCount == 0 && view.PreviewFindState == "Canceled", "Stop published a partial text Find success.");
            checks.Add(new { check = "text-stop", stoppedDuringSearch, state = view.PreviewFindState });

            Query(view).Text = "z";
            var changing = view.FindInPreviewAsync();
            search.SelectedFile = files[otherPath];
            await changing; await WaitPreviewAsync(view, files[otherPath], "Text");
            Require(Query(view).Text.Length == 0 && view.PreviewMatchCount == 0 && Editor(view).Text.StartsWith("Different source", StringComparison.Ordinal),
                "Source replacement retained an old query, result or document.");
            checks.Add(new { check = "source-switch-invalidates", state = view.PreviewFindState });

            search.SelectedFile = files[truncatedPath]; await WaitPreviewAsync(view, files[truncatedPath], "Text");
            await FindAsync(view, "hidden tail phrase");
            Require(view.PreviewMatchCount == 0 && view.PreviewFindStatusText.Contains("Partial", StringComparison.OrdinalIgnoreCase),
                "An unloaded tail was searched or truncated coverage was not explained.");
            await FindAsync(view, "visible boundary phrase");
            Require(view.PreviewMatchCount == 1 && view.PreviewFindStatusText.Contains("Partial", StringComparison.OrdinalIgnoreCase),
                "A visible hit hid truncated preview coverage.");
            checks.Add(new { check = "loaded-text-coverage", loaded = Editor(view).Text.Length, status = view.PreviewFindStatusText });

            search.SelectedFile = files[truncatedMarkdownPath]; await WaitPreviewAsync(view, files[truncatedMarkdownPath], "Html");
            view.ShowPreviewFind(); await FindAsync(view, Phrase);
            await MoveToAsync(view, 1);
            RequirePartialHtmlStatus(view, 1);
            await view.MovePreviewMatchAsync(1);
            await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 2);
            RequirePartialHtmlStatus(view, 2);
            await view.MovePreviewMatchAsync(-1);
            await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 1);
            RequirePartialHtmlStatus(view, 1);
            checks.Add(new { check = "loaded-html-coverage-navigation", matches = view.PreviewMatchCount,
                current = view.CurrentPreviewMatchNumber, state = view.PreviewFindState, status = view.PreviewFindStatusText });

            // Start two sources without yielding; only the latest selected source may publish.
            search.SelectedFile = files[markdownPath];
            search.SelectedFile = files[textPath];
            search.SelectedFile = files[otherPath];
            await WaitPreviewAsync(view, files[otherPath], "Text"); await Task.Delay(300);
            Require(view.Result?.ResultKey == files[otherPath].ResultKey && view.PreviewState == "Text" &&
                view.PreviewMatchCount == 0 && Editor(view).Text.StartsWith("Different source", StringComparison.Ordinal),
                "A late formatted preview replaced the most recent text source.");
            checks.Add(new { check = "rapid-source-replacement", state = view.PreviewState });

            search.SelectedFile = files[mailPath]; await WaitPreviewAsync(view, files[mailPath], "Html");
            await CheckHtmlFindAsync(view, checks, "main-mail");
            await CheckHtmlSecurityAsync(view, checks);
            Save(window, ImagePath(output, "mail-security"));
            await SaveBrowserAsync(view, ImagePath(output, "mail-browser"));
            await CheckExpandedHtmlAsync(window, view, files[mailPath], output, "mail", checks);

            foreach (var file in new[] { files[textPath], files[markdownPath] }) quick.Results.Add(file);
            quickWindow.Show(); quick.SelectedResult = files[textPath];
            await quick.PreviewResultCommand.ExecuteAsync(null); await Settle();
            var quickView = Descendants(quickWindow).OfType<FileDocumentPreview>().Single();
            await WaitPreviewAsync(quickView, files[textPath], "Text");
            await CheckTextFindAsync(quickView, checks, "quick");
            quickView.ClosePreviewFind();
            quickWindow.Width = quickWindow.MinWidth;
            quickWindow.Height = quickWindow.MinHeight;
            await Settle();
            double compactHeight = quickWindow.ActualHeight, compactMinimum = quickWindow.MinHeight;
            quickView.ShowPreviewFind();
            await Settle();
            Require(quickView.IsPreviewFindVisible && Query(quickView).ActualWidth > 0 &&
                AutomationProperties.GetName(Query(quickView)) == "Find literal text in preview" && quickWindow.MinHeight >= 540,
                "Quick Find controls were unavailable at minimum width.");
            Save(quickWindow, ImagePath(output, "quick-minimum"));
            checks.Add(new { check = "quick-minimum-layout", quickWindow.ActualWidth, quickWindow.ActualHeight,
                queryWidth = Query(quickView).ActualWidth, state = quickView.PreviewFindState });
            Require(Route(Query(quickView), Key.Escape, ModifierKeys.None, out bool closed) && closed, "Quick Escape was not handled by preview Find.");
            await Settle();
            Require(quickWindow.IsVisible && !quickView.IsPreviewFindVisible && quickWindow.MinHeight == compactMinimum &&
                Math.Abs(quickWindow.ActualHeight - compactHeight) < 1, "Closing Quick Find did not restore the compact preview layout.");
            Save(quickWindow, ImagePath(output, "quick-compact-restored"));
            checks.Add(new { check = "quick-escape-restores-compact", quickWindow.ActualHeight, quickWindow.MinHeight, previewClosed = true });
            quick.SelectedResult = files[markdownPath]; await WaitPreviewAsync(quickView, files[markdownPath], "Html");
            await CheckHtmlFindAsync(quickView, checks, "quick-markdown");
            Save(quickWindow, ImagePath(output, "quick-html"));
            await SaveBrowserAsync(quickView, ImagePath(output, "quick-browser"));

            bool unchanged = sources.All(path => Hash(path) == hashes[path]);
            Require(unchanged, "Document Find changed source bytes.");
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                fixtureRoot = root, passed = true, sourcesUnchanged = unchanged,
                installedModelsRequired = false, powerToysRequired = false,
                scope = "Scripted real main/Quick/expanded text and native-browser Find with generated fixtures and fake settings/shell services. Not physical-input, complete accessibility, or performance qualification.",
                checks,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(output + ".failure.json", JsonSerializer.Serialize(new
            {
                error = error.ToString(), checks,
                previews = System.Windows.Application.Current.Windows.Cast<Window>().SelectMany(host => Descendants(host).OfType<FileDocumentPreview>()).Select(view => new
                {
                    source = view.Result?.FullPath, view.PreviewState, view.PreviewFindState,
                    view.PreviewFindStatusText, view.PreviewMatchCount, view.CurrentPreviewMatchNumber,
                }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Save(window, ImagePath(output, "failure-main"));
            throw;
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            foreach (Window owned in quickWindow.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            quickWindow.Close(); window.Close();
        }
    }

    private static async Task CheckTextFindAsync(FileDocumentPreview view, List<object> checks, string host)
    {
        view.ShowPreviewFind();
        await FindAsync(view, Phrase);
        Require(view.PreviewMatchCount == 3 && view.CurrentPreviewMatchNumber == 1 && Editor(view).SelectedText == Phrase,
            host + ": literal Unicode phrase did not select the first of three matches.");
        await view.MovePreviewMatchAsync(-1);
        Require(view.CurrentPreviewMatchNumber == 3 && Editor(view).SelectedText == "RENEWAL 東京", host + ": previous did not wrap to the final match.");
        Require(Route(Query(view), Key.F3, ModifierKeys.None, out bool handled) && handled, host + ": F3 was not kept inside the preview.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 1);
        Require(Route(Query(view), Key.F3, ModifierKeys.Shift, out handled) && handled, host + ": Shift+F3 was not kept inside the preview.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 3);
        await FindAsync(view, Phrase, true);
        Require(view.PreviewMatchCount == 2, host + ": case-sensitive text Find matched the uppercase variant.");
        await FindAsync(view, "john OR smith");
        Require(view.PreviewMatchCount == 1 && Editor(view).SelectedText == "john OR smith", host + ": OR was treated as an operator rather than literal text.");
        Query(view).Text = "unfinished edit";
        Require(view.PreviewMatchCount == 0 && Editor(view).SelectionLength == 0 && view.PreviewFindState == "Idle", host + ": an edit retained old highlights.");
        Require(Route(Query(view), Key.C, ModifierKeys.Control, out handled) && !handled, host + ": Copy was intercepted by outer file actions.");
        Require(Route(Query(view), Key.Enter, ModifierKeys.None, out handled) && handled, host + ": Enter in Find was allowed to open a file.");
        await WaitUntilAsync(() => view.PreviewFindState != "Searching");
        await FindAsync(view, Phrase);
        Require(Route(Editor(view), Key.F, ModifierKeys.Control, out handled) && handled && view.IsPreviewFindVisible,
            host + ": Ctrl+F did not open the local Find panel.");
        Require(Route(Query(view), Key.Escape, ModifierKeys.None, out handled) && handled && !view.IsPreviewFindVisible &&
            view.PreviewMatchCount == 0 && Editor(view).SelectionLength == 0, host + ": Escape did not close and clear preview Find.");
        view.ShowPreviewFind(); await FindAsync(view, Phrase);
        checks.Add(new { check = host + "-text-find", phrases = 3, caseSensitive = 2, literalOperators = true,
            keyboardScope = true, editorReadOnly = Editor(view).IsReadOnly });
    }

    private static async Task CheckHtmlFindAsync(FileDocumentPreview view, List<object> checks, string host)
    {
        var browser = Descendants(view).OfType<WebView2>().Single();
        Require(!browser.CoreWebView2.Settings.IsScriptEnabled && !browser.CoreWebView2.Settings.AreHostObjectsAllowed,
            host + ": native Find enabled document scripts or host objects.");
        Require(view.CanFindInPreview, host + ": native browser Find is unavailable in this installed WebView2 runtime.");
        view.ShowPreviewFind(); await FindAsync(view, Phrase);
        Require(view.PreviewMatchCount == 3, host + ": browser Find lost phrase/inline or Unicode matches.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber > 0);
        int first = view.CurrentPreviewMatchNumber;
        await view.MovePreviewMatchAsync(1);
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber != first);
        await FindAsync(view, Phrase, true);
        Require(view.PreviewMatchCount == 2, host + ": case-sensitive browser Find matched the uppercase variant.");
        await FindAsync(view, "john OR smith");
        Require(view.PreviewMatchCount == 1, host + ": browser Find treated OR as an operator rather than literal text.");
        Query(view).Text = "not-yet-searched";
        Require(view.PreviewMatchCount == 0 && view.PreviewFindState == "Idle", host + ": browser query editing retained native matches.");
        await FindAsync(view, Phrase);
        await MoveToAsync(view, 1);
        Require(Route(Query(view), Key.Enter, ModifierKeys.None, out bool handled) && handled,
            host + ": Enter in browser Find was allowed to open a file.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 2);
        Require(Route(browser, Key.F3, ModifierKeys.Shift, out handled) && handled,
            host + ": Shift+F3 was not kept inside the formatted preview.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 1);
        Require(Route(browser, Key.F3, ModifierKeys.Shift, out handled) && handled,
            host + ": Shift+F3 wrapping escaped formatted preview Find.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 3);
        Require(Route(Query(view), Key.Enter, ModifierKeys.None, out handled) && handled,
            host + ": Enter did not stay within browser Find while wrapping.");
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber == 1);
        Require(Route(browser, Key.F, ModifierKeys.Control, out handled) && handled && view.IsPreviewFindVisible,
            host + ": browser focus did not route Ctrl+F to the shared Find panel.");
        Require(!browser.CoreWebView2.Settings.IsScriptEnabled, host + ": Find changed document script permissions.");
        checks.Add(new { check = host + "-browser-find", matches = view.PreviewMatchCount,
            scriptsDisabled = !browser.CoreWebView2.Settings.IsScriptEnabled, nativeTextMatching = true });
    }

    private static async Task CheckExpandedHtmlAsync(Window owner, FileDocumentPreview view, FileResultViewModel file,
        string output, string fixture, List<object> checks)
    {
        await FindAsync(view, Phrase, true);
        await MoveToAsync(view, 2);
        var originalBrowser = Descendants(view).OfType<WebView2>().Single();
        var originalImage = fixture == "mail" ? await LoadedMailImageAsync(originalBrowser, "Original mail preview") : null;
        ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var expanded = owner.OwnedWindows.Cast<Window>().Single();
        try
        {
            var expandedView = (FileDocumentPreview)expanded.Content;
            await WaitPreviewAsync(expandedView, file, "Html");
            Require(expandedView.IsPreviewFindVisible && Query(expandedView).Text == Phrase && Case(expandedView).IsChecked == true &&
                expandedView.PreviewMatchCount == 2 && expandedView.CurrentPreviewMatchNumber == 2,
                fixture + ": Expand did not retain formatted preview Find query, case and active occurrence.");
            var expandedImage = fixture == "mail" ?
                await LoadedMailImageAsync(Descendants(expandedView).OfType<WebView2>().Single(), "Expanded mail preview") : null;
            bool regeneratedImage = originalImage is not null && expandedImage is not null && originalImage.Source != expandedImage.Source;
            if (fixture == "mail") Require(regeneratedImage, "Mail fixture did not exercise regenerated embedded image URLs.");
            Save(expanded, ImagePath(output, "expanded-" + fixture));
            await SaveBrowserAsync(expandedView, ImagePath(output, "expanded-" + fixture + "-browser"));
            checks.Add(new { check = "expanded-" + fixture + "-browser-state", matches = expandedView.PreviewMatchCount,
                current = expandedView.CurrentPreviewMatchNumber, regeneratedEmbeddedImage = regeneratedImage,
                originalEmbeddedImage = originalImage, expandedEmbeddedImage = expandedImage });
        }
        finally { expanded.Close(); }
    }

    private sealed record LoadedMailImage(string Source, int NaturalWidth);

    private static async Task<LoadedMailImage> LoadedMailImageAsync(WebView2 browser, string stage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            string result = await browser.CoreWebView2.ExecuteScriptAsync("Array.from(document.images).filter(image=>image.src.startsWith('https://filesearch-preview.invalid/')).map(image=>({src:image.src,complete:image.complete,width:image.naturalWidth}))");
            using var document = JsonDocument.Parse(result);
            var approved = document.RootElement.EnumerateArray().ToArray();
            Require(approved.Length == 1, stage + " must contain exactly one approved embedded image; found " + approved.Length + ".");
            var image = approved[0];
            if (image.GetProperty("complete").GetBoolean())
            {
                int width = image.GetProperty("width").GetInt32();
                Require(width > 0, stage + " embedded image did not decode.");
                return new(image.GetProperty("src").GetString()!, width);
            }
            Require(DateTime.UtcNow < deadline, stage + " embedded image did not finish loading.");
            await Task.Delay(25);
        }
    }

    private static async Task MoveToAsync(FileDocumentPreview view, int matchNumber)
    {
        await WaitUntilAsync(() => view.CurrentPreviewMatchNumber > 0);
        for (int attempt = 0; attempt < view.PreviewMatchCount && view.CurrentPreviewMatchNumber != matchNumber; attempt++)
        {
            int previous = view.CurrentPreviewMatchNumber;
            await view.MovePreviewMatchAsync(1);
            await WaitUntilAsync(() => view.CurrentPreviewMatchNumber != previous);
        }
        Require(view.CurrentPreviewMatchNumber == matchNumber, "Preview navigation could not reach occurrence " + matchNumber);
    }

    private static void RequirePartialHtmlStatus(FileDocumentPreview view, int matchNumber)
    {
        Require(view.PreviewMatchCount == 2 && view.PreviewFindState == "Partial" &&
            view.PreviewFindStatusText.Contains($"Match {matchNumber} of 2", StringComparison.Ordinal) &&
            view.PreviewFindStatusText.Contains("Partial: only loaded text is searched", StringComparison.Ordinal),
            "Truncated formatted preview navigation lost its current occurrence or loaded-text coverage message: " + view.PreviewFindStatusText);
    }

    private static async Task CheckHtmlSecurityAsync(FileDocumentPreview view, List<object> checks)
    {
        var browser = Descendants(view).OfType<WebView2>().Single();
        var core = browser.CoreWebView2;
        string readOnly = await core.ExecuteScriptAsync("document.querySelectorAll('script,[contenteditable],img[src^=\"http\"]:not([src^=\"https://filesearch-preview.invalid/\"])').length===0 && window.previewUnsafe===undefined");
        Require(readOnly == "true", "Sanitized HTML retained scripts, editable content or a remote image.");
        string before = core.Source;
        bool blocked = false;
        string unapprovedImage = new Uri(new Uri(core.Source), "unapproved-preview-image.png").AbsoluteUri;
        int? unapprovedResourceStatus = null;
        void Starting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs args)
        { if (args.Uri.StartsWith("https://example.invalid/", StringComparison.Ordinal)) blocked = args.Cancel; }
        void Response(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceResponseReceivedEventArgs args)
        { if (args.Request.Uri == unapprovedImage) unapprovedResourceStatus = args.Response.StatusCode; }
        core.NavigationStarting += Starting;
        core.WebResourceResponseReceived += Response;
        try
        {
            core.Navigate("https://example.invalid/blocked-preview-navigation");
            await WaitUntilAsync(() => blocked); await Settle();
            Require(core.Source == before && !core.Settings.IsScriptEnabled && !core.Settings.AreHostObjectsAllowed,
                "Find allowed external navigation or changed preview security permissions.");
            await core.ExecuteScriptAsync("var blockedImage=document.createElement('img');blockedImage.id='blocked-preview-image';blockedImage.src=" + JsonSerializer.Serialize(unapprovedImage) + ";document.body.appendChild(blockedImage);");
            await WaitUntilAsync(() => unapprovedResourceStatus.HasValue);
            Require(unapprovedResourceStatus == 403, "Preview allowed an unapproved resource request.");
            await core.ExecuteScriptAsync("document.getElementById('blocked-preview-image').remove();");
            // The host-only script simulates native editable focus; no document script permission is enabled.
            await core.ExecuteScriptAsync("document.body.setAttribute('contenteditable','true');document.body.focus();");
            Require(Route(browser, Key.F, ModifierKeys.Control, out bool handled) && handled, "Editable browser focus escaped the preview shortcut scope.");
            await core.ExecuteScriptAsync("document.body.removeAttribute('contenteditable');");
        }
        finally { core.NavigationStarting -= Starting; core.WebResourceResponseReceived -= Response; }
        checks.Add(new { check = "html-security-and-editable-focus", scriptsDisabled = !core.Settings.IsScriptEnabled,
            externalNavigationBlocked = blocked, unapprovedResourceStatus, sourceBytesUnchanged = true,
            note = "Scripted host-side focus/routing check; not a physical native WebView2 accelerator test." });
    }

    private static async Task FindAsync(FileDocumentPreview view, string query, bool matchCase = false)
    {
        Query(view).Text = query; Case(view).IsChecked = matchCase;
        await view.FindInPreviewAsync();
        await WaitUntilAsync(() => view.PreviewFindState != "Searching");
        Require(view.PreviewFindState is "Ready" or "NoMatches" or "Partial", "Find failed: " + view.PreviewFindStatusText);
        await Settle();
    }

    private static async Task WaitPreviewAsync(FileDocumentPreview view, FileResultViewModel file, string kind)
    {
        await WaitUntilAsync(() => view.Result?.ResultKey == file.ResultKey && view.PreviewState == kind);
        await Settle();
    }

    private static bool Route(object source, Key key, ModifierKeys modifiers, out bool handled)
    {
        var method = typeof(FileDocumentPreview).GetMethod("TryRoutePreviewFindKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = [source, key, modifiers, false];
        bool reserved = (bool)method.Invoke(null, args)!; handled = (bool)args[3]!;
        return reserved;
    }

    private static TextEditor Editor(FileDocumentPreview view) => Descendants(view).OfType<TextEditor>().Single();
    private static TextBox Query(FileDocumentPreview view) => (TextBox)view.FindName("PreviewFindQuery");
    private static CheckBox Case(FileDocumentPreview view) => (CheckBox)view.FindName("PreviewFindCase");
    private static T Offscreen<T>(T window) where T : Window
    { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string ImagePath(string output, string suffix) => Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + suffix + ".png");
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Save(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static async Task SaveBrowserAsync(FileDocumentPreview view, string path)
    {
        // WPF RenderTargetBitmap captures the toolbar but cannot capture WebView2's native window.
        await Settle();
        var browser = Descendants(view).OfType<WebView2>().Single();
        using var file = File.Create(path);
        await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, file);
        Require(file.Length > 8, "Native browser snapshot was empty: " + path);
    }
}
