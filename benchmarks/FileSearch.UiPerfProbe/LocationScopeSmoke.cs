using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;
using Microsoft.Data.Sqlite;

internal static class LocationScopeSmoke
{
    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        string work = Path.Combine(Path.GetDirectoryName(output)!, "location-scope-fixtures", Guid.NewGuid().ToString("N"));
        string first = Path.Combine(work, "A"), nested = Path.Combine(first, "Nested"), second = Path.Combine(work, "B");
        string unavailable = Path.Combine(work, "Offline"), removed = Path.Combine(work, "Removed");
        foreach (string directory in new[] { nested, second, removed }) Directory.CreateDirectory(directory);
        string alpha = Path.Combine(first, "alpha annual needle.txt"), child = Path.Combine(nested, "nested needle.txt");
        string beta = Path.Combine(second, "beta needle.txt"), removedFile = Path.Combine(removed, "removed needle.txt");
        File.WriteAllText(alpha, "First needle phrase evidence.\nLast source line.");
        File.WriteAllText(child, "Nested needle phrase evidence.");
        File.WriteAllText(beta, "Separated needle\nphrase evidence.");
        File.WriteAllText(removedFile, "Removed registration needle phrase must not be searched.");
        string database = Path.Combine(first, "records.sqlite"), archive = Path.Combine(second, "members.zip");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Records (Id INTEGER PRIMARY KEY, Description TEXT); INSERT INTO Records VALUES (1, 'needle phrase database evidence');";
            command.ExecuteNonQuery();
        }
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("inner/evidence.txt").Open())) writer.Write("needle phrase archive evidence");
        string[] sources = [alpha, child, beta, removedFile, database, archive];
        var hashes = sources.ToDictionary(path => path, Hash);
        var settings = new FakeSettingsService();
        settings.Current.UseIndex = false;
        settings.Current.IndexedLocations = new[] { first, nested, second, unavailable }
            .Select(root => new IndexedLocationSettings { Root = root, Recursive = true }).ToList();
        settings.Current.QuickSearchSelectedIndexedRoots = [first];
        settings.Current.QuickSearchDefaultScope = QuickSearchScopeKind.SelectedIndexedLocations;
        settings.Current.QuickSearchLastScope = QuickSearchScopeKind.SelectedIndexedLocations;
        settings.Current.QuickSearchIncludeContent = false;
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher();
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain, new DatabaseExtractor(), new ZipExtractor()], plain);
        var preview = new FilePreviewService(registry);
        var searcher = new RecordingSearcher(new Searcher(new FileWalker(), registry));
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), preview, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher,
            new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(work, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(),
            new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        search.SearchPath = first;
        var window = Offscreen(new MainWindow { DataContext = main, Width = 1440, Height = 900 });
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), preview, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = Offscreen(new QuickSearchWindow { DataContext = quick, Width = 640, Height = 540 });
        int activationRequests = 0;
        quick.RequestOpenMainWindow += (_, _) => activationRequests++;
        var checks = new List<object>();
        try
        {
            window.Show(); quickWindow.Show(); await Settle();
            var scopeBox = (ComboBox)window.FindName("MainLocationScope");
            Require(AutomationProperties.GetName(scopeBox) == "Search location scope", "Main scope selector has no accessible name.");
            scopeBox.SelectedValue = LocationScopeKind.SelectedIndexedLocations;
            await Settle();
            Require(search.SelectedLocationScope == LocationScopeKind.SelectedIndexedLocations, "Main scope selector did not update the view model.");
            double mainContrast = VerifyScopePalette(scopeBox);
            var quickScopeBox = (ComboBox)quickWindow.FindName("QuickLocationScope");
            double quickContrast = VerifyScopePalette(quickScopeBox);
            Require(((FrameworkElement)window.FindName("MainLocationSelectionButton")).ActualWidth >= 80 &&
                ((FrameworkElement)quickWindow.FindName("QuickLocationSelectionButton")).ActualWidth >= 80,
                "Location chooser captions are clipped by a fixed-width icon-button style.");
            var contentToggle = (CheckBox)quickWindow.FindName("QuickContentToggle");
            Require(contentToggle.Foreground is SolidColorBrush contentInk && quickWindow.Background is SolidColorBrush quickPaper &&
                Contrast(contentInk.Color, quickPaper.Color) >= 4.5, "Quick Content label has insufficient contrast against the window.");
            await SetChoice(window, "MainLocationSelectionButton", "MainLocationChoices", second, true);
            Require(search.SelectedIndexedRoots.Contains(second), "Main checkbox did not add its selected root.");
            Require(quick.LocationSelections.Any(row => row.Root == second && row.IsSelected), "Quick Search did not share the main selection.");
            checks.Add(new { check = "main-scope-selector-and-shared-checkbox", selected = search.SelectedIndexedRoots.ToArray(),
                mainScopeContrast = mainContrast, quickScopeContrast = quickContrast,
                mainChooserWidth = ((FrameworkElement)window.FindName("MainLocationSelectionButton")).ActualWidth,
                quickChooserWidth = ((FrameworkElement)quickWindow.FindName("QuickLocationSelectionButton")).ActualWidth });

            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.SelectedIndexedLocations);
            await SetChoice(quickWindow, "QuickLocationSelectionButton", "QuickLocationChoices", second, false);
            Require(!search.SelectedIndexedRoots.Contains(second), "Quick checkbox did not update the shared main selection.");
            checks.Add(new { check = "quick-checkbox-updates-main", selected = search.SelectedIndexedRoots.ToArray() });

            search.QueryText = "needle"; search.UseIndex = false; search.FindFileCommand.Execute(null);
            await search.SearchCommand.ExecuteAsync(null); await Settle();
            Require(Keys(search.Files).SequenceEqual(KeysFor(alpha, child)), "Selected indexed search escaped its selected root or missed nested content.");
            checks.Add(new { check = "main-selected-root-search", files = Keys(search.Files), route = search.SearchCoverageText });

            search.SelectedIndexedRoots.Clear();
            int requestsBeforeEmpty = searcher.Requests.Count;
            await search.SearchCommand.ExecuteAsync(null); await Settle();
            Require(searcher.Requests.Count == requestsBeforeEmpty && !search.LocationSelections.Any(row => row.IsSelected),
                "An empty selected-indexed scope ran a broader search.");
            Require(!string.IsNullOrWhiteSpace(search.LocationScopeNotice), "Empty indexed selection has no coverage explanation.");
            quick.SearchText = "needle"; await WaitQuick(quick);
            Require(quick.Results.Count == 0 && !string.IsNullOrWhiteSpace(quick.LocationScopeNotice), "Quick empty selection broadened its scope.");
            checks.Add(new { check = "empty-selection-never-means-all", mainNotice = search.LocationScopeNotice, quickNotice = quick.LocationScopeNotice });

            search.SelectedIndexedRoots.Add(removed);
            search.RefreshLocationSelections();
            Require(search.LocationSelections.Any(row => row.Root == removed && row.IsSelected && !string.IsNullOrWhiteSpace(row.StatusText)),
                "A removed selected root disappeared without a status row.");
            int requestsBeforeRemoved = searcher.Requests.Count;
            await search.SearchCommand.ExecuteAsync(null); await Settle();
            Require(searcher.Requests.Count == requestsBeforeRemoved, "Removed registration was searched or replaced with all roots.");
            checks.Add(new { check = "removed-root-stays-explicit", notice = search.LocationScopeNotice });

            quick.SearchText = ""; await Settle();
            search.SelectedIndexedRoots.Clear(); search.SelectedIndexedRoots.Add(first); search.SelectedIndexedRoots.Add(unavailable);
            search.SelectedLocationScope = LocationScopeKind.SelectedIndexedLocations;
            await search.SearchCommand.ExecuteAsync(null); await Settle();
            Require(Keys(search.Files).SequenceEqual(KeysFor(alpha, child)), "Unavailable roots changed the remaining selected-root coverage.");
            Require(!string.IsNullOrWhiteSpace(search.LocationScopeNotice), "Unavailable selected root was not explained.");
            checks.Add(new { check = "offline-root-does-not-broaden", files = Keys(search.Files), notice = search.LocationScopeNotice });

            scopeBox.SelectedValue = LocationScopeKind.AllIndexedLocations;
            await search.SearchCommand.ExecuteAsync(null); await Settle();
            Require(Keys(search.Files).SequenceEqual(KeysFor(alpha, child, beta)), "All indexed roots omitted or added an unexpected physical file.");
            Require(search.Files.All(file => file.Hits.Count == 1), "Overlapping indexed roots duplicated filename hits.");
            checks.Add(new { check = "all-roots-overlap-dedup", files = Keys(search.Files), hits = search.Files.Sum(file => file.HitCount) });
            Save(window, ImagePath(output, "main-all-roots"));

            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.SelectedIndexedLocations);
            search.SelectedIndexedRoots.Clear(); search.SelectedIndexedRoots.Add(first);
            quick.IncludeContentMatches = false; quick.SearchText = "alpha needle"; await WaitQuick(quick);
            Require(quick.Results.Any(file => file.FullPath == alpha), "Quick filename word matching did not find nonadjacent literal terms.");
            quick.SelectedResult = quick.Results.Single(file => file.FullPath == alpha);
            await quick.OpenFullSearchCommand.ExecuteAsync(null); await Settle();
            Require(activationRequests == 1 && search.UseQuickSearchMatching && search.QueryText == "alpha needle" &&
                search.SelectedLocationScope == LocationScopeKind.SelectedIndexedLocations && search.SelectedIndexedRoots.SequenceEqual([first]) &&
                search.SelectedFile?.FullPath == alpha, "Quick handoff lost matching semantics, location scope or selected result.");
            Require(((Button)window.FindName("UseStandardSearchMatchingButton")).IsVisible, "Quick matching has no visible way back to normal search.");
            checks.Add(new { check = "quick-name-handoff-preserves-and-profile", activationRequests, query = search.QueryText,
                selected = search.SelectedFile?.ResultKey, meaning = search.QueryMeaningText });
            search.SaveCurrentSearchCommand.Execute(null); search.SaveWorkspaceCommand.Execute(null);
            Require(history.SavedSearches[0].LocationScope == LocationScopeKind.SelectedIndexedLocations &&
                history.SavedSearches[0].SelectedIndexedRoots.SequenceEqual([first]) && history.SavedSearches[0].UseQuickSearchMatching &&
                history.Workspaces[0].Search.LocationScope == LocationScopeKind.SelectedIndexedLocations &&
                history.Workspaces[0].Search.SelectedIndexedRoots.SequenceEqual([first]) && history.Workspaces[0].Search.UseQuickSearchMatching,
                "Saving a handed-off search or workspace discarded scope/profile.");
            checks.Add(new { check = "saved-search-workspace-retain-scope-profile", saved = history.SavedSearches[0].LocationSummary,
                workspace = history.Workspaces[0].Search.LocationSummary });

            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.AllIndexedLocations);
            quick.IncludeContentMatches = true; quick.SearchText = "needle phrase"; await WaitQuick(quick);
            Require(quick.Results.Any(file => file.FullPath == database) && quick.Results.Any(file => file.FullPath == archive),
                "Quick content scope did not include generated database/archive matches.");
            string[] quickContentKeys = Keys(quick.Results);
            var databaseResult = quick.Results.Single(file => file.FullPath == database);
            var databaseLocation = databaseResult.Hits.First(hit => hit.Database is not null).Database!;
            quick.SelectedResult = databaseResult;
            await quick.OpenFullSearchCommand.ExecuteAsync(null); await Settle();
            Require(search.UseQuickSearchMatching && search.SelectedLocationScope == LocationScopeKind.AllIndexedLocations &&
                Keys(search.Files).SequenceEqual(quickContentKeys) && search.SelectedFile?.FullPath == database &&
                search.CurrentPreviewHit?.Database?.Table == databaseLocation.Table && search.CurrentPreviewHit?.Database?.Column == databaseLocation.Column,
                "Content handoff changed phrase results or database record identity.");
            Require(!search.Files.Any(file => file.FullPath == beta || file.FullPath == removedFile), "Content handoff split the phrase or escaped registered roots.");
            checks.Add(new { check = "quick-content-handoff-and-database-location", files = Keys(search.Files), table = databaseLocation.Table,
                column = databaseLocation.Column, row = databaseLocation.RowNumber });

            quick.SearchText = ""; await Settle(); quick.SearchText = "needle phrase"; await WaitQuick(quick);
            quick.SelectedResult = quick.Results.Single(file => file.FullPath == archive);
            await quick.OpenFullSearchCommand.ExecuteAsync(null); await Settle();
            var selectedHit = search.CurrentPreviewHit;
            string? member = selectedHit?.Snippet?.Locator?.ArchiveMember ?? selectedHit?.Locator?.ArchiveMember ?? selectedHit?.Anchor?.MemberPath;
            Require(search.SelectedFile?.FullPath == archive && member == "inner/evidence.txt", "Archive member identity was lost by handoff.");
            checks.Add(new { check = "quick-archive-member-handoff", member, selected = search.SelectedFile?.ResultKey });
            var standardButton = (Button)window.FindName("UseStandardSearchMatchingButton");
            Require(standardButton.Command is not null && standardButton.Command.CanExecute(standardButton.CommandParameter),
                "Use standard matching has no enabled command binding.");
            var standardPeer = new ButtonAutomationPeer(standardButton);
            if (standardPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider standardInvoke)
                throw new InvalidOperationException("Use standard matching has no automation Invoke pattern.");
            standardInvoke.Invoke();
            await WaitUntil(() => !search.UseQuickSearchMatching); await Settle();
            checks.Add(new { check = "standard-matching-action", meaning = search.QueryMeaningText });

            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.EntireMachineMetadata);
            Require(!quick.OpenFullSearchCommand.CanExecute(null), "Entire machine metadata was silently mapped into a narrower full-search scope.");
            checks.Add(new { check = "unsupported-machine-handoff-explicit", explanation = quick.FullSearchHandoffSummary });

            if (!quickWindow.IsVisible) quickWindow.Show();
            await Settle();
            Require(quickWindow.IsVisible, "Quick Search did not reopen for the minimum-layout preview check.");
            quick.SearchText = ""; await Settle(); quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.SelectedIndexedLocations);
            search.SelectedIndexedRoots.Clear(); search.SelectedIndexedRoots.Add(first);
            quick.IncludeContentMatches = false; quick.SearchText = "alpha needle"; await WaitQuick(quick);
            quick.SelectedResult = quick.Results.Single(file => file.FullPath == alpha);
            await quick.PreviewResultCommand.ExecuteAsync(null); await Settle();
            var documentView = Descendants(quickWindow).OfType<FileDocumentPreview>().Single();
            await WaitUntil(() => documentView.PreviewState == "Text" && documentView.CanFindInPreview);
            documentView.ShowPreviewFind(); await Settle();
            var previewBorder = (FrameworkElement)quickWindow.FindName("InlinePreviewBorder");
            var scopeButton = (FrameworkElement)quickWindow.FindName("QuickLocationSelectionButton");
            var handoffButton = (FrameworkElement)quickWindow.FindName("OpenFullSearchButton");
            Require(InWindow(scopeButton, quickWindow) && InWindow(handoffButton, quickWindow) && InWindow(previewBorder, quickWindow),
                "Quick selected-location controls or preview were clipped at 640 by 540.");
            Save(quickWindow, ImagePath(output, "quick-selected-preview-minimum"));
            checks.Add(new { check = "quick-minimum-selected-scope-preview-layout", quickWindow.ActualWidth, quickWindow.ActualHeight,
                previewHeight = previewBorder.ActualHeight });
            documentView.ClosePreviewFind();
            search.SelectedLocationScope = LocationScopeKind.SelectedIndexedLocations;
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Settle();
            Require(InWindow(scopeBox, window) && InWindow((FrameworkElement)window.FindName("MainLocationScopeNotice"), window) &&
                InWindow((FrameworkElement)window.FindName("MainLocationSelectionButton"), window) &&
                InWindow((FrameworkElement)window.FindName("QueryBox"), window) && InWindow((FrameworkElement)window.FindName("SearchStartButton"), window),
                "Main query, search or location scope controls were not reachable at minimum dimensions.");
            Save(window, ImagePath(output, "main-minimum"));
            checks.Add(new { check = "main-minimum-layout", window.ActualWidth, window.ActualHeight });

            var settingsView = new ApplicationSettingsViewModel(settings, status);
            foreach (var row in settingsView.QuickIndexedLocationSelections) row.IsSelected = false;
            settingsView.RefreshQuickIndexedLocationsCommand.Execute(null);
            Require(settingsView.QuickIndexedLocationSelections.All(row => !row.IsSelected) && settings.Current.QuickSearchSelectedIndexedRoots.Count == 0,
                "Refreshing settings reselected every indexed root after clear-all.");
            checks.Add(new { check = "settings-empty-selection-refresh", selectedRoots = settings.Current.QuickSearchSelectedIndexedRoots.ToArray() });

            bool unchanged = sources.All(path => Hash(path) == hashes[path]);
            Require(unchanged, "Location scope checks changed fixture source bytes.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, fixtureRoot = work, sourcesUnchanged = unchanged, checks,
                scope = "Scripted real main/Quick UI and live searches with generated text/database/archive fixtures and fake settings/shell services. Not physical-input, screen-reader, performance or installed-product qualification." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(output + ".failure.json", JsonSerializer.Serialize(new { error = error.ToString(), checks, status = status.Text,
                mainScope = search.SelectedLocationScope.ToString(), mainNotice = search.LocationScopeNotice, mainRoots = search.SelectedIndexedRoots.ToArray(),
                mainFiles = Keys(search.Files), quickScope = quick.SelectedScope?.DisplayName, quick.StatusText, quickNotice = quick.LocationScopeNotice,
                quickFiles = Keys(quick.Results) }, new JsonSerializerOptions { WriteIndented = true }));
            Save(window, ImagePath(output, "failure-main")); Save(quickWindow, ImagePath(output, "failure-quick"));
            throw;
        }
        finally { quickWindow.Close(); window.Close(); }
    }

    private static async Task SetChoice(Window window, string toggleName, string itemsName, string root, bool value)
    {
        var toggle = (ToggleButton)window.FindName(toggleName); toggle.IsChecked = true; await Settle();
        try
        {
            var items = (ItemsControl)window.FindName(itemsName); items.UpdateLayout();
            var check = Descendants(items).OfType<CheckBox>().Single(candidate =>
                (string?)candidate.DataContext.GetType().GetProperty("Root")!.GetValue(candidate.DataContext) == root);
            Require(AutomationProperties.GetName(check) == root, "Location checkbox lacks its accessible root name.");
            check.IsChecked = value; check.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource(); await Settle();
        }
        finally { toggle.IsChecked = false; }
    }

    private static async Task WaitQuick(QuickSearchViewModel quick)
    {
        await Task.Delay(100); await WaitUntil(() => !quick.IsSearching); await Settle();
    }
    private static double VerifyScopePalette(ComboBox scope)
    {
        if (scope.Foreground is not SolidColorBrush foreground || scope.Background is not SolidColorBrush background)
            throw new InvalidOperationException("Location scope selector has no explicit solid palette.");
        double contrast = Contrast(foreground.Color, background.Color);
        Require(foreground.Opacity * foreground.Color.A / 255 >= .9 && contrast >= 4.5,
            "Location scope selector text has insufficient foreground/background contrast.");
        foreach (var label in Descendants(scope).OfType<TextBlock>().Where(label => label.IsVisible &&
            !string.IsNullOrWhiteSpace(label.Text) && label.Text == scope.Text))
            Require(label.Foreground is SolidColorBrush labelInk && Contrast(labelInk.Color, background.Color) >= 4.5,
                "Rendered scope selector label does not inherit its visible foreground.");
        return contrast;
    }
    private static double Contrast(System.Windows.Media.Color first, System.Windows.Media.Color second)
    {
        static double Linear(byte component)
        {
            double value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(System.Windows.Media.Color color) =>
            .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static string[] Keys(IEnumerable<FileResultViewModel> files) => files.Select(file => file.ResultKey).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string[] KeysFor(params string[] paths) => paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool InWindow(FrameworkElement element, Window window)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
    }
    private static T Offscreen<T>(T window) where T : Window
    { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitUntil(Func<bool> condition)
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
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class RecordingSearcher(ISearcher inner) : ISearcher
    {
        internal ConcurrentQueue<SearchRequest> Requests { get; } = new();
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            await foreach (var hit in inner.SearchAsync(request, cancellationToken)) yield return hit;
        }
    }
}
