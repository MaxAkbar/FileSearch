using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;
using Microsoft.Data.Sqlite;

internal static class SearchGuidanceSmoke
{
    public static async Task RunAsync(string output)
    {
        string work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "guidance-fixtures", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(work, "documents");
        Directory.CreateDirectory(root);
        string knownFile = Path.Combine(root, "renewal-agreement.txt");
        string databasePath = Path.Combine(root, "records.sqlite");
        await File.WriteAllTextAsync(knownFile, "ordinary agreement contents");
        await CreateDatabaseAsync(databasePath);
        string[] sources = [knownFile, databasePath];
        var hashes = sources.ToDictionary(path => path, Hash);

        var settings = new FakeSettingsService();
        settings.Current.UseIndex = false;
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher();
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain, new DatabaseExtractor()], plain);
        using var fileIndex = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(work, "index", "probe.db") },
            new FileWalker(), registry);
        var live = new Searcher(new FileWalker(), registry);
        var indexed = new IndexedSearcher(live, fileIndex, new IndexCoverageService(fileIndex));
        using var search = new SearchViewModel(indexed, registry, new QueryFactory(), new FilePreviewService(registry), launcher,
            settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(fileIndex, new FakeIndexingService(), settings, appSettings, launcher,
            new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(work, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(),
            new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var window = new MainWindow
        {
            DataContext = main, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        var checks = new List<object>();
        try
        {
            window.Show();
            await Settle();
            search.SearchPath = root;
            search.QueryText = "john smith";
            search.SearchMode = QueryMode.Unified;
            await Settle();
            Require(search.QueryMeaningText.Contains("AND", StringComparison.Ordinal), "Implicit AND was not explained.");
            CheckBindings(window, search);
            string andMeaning = search.QueryMeaningText;
            search.QueryText = "john OR smith";
            await Settle();
            Require(search.QueryMeaningText.Contains("OR", StringComparison.Ordinal) && search.QueryMeaningText != andMeaning,
                "Changing to OR did not update the interpretation.");
            string orMeaning = search.QueryMeaningText;
            search.QueryText = "\"john smith\"";
            await Settle();
            Require(search.QueryMeaningText.Contains("phrase", StringComparison.OrdinalIgnoreCase), "Quoted phrase was not explained.");
            string phraseMeaning = search.QueryMeaningText;
            search.QueryText = "john OR smith";
            search.SearchMode = QueryMode.PlainText;
            await Settle();
            Require(search.QueryText == "john OR smith" && search.QueryMeaningText.Contains("literal", StringComparison.OrdinalIgnoreCase),
                "Plain text mode rewrote or misrepresented the query.");
            checks.Add(new { check = "live-mode-explanations", andMeaning, orMeaning, phraseMeaning, literalMeaning = search.QueryMeaningText });

            search.IsSearchOptionsVisible = true;
            await Settle();
            SelectPreset(window, "Find a file");
            Require(search.QueryText == "john OR smith" && search.CurrentSearchTargets.SequenceEqual([SearchTarget.FileNames]),
                "Filename preset changed the query or selected the wrong target.");
            search.QueryText = "renewal-agreement";
            await SearchAsync(search);
            Require(search.Files.Any(file => file.FullPath == knownFile), "Fresh-profile filename search did not find the generated file.");
            checks.Add(new { check = "fresh-filename-search", files = search.Files.Count, route = search.SearchCoverageText });

            search.QueryText = "john smith";
            SelectPreset(window, "Database values");
            Require(search.QueryText == "john smith" && search.CurrentSearchTargets.SequenceEqual([SearchTarget.Content]) &&
                    search.EnableDocumentExtraction && !search.EnableImageOcr && search.SearchMode == QueryMode.PlainText,
                "Database preset did not establish a literal per-cell search without optional OCR.");
            await SearchAsync(search);
            AssertDatabaseHit(search, databasePath, HitRoute.Live);
            Require(search.SearchScopeText.Contains("cell", StringComparison.OrdinalIgnoreCase), "Database cell scope is not visible.");
            checks.Add(new { check = "fresh-database-value-search", route = search.SearchCoverageText, scope = search.SearchScopeText });
            Save(window, ImagePath(output, "options"));

            search.UseIndex = true;
            await SearchAsync(search);
            AssertDatabaseHit(search, databasePath, HitRoute.Live);
            Require(search.SearchCoverageText.Contains("does not cover", StringComparison.OrdinalIgnoreCase) &&
                    search.SearchCoverageText.Contains("live", StringComparison.OrdinalIgnoreCase),
                "Missing-index fallback reason was not retained after search completion.");
            checks.Add(new { check = "missing-index-fallback", route = search.SearchCoverageText });

            var walkerOptions = GetWalkerOptions(search) with
            {
                IncludeGlobs = [], ExcludeGlobs = [], MinFileSizeBytes = 0, MaxFileSizeBytes = 0,
                ModifiedAfterUtc = null, ModifiedBeforeUtc = null,
            };
            await fileIndex.BuildOrRefreshAsync(new IndexRequest(root, walkerOptions), CancellationToken.None);
            var location = new IndexedLocationSettings { Root = root, Recursive = true, EnableDocumentExtraction = true };
            settings.Current.IndexedLocations.Add(location);
            index.IndexedLocations.Add(location);
            search.SearchPath = root + Path.DirectorySeparatorChar;
            search.SearchPath = root;
            await SearchAsync(search);
            AssertDatabaseHit(search, databasePath, HitRoute.Indexed);
            Require(search.SearchCoverageText.Contains("index", StringComparison.OrdinalIgnoreCase), "Covered search route is not visible.");
            checks.Add(new { check = "covered-database-value-search", route = search.SearchCoverageText });

            search.SearchMode = QueryMode.Unified;
            search.QueryText = "alpha beta";
            await SearchAsync(search);
            Require(search.TotalHits == 0, "AND incorrectly combined separate database columns in one record.");
            checks.Add(new { check = "database-and-stays-within-cell", hits = search.TotalHits });
            search.QueryText = "john smith";
            search.SearchMode = QueryMode.PlainText;
            await SearchAsync(search);
            AssertDatabaseHit(search, databasePath, HitRoute.Indexed);
            search.IsSearchOptionsVisible = false;
            await Settle();
            CheckBindings(window, search);
            checks.Add(CheckLayout(window, "wide"));
            Save(window, ImagePath(output, "wide"));
            window.Width = window.MinWidth;
            window.Height = 760;
            await Settle();
            search.IsSearchOptionsVisible = true;
            await Settle();
            string minimumQuery = search.QueryText;
            SelectPreset(window, "Find a file");
            Require(search.QueryText == minimumQuery && search.CurrentSearchTargets.SequenceEqual([SearchTarget.FileNames]),
                "The minimum-width filename choice rewrote the query or selected the wrong target.");
            SelectPreset(window, "Find content");
            Require(search.QueryText == minimumQuery && search.CurrentSearchTargets.SequenceEqual([SearchTarget.Content]),
                "The minimum-width content choice rewrote the query or selected the wrong target.");
            SelectPreset(window, "Database values");
            Require(search.QueryText == minimumQuery && search.CurrentSearchTargets.SequenceEqual([SearchTarget.Content]) &&
                    search.EnableDocumentExtraction && search.SearchMode == QueryMode.PlainText,
                "The minimum-width database choice did not restore the expected search.");
            checks.Add(new { check = "minimum-width-starting-choices", allThreeAccessible = true, queryPreserved = true });
            Save(window, ImagePath(output, "minimum-options"));
            search.IsSearchOptionsVisible = false;
            await SearchAsync(search);
            AssertDatabaseHit(search, databasePath, HitRoute.Indexed);
            CheckBindings(window, search);
            checks.Add(CheckLayout(window, "minimum"));
            Save(window, ImagePath(output, "minimum"));

            SelectFindContentWithoutRewriting(search);
            bool unchanged = sources.All(path => Hash(path) == hashes[path]);
            Require(unchanged, "A generated source file changed during read-only searching.");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                fixtureRoot = root, sourcesUnchanged = unchanged, optionalOcrEnabled = search.EnableImageOcr,
                installedModelsRequired = false, powerToysRequired = false,
                scope = "Real main-window controls and generated files with fake settings/shell services; not physical input or OS-cold timing.",
                checks,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static void SelectFindContentWithoutRewriting(SearchViewModel search)
    {
        string query = search.QueryText;
        search.FindContentCommand.Execute(null);
        Require(search.QueryText == query && search.CurrentSearchTargets.SequenceEqual([SearchTarget.Content]),
            "Content preset rewrote the query or selected the wrong target.");
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Records (Id INTEGER PRIMARY KEY, Notes TEXT, Owner TEXT); " +
            "INSERT INTO Records VALUES (1, 'alpha', 'beta'); INSERT INTO Records VALUES (2, 'john smith renewal-9342', 'ordinary owner');";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SearchAsync(SearchViewModel search)
    {
        await search.SearchCommand.ExecuteAsync(null);
        await Settle();
        Require(!search.IsSearching, "Search did not complete.");
    }

    private static WalkerOptions GetWalkerOptions(SearchViewModel search) =>
        (WalkerOptions)typeof(SearchViewModel).GetMethod("BuildWalkerOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(search, [SearchTarget.Content])!;

    private static void AssertDatabaseHit(SearchViewModel search, string path, HitRoute route)
    {
        var hits = search.Files.SelectMany(file => file.Hits).Where(hit => hit.Database is not null).ToArray();
        Require(hits.Length == 1 && hits[0].Path == path && hits[0].Database!.Table == "Records" &&
                hits[0].Database!.RowNumber == 2 && hits[0].Database!.Column == "Notes" && hits[0].Route == route,
            "Database search lost the expected file, table, row, column or route.");
    }

    private static void SelectPreset(Window window, string label)
    {
        var button = Descendants(window).OfType<Button>().Single(candidate => Equals(candidate.Content, label));
        Require(button.IsVisible && button.Command?.CanExecute(button.CommandParameter) == true, "Starting choice is unavailable: " + label);
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), "Starting choice lacks an accessible name: " + label);
        CheckWindowBounds(window, button, "Starting choice " + label);
        button.Command!.Execute(button.CommandParameter);
    }

    private static void CheckBindings(MainWindow window, SearchViewModel search)
    {
        foreach (var (name, expected) in new[]
                 { ("SearchMeaningText", search.QueryMeaningText), ("SearchScopeText", search.SearchScopeText), ("SearchCoverageText", search.SearchCoverageText) })
        {
            var block = (TextBlock)window.FindName(name);
            Require(block is not null && block.Text == expected && BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.HasError == false,
                "Guidance binding failed: " + name);
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(block!)), "Guidance lacks an accessible name: " + name);
        }
    }

    private static object CheckLayout(MainWindow window, string layout)
    {
        var panel = (FrameworkElement)window.FindName("SearchGuidancePanel");
        var results = (FrameworkElement)window.FindName("ResultsArea");
        var bounds = panel.TransformToAncestor(window).TransformBounds(new Rect(panel.RenderSize));
        var resultBounds = results.TransformToAncestor(window).TransformBounds(new Rect(results.RenderSize));
        Require(panel.IsVisible && panel.ActualHeight > 24 && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 &&
                bounds.Bottom <= resultBounds.Top + 1 && results.ActualHeight > 120,
            "Guidance is clipped or overlaps results in the " + layout + " layout.");
        foreach (string name in new[] { "SearchMeaningText", "SearchScopeText", "SearchCoverageText" })
        {
            var block = (FrameworkElement)window.FindName(name);
            var textBounds = block.TransformToAncestor(panel).TransformBounds(new Rect(block.RenderSize));
            Require(block.IsVisible && block.ActualHeight > 8 && textBounds.Bottom <= panel.ActualHeight + 1,
                "Guidance text is clipped: " + name);
        }
        var headerControls = new List<object>();
        foreach (string name in new[] { "QueryBox", "FileTypesButton", "SearchStartButton", "SearchOptionsButton" })
        {
            var control = (FrameworkElement)window.FindName(name);
            Require(control is not null && control.IsVisible && control.ActualWidth > 8 && control.ActualHeight > 8,
                "Search header control is unavailable in the " + layout + " layout: " + name);
            var controlBounds = CheckWindowBounds(window, control!, name + " in " + layout);
            headerControls.Add(new { name, left = controlBounds.Left, top = controlBounds.Top, width = controlBounds.Width, height = controlBounds.Height });
        }
        return new { check = "layout", layout, width = window.ActualWidth, height = window.ActualHeight,
            panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight, resultsHeight = results.ActualHeight, headerControls };
    }

    private static Rect CheckWindowBounds(Window window, FrameworkElement control, string description)
    {
        var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
            "Control extends outside the window: " + description);
        return bounds;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string ImagePath(string output, string suffix) => Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + suffix + ".png");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Settle() { await Task.Delay(120); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
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
}
