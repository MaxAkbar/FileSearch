using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSharpDB.Engine;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Tests;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Services;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;
using Microsoft.Data.Sqlite;

internal static class DatabaseFindSmoke
{
    private static string _output = string.Empty;
    private static int _cellFailureCount;
    private static int _waitFailureCount;
    private static readonly ConcurrentQueue<PreviewException> PreviewExceptions = new();
    [ThreadStatic] private static bool _recordingException;
    private sealed record PreviewException(DateTime Utc, int ManagedThreadId, string Type, string Message, string Stack);

    public static async Task RunAsync(string output, bool traceExpandedDatabaseLoad = false)
    {
        _output = Path.GetFullPath(output);
        _cellFailureCount = 0;
        _waitFailureCount = 0;
        using var exceptionCapture = new PreviewExceptionCapture();
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "database-find-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sources = new List<string>();
        foreach (var kind in new[] { DatabaseKind.Sqlite, DatabaseKind.CSharpDb })
        {
            string path = await DatabaseTestFiles.CreateAsync(root, kind == DatabaseKind.Sqlite ? "records.sqlite" : "records.csdb", kind, 800, CancellationToken.None);
            await PrepareMatchesAsync(path, kind);
            sources.Add(path);
        }
        var hashes = sources.ToDictionary(path => path, Hash);
        var launcher = new FakeFileLauncher();
        var settings = new FakeSettingsService();
        settings.Current.UseIndex = false;
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain, new DatabaseExtractor()], plain);
        var preview = new FilePreviewService(registry);
        var searcher = new Searcher(new FileWalker(), registry);
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), preview, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher,
            new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(), new FakeStyleService(),
            new FakeShellIntegrationService(), replacement);
        var files = sources.Select(path => new FileResultViewModel(path, launcher)).ToArray();
        foreach (var file in files) search.Files.Add(file);
        search.SearchPath = root;
        search.PreviewPaneWidth = 580;
        var window = new MainWindow
        {
            DataContext = main, Width = 1440, Height = 940, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        using var quick = new QuickSearchViewModel(searcher, new QueryFactory(), preview, launcher, settings, search, new FakeFolderPicker());
        var quickWindow = new QuickSearchWindow
        {
            DataContext = quick, Width = 900, Height = 750, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        var checks = new List<object>();
        try
        {
            window.Show();
            await Settle();
            var tabs = Descendants(window).OfType<TabControl>().Single(control => AutomationProperties.GetName(control) == "Result preview views");
            tabs.SelectedIndex = 1;
            foreach (var file in files)
            {
                search.SelectedFile = file;
                await Settle();
                var view = Descendants(tabs).OfType<FileDocumentPreview>().Single();
                var database = await WaitDatabaseAsync(view);
                Require(database.CurrentLocation?.RowNumber == 1, "Filename-only preview did not begin on page one.");
                await CheckFindAndNavigationAsync(database, file.FullPath, checks, "main");
                await CheckModesAndInvalidationAsync(database, checks, file.FileName);
                await FindAsync(database, "needle 東京", DatabaseFindMode.AllTerms);
                AssertCell(database, file.FullPath, 750, "Notes", requireHighlight: true);
                checks.Add(CheckLayout(window, database, file.FileName + " main"));
                checks.Add(await CheckOptionsAsync(window, database, file.FullPath, file.FileName + " main", file.Extension + "-main-options"));
                Save(window, ImagePath(output, file.Extension + "-main"));

                ((Button)view.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // Attach synchronously before the first settle/poll can miss the
                // database control's opening or initial page preparation.
                using var loadTrace = traceExpandedDatabaseLoad ? new ExpandedDatabaseLoadTrace(
                    (FileDocumentPreview)window.OwnedWindows.Cast<Window>().Single().Content,
                    Path.Combine(Path.GetDirectoryName(_output)!, Path.GetFileNameWithoutExtension(_output) + "-" + file.Extension + "-expanded-load-trace.json")) : null;
                try
                {
                    await Settle();
                    var expanded = window.OwnedWindows.Cast<Window>().Single();
                    var expandedDatabase = await WaitDatabaseAsync((FileDocumentPreview)expanded.Content);
                    AssertCell(expandedDatabase, file.FullPath, 750, "Notes", requireHighlight: expandedDatabase.FindMatchCount > 0);
                    Require(expandedDatabase.CurrentLocation!.RowKey == database.CurrentLocation!.RowKey,
                        "Expand lost the selected record key.");
                    checks.Add(new { check = "expanded-location", file = file.FileName, row = expandedDatabase.CurrentLocation.RowNumber,
                        column = expandedDatabase.CurrentLocation.Column, key = expandedDatabase.CurrentLocation.RowKey,
                        fingerprint = expandedDatabase.CurrentLocation.Fingerprint, restoredFindMatches = expandedDatabase.FindMatchCount });
                    Save(expanded, ImagePath(output, file.Extension + "-expanded"));
                    loadTrace?.Complete("ready");
                    expanded.Close();
                }
                catch (Exception error) { loadTrace?.Complete("failed", error); throw; }
                finally { loadTrace?.Dispose(); }

                await CheckCancellationAsync(database, checks, file.FileName);
                await CheckChangedSourceAsync(database, file.FullPath, checks);
            }

            foreach (var file in files) quick.Results.Add(file);
            quickWindow.Show();
            quick.SelectedResult = files[0];
            await quick.PreviewResultCommand.ExecuteAsync(null);
            await Settle();
            foreach (var file in files)
            {
                quick.SelectedResult = file;
                if (!quick.IsPreviewVisible) await quick.PreviewResultCommand.ExecuteAsync(null);
                await Settle();
                var view = Descendants(quickWindow).OfType<FileDocumentPreview>().Single();
                var database = await WaitDatabaseAsync(view);
                Require(database.CurrentLocation?.RowNumber == 1, "Quick filename-only preview did not begin on page one.");
                await CheckFindAndNavigationAsync(database, file.FullPath, checks, "quick");
                quickWindow.Width = 760;
                quickWindow.Height = 540;
                await Settle();
                AssertCell(database, file.FullPath, 750, "Notes", requireHighlight: true);
                checks.Add(CheckLayout(quickWindow, database, file.FileName + " Quick default size"));
                checks.Add(await CheckOptionsAsync(quickWindow, database, file.FullPath, file.FileName + " Quick default size", file.Extension + "-quick-options"));
                Save(quickWindow, ImagePath(output, file.Extension + "-quick"));
                quickWindow.Width = quickWindow.MinWidth;
                await Settle();
                AssertCell(database, file.FullPath, 750, "Notes", requireHighlight: true);
                await database.MoveFindMatchAsync(1);
                await Settle();
                AssertCell(database, file.FullPath, 800, "Notes", requireHighlight: true);
                await database.MoveFindMatchAsync(-1);
                await Settle();
                AssertCell(database, file.FullPath, 750, "Notes", requireHighlight: true);
                checks.Add(CheckLayout(quickWindow, database, file.FileName + " Quick minimum width"));
                checks.Add(await CheckOptionsAsync(quickWindow, database, file.FullPath, file.FileName + " Quick minimum width", file.Extension + "-quick-minimum-options"));
                Save(quickWindow, ImagePath(output, file.Extension + "-quick-minimum"));
                checks.Add(await CheckQuickFindSizingAsync(quickWindow, database, file.FullPath));
                Save(quickWindow, ImagePath(output, file.Extension + "-quick-hidden-preview"));
                quickWindow.Width = 900;
                quickWindow.Height = 750;
            }

            // A deliberate resize after automatic DB growth must survive closing
            // the preview; the application only owns its own temporary growth.
            quickWindow.Height = 420;
            quick.IsPreviewVisible = true;
            var resizedView = (FileDocumentPreview)quickWindow.FindName("QuickFileDocumentPreview");
            var resizedDatabase = await WaitDatabaseAsync(resizedView);
            CheckQuickPreviewGeometry(quickWindow, (FrameworkElement)quickWindow.FindName("InlinePreviewBorder"), resizedDatabase,
                "Database preview before manual resize");
            double manuallyResizedHeight = quickWindow.ActualHeight + 80;
            quickWindow.Height = manuallyResizedHeight;
            await Settle();
            quick.IsPreviewVisible = false;
            await Settle();
            Require(Math.Abs(quickWindow.ActualHeight - manuallyResizedHeight) < 1 && Math.Abs(quickWindow.MinHeight - 420) < 1,
                "Closing the database preview undid a manual window resize.");
            checks.Add(new { check = "quick-database-manual-height-preserved", quickWindow.ActualHeight, quickWindow.MinHeight, manuallyResizedHeight });

            foreach (var file in files)
                await CheckOuterPreviewLifetimeAsync(file, files.Single(other => !ReferenceEquals(other, file)), checks);

            bool unchanged = sources.All(path => Hash(path) == hashes[path]);
            Require(unchanged, "Database Find changed a generated source's bytes.");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                fixtureRoot = root, sourcesUnchanged = unchanged, optionalOcrEnabled = search.EnableImageOcr,
                installedModelsRequired = false, powerToysRequired = false,
                scope = "Scripted real shared main/Quick/expanded database previews with generated fixtures and fake settings/shell services; not physical-input or performance evidence.",
                checks,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            quickWindow.Close();
            window.Close();
        }
    }

    private static async Task PrepareMatchesAsync(string path, DatabaseKind kind)
    {
        string table = DatabaseFiles.Quote(DatabaseTestFiles.TableName);
        string sql = $"UPDATE {table} SET \"Notes\"='東京 needle O''Brien' WHERE \"Id\"=750; " +
            $"UPDATE {table} SET \"Display Name\"='alpha', \"Notes\"='beta' WHERE \"Id\"=10";
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await using var database = await Database.OpenAsync(path);
            foreach (string statement in sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                await using var result = await database.ExecuteAsync(statement);
            }
        }
    }

    private static async Task CheckOuterPreviewLifetimeAsync(FileResultViewModel file, FileResultViewModel other, List<object> checks)
    {
        var view = new FileDocumentPreview { Result = file };
        var window = new Window
        {
            Content = view, Width = 1000, Height = 760, ShowActivated = false, ShowInTaskbar = false,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var snapshots = new List<string>();
        try
        {
            window.Show();
            var database = await WaitDatabaseAsync(view);
            string originalSnapshot = SnapshotPath(database); snapshots.Add(originalSnapshot);
            var originalSession = Session(database);
            foreach (long row in new long[] { 750, 800 })
            {
                view.PreferredHit = DatabaseHit(file.FullPath, row, "Notes");
                var updated = await WaitDatabaseAsync(view);
                Require(ReferenceEquals(view.Result, file) && ReferenceEquals(updated, database),
                    "Changing only the selected hit replaced the same-file database preview control.");
                Require(ReferenceEquals(Session(updated), originalSession) && SnapshotPath(updated) == originalSnapshot,
                    "Changing only the selected hit reopened the unchanged private database snapshot.");
                AssertCell(updated, file.FullPath, row, "Notes", requireHighlight: false);
                Require(updated.CurrentLocation!.RowKey == (DatabaseFiles.Detect(file.FullPath) == DatabaseKind.Sqlite ? "rowid=" : "Id=") + row,
                    "Same-file hit navigation selected the wrong record key.");
            }
            checks.Add(new
            {
                check = "outer-preview-same-file-hit-reuse", file = file.FileName, rows = new[] { 750, 800 },
                sameResult = ReferenceEquals(view.Result, file), sameControl = true, sameSnapshot = true,
                location = database.CurrentLocation,
                scope = "Actual shared FileDocumentPreview in a separate app-resource host; main/Quick/expanded Find coverage remains above.",
            });

            view.Visibility = Visibility.Collapsed;
            await WaitUntilAsync(() => !File.Exists(originalSnapshot));
            Require(!Descendants(view).OfType<DatabasePreviewControl>().Any(), "Hiding the outer preview retained its disposed database control.");
            view.Visibility = Visibility.Visible;
            var reopened = await WaitDatabaseAsync(view);
            string reopenedSnapshot = SnapshotPath(reopened); snapshots.Add(reopenedSnapshot);
            Require(!ReferenceEquals(reopened, database) && reopenedSnapshot != originalSnapshot,
                "Showing a hidden preview reused its disposed control or deleted snapshot.");
            AssertCell(reopened, file.FullPath, 800, "Notes", requireHighlight: false);

            // Clear the selected hit before changing files; a hit fingerprint belongs to its source.
            view.PreferredHit = null;
            reopened = await WaitDatabaseAsync(view);
            AssertCell(reopened, file.FullPath, 1, "Id", requireHighlight: false);
            Require(SnapshotPath(reopened) == reopenedSnapshot, "Clearing the hit reopened an unchanged source.");
            view.Result = other;
            var switched = await WaitDatabaseAsync(view);
            string switchedSnapshot = SnapshotPath(switched); snapshots.Add(switchedSnapshot);
            await WaitUntilAsync(() => !File.Exists(reopenedSnapshot));
            Require(!ReferenceEquals(switched, reopened) && switchedSnapshot != reopenedSnapshot,
                "Switching to another file retained the former source's database control or snapshot.");
            AssertCell(switched, other.FullPath, 1, "Id", requireHighlight: false);
            checks.Add(new
            {
                check = "outer-preview-hide-and-source-cleanup", file = file.FileName, switchedTo = other.FileName,
                hiddenSnapshotDeleted = !File.Exists(originalSnapshot), replacedSnapshotDeleted = !File.Exists(reopenedSnapshot),
                freshControlAfterHide = true, freshControlAfterSourceSwitch = true, location = switched.CurrentLocation,
            });
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => snapshots.All(snapshot => !File.Exists(snapshot)));
        }
    }

    private static Hit DatabaseHit(string path, long row, string column) => new(path, (int)row, "selected database cell", [],
        Anchor: new SourceAnchor(SourceAnchorKind.Database, "Selected database record", Database:
            new DatabaseLocation(DatabaseFiles.Detect(path), DatabaseTestFiles.TableName, row, column, "", DatabaseFiles.Fingerprint(path))));

    private static DatabaseReadSession Session(DatabasePreviewControl database) =>
        (DatabaseReadSession)typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;

    private static string SnapshotPath(DatabasePreviewControl database) =>
        (string)typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Session(database))!;

    private static async Task CheckFindAndNavigationAsync(DatabasePreviewControl database, string path, List<object> checks, string host)
    {
        await FindAsync(database, "needle 東京", DatabaseFindMode.AllTerms);
        Require(database.FindMatchCount == 2 && database.CurrentFindMatchNumber == 1, "All-terms Find did not return both distant cells.");
        AssertCell(database, path, 750, "Notes", requireHighlight: true);
        await database.MoveFindMatchAsync(1);
        await Settle();
        AssertCell(database, path, 800, "Notes", requireHighlight: true);
        Require(database.CurrentFindMatchNumber == 2, "Next match did not advance the match counter.");
        await database.MoveFindMatchAsync(-1);
        await Settle();
        AssertCell(database, path, 750, "Notes", requireHighlight: true);
        checks.Add(new { check = "beyond-page-find-and-navigation", host, file = Path.GetFileName(path),
            matches = database.FindMatchCount, rows = new[] { 750, 800 }, column = database.CurrentLocation!.Column,
            key = database.CurrentLocation.RowKey, fingerprint = database.CurrentLocation.Fingerprint });
    }

    private static async Task CheckModesAndInvalidationAsync(DatabasePreviewControl database, List<object> checks, string file)
    {
        await FindAsync(database, "needle 東京", DatabaseFindMode.Phrase);
        Require(database.FindMatchCount == 1 && database.CurrentLocation?.RowNumber == 800, "Phrase Find did not preserve order.");
        await FindAsync(database, "needle nonexistent-9342", DatabaseFindMode.AnyTerm);
        Require(database.FindMatchCount == 2, "Either-term Find did not return both matching cells.");
        await FindAsync(database, "alpha beta", DatabaseFindMode.AllTerms);
        Require(database.FindMatchCount == 0 && database.FindState == "NoMatches", "All terms incorrectly combined different columns.");
        await FindAsync(database, "needle", DatabaseFindMode.AllTerms, "Display Name");
        Require(database.FindMatchCount == 0, "Selected-column Find searched a different column.");
        await FindAsync(database, "NEEDLE", DatabaseFindMode.AllTerms, "Notes", matchCase: true);
        Require(database.FindMatchCount == 0, "Case-sensitive Find ignored case.");
        await FindAsync(database, "NEEDLE", DatabaseFindMode.AllTerms, "Notes");
        Require(database.FindMatchCount == 2, "Case-insensitive Find missed matching cells.");

        var input = Control<TextBox>(database, "DatabaseFindTextBox");
        input.Text = "changed input";
        await Settle();
        Require(database.FindMatchCount == 0 && database.FindState != "Searching", "Edited Find input retained stale results.");
        var selector = Control<ComboBox>(database, "DatabaseTableSelector");
        selector.SelectedItem = selector.Items.Cast<DatabaseTable>().Single(table => table.Name == "EmptyTable");
        await WaitUntilAsync(() => database.PreviewState != "Loading");
        await FindAsync(database, "needle", DatabaseFindMode.AllTerms);
        Require(database.FindMatchCount == 0 && database.FindState == "NoMatches", "Empty table did not produce a clear no-match state.");
        selector.SelectedItem = selector.Items.Cast<DatabaseTable>().Single(table => table.Name == DatabaseTestFiles.TableName);
        await WaitUntilAsync(() => database.PreviewState != "Loading");
        Require(database.FindMatchCount == 0, "Changing tables retained old matches.");

        input.Text = "needle 東京";
        var find = database.FindInTableAsync();
        input.Text = "newer input";
        await find;
        await Settle();
        Require(database.FindMatchCount == 0 && database.FindState != "Searching", "An old search published after its input changed.");
        checks.Add(new { check = "modes-column-case-empty-and-invalidation", file, allTermsWithinCell = true,
            phraseOrder = true, selectedColumn = true, caseSensitive = true, emptyTable = true, staleFindSuppressed = true });
    }

    private static async Task CheckCancellationAsync(DatabasePreviewControl database, List<object> checks, string file)
    {
        Configure(database, "ordinary", DatabaseFindMode.AllTerms);
        var find = database.FindInTableAsync();
        var stop = Descendants(database).OfType<Button>().Single(button => button.Name == "DatabaseFindStopButton");
        Require(stop.IsEnabled, "Stop was unavailable during Find.");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await find;
        await Settle();
        Require(database.FindState == "Cancelled" && database.FindMatchCount == 0, "Cancelled Find published results or an unclear state.");
        await FindAsync(database, "needle 東京", DatabaseFindMode.AllTerms);
        Require(database.FindMatchCount == 2, "Find could not run after cancellation.");
        checks.Add(new { check = "stop-and-restart", file, state = "Cancelled", restartMatches = database.FindMatchCount,
            scope = "Immediate UI cancellation request; not cancellation latency or proof of a long active scan." });
    }

    private static async Task CheckChangedSourceAsync(DatabasePreviewControl database, string path, List<object> checks)
    {
        DateTime original = File.GetLastWriteTimeUtc(path);
        try
        {
            File.SetLastWriteTimeUtc(path, original.AddSeconds(2));
            await FindAsync(database, "needle", DatabaseFindMode.AllTerms, allowUnavailable: true);
            Require(database.FindState == "Unavailable" && database.FindMatchCount == 0, "Changed-source Find did not invalidate its matches.");
            checks.Add(new { check = "changed-source-rejected", file = Path.GetFileName(path),
                scope = "Generated source timestamp intentionally changed and restored; source bytes not written." });
        }
        finally { File.SetLastWriteTimeUtc(path, original); }
    }

    private static async Task FindAsync(DatabasePreviewControl database, string query, DatabaseFindMode mode, string? column = null,
        bool matchCase = false, bool allowUnavailable = false)
    {
        Configure(database, query, mode, column, matchCase);
        await database.FindInTableAsync();
        await Settle();
        Require(database.FindState != "Searching" && (allowUnavailable || database.FindState != "Unavailable"), "Find did not finish successfully.");
    }

    private static void Configure(DatabasePreviewControl database, string query, DatabaseFindMode mode, string? column = null, bool matchCase = false)
    {
        Control<ComboBox>(database, "DatabaseFindModeSelector").SelectedValue = mode;
        var columns = Control<ComboBox>(database, "DatabaseFindColumnSelector");
        if (column is null) columns.SelectedIndex = 0;
        else columns.SelectedValue = column;
        Control<CheckBox>(database, "DatabaseFindMatchCase").IsChecked = matchCase;
        Control<TextBox>(database, "DatabaseFindTextBox").Text = query;
    }

    private static void AssertCell(DatabasePreviewControl database, string path, long row, string column, bool requireHighlight)
    {
        try
        {
            var location = database.CurrentLocation;
            Require(location is not null && location.Table == DatabaseTestFiles.TableName && location.RowNumber == row &&
                location.Column == column && !string.IsNullOrWhiteSpace(location.RowKey) && location.Fingerprint == DatabaseFiles.Fingerprint(path),
                "Find navigation lost table, row, column, key or fingerprint.");
            var grid = Control<DataGrid>(database, "DatabaseRecordsGrid");
            Require(grid.IsReadOnly && grid.CurrentCell.Item is DataRowView current && (long)current["r"] == row &&
                grid.CurrentCell.Column?.Header?.ToString() == column, "Find did not select its matching grid cell.");
            var cell = Descendants(grid).OfType<DataGridCell>().SingleOrDefault(candidate => candidate.IsSelected &&
                candidate.Column.Header?.ToString() == column && candidate.DataContext is DataRowView record && (long)record["r"] == row);
            Require(cell is not null, "Matching database cell was not realized.");
            var bounds = cell!.TransformToAncestor(grid).TransformBounds(new Rect(cell.RenderSize));
            Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= grid.ActualWidth + 2 && bounds.Bottom <= grid.ActualHeight + 2,
                "Matching database cell was not scrolled into view.");
            if (requireHighlight)
                Require(Descendants(cell).OfType<TextBlock>().SelectMany(block => block.Inlines.OfType<Run>()).Any(run => run.Background is not null),
                    "The matching cell has no highlighted text.");
        }
        catch (Exception error)
        {
            SaveCellFailure(database, path, row, column, requireHighlight, error);
            throw;
        }
    }

    private static void SaveCellFailure(DatabasePreviewControl database, string path, long row, string column, bool requireHighlight, Exception failure)
    {
        string prefix = Path.Combine(Path.GetDirectoryName(_output)!, Path.GetFileNameWithoutExtension(_output) +
            "-cell-failure-" + (++_cellFailureCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var window = Window.GetWindow(database);
            var grid = Descendants(database).OfType<DataGrid>().FirstOrDefault(control => control.Name == "DatabaseRecordsGrid");
            var ancestors = new List<FrameworkElement>();
            for (DependencyObject? element = grid; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is FrameworkElement framework) ancestors.Add(framework);
            var report = new
            {
                failure = failure.ToString(), expected = new { path, row, column, requireHighlight },
                database.PreviewState, database.FindState, database.FindMatchCount, database.CurrentFindMatchNumber, database.CurrentLocation,
                window = window is null ? null : new { window.Title, window.ActualWidth, window.ActualHeight, window.IsVisible, window.Left, window.Top },
                databaseLayout = ElementLayout(database, window),
                grid = grid is null ? null : new
                {
                    layout = ElementLayout(grid, window), grid.Items.Count,
                    currentRow = RowNumber(grid.CurrentCell.Item), currentColumn = grid.CurrentCell.Column?.Header?.ToString(),
                    selected = grid.SelectedCells.Select(cell => new { row = RowNumber(cell.Item), column = cell.Column?.Header?.ToString() }).ToArray(),
                    columns = grid.Columns.Select(value => new { header = value.Header?.ToString(), value.DisplayIndex, value.ActualWidth,
                        configuredWidth = value.Width.Value, unit = value.Width.UnitType.ToString(), value.Visibility }).ToArray(),
                    cells = Descendants(grid).OfType<DataGridCell>().Select(cell => new
                    {
                        row = RowNumber(cell.DataContext), column = cell.Column.Header?.ToString(), cell.IsSelected, cell.IsVisible,
                        cell.ActualWidth, cell.ActualHeight, gridBounds = Bounds(cell, grid), windowBounds = Bounds(cell, window),
                    }).ToArray(),
                    rows = Descendants(grid).OfType<DataGridRow>().Select(item => new { row = RowNumber(item.Item), item.IsVisible,
                        item.ActualWidth, item.ActualHeight, gridBounds = Bounds(item, grid) }).ToArray(),
                    scrolls = Descendants(grid).OfType<ScrollViewer>().Concat(ancestors.OfType<ScrollViewer>()).Distinct().Select(scroll => new
                    {
                        layout = ElementLayout(scroll, window), scroll.HorizontalOffset, scroll.VerticalOffset, scroll.ScrollableWidth, scroll.ScrollableHeight,
                        scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth, scroll.ExtentHeight, scroll.CanContentScroll,
                    }).ToArray(),
                    ancestors = ancestors.Select(element => ElementLayout(element, window)).ToArray(),
                },
                note = "DataGrid logical scroll viewport/offset units may be rows rather than pixels when CanContentScroll is true. Bounds are device-independent pixels.",
            };
            File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
            if (window is not null) Save(window, prefix + ".png");
        }
        catch (Exception diagnosticError)
        {
            File.WriteAllText(prefix + ".diagnostic-error.txt", diagnosticError.ToString());
        }
    }

    private static long? RowNumber(object? item) => item is DataRowView row && row.Row.Table.Columns.Contains("r") ? (long)row["r"] : null;

    private static object ElementLayout(FrameworkElement element, Visual? ancestor) => new
    {
        type = element.GetType().Name, element.Name, element.ActualWidth, element.ActualHeight,
        desiredWidth = element.DesiredSize.Width, desiredHeight = element.DesiredSize.Height,
        renderWidth = element.RenderSize.Width, renderHeight = element.RenderSize.Height,
        element.MinWidth, element.MinHeight, element.MaxWidth, element.MaxHeight, element.ClipToBounds, element.IsVisible,
        visibility = element.Visibility.ToString(), bounds = Bounds(element, ancestor),
    };

    private static object? Bounds(Visual element, Visual? ancestor)
    {
        if (ancestor is null || element is not FrameworkElement framework) return null;
        try
        {
            var bounds = element.TransformToAncestor(ancestor).TransformBounds(new Rect(framework.RenderSize));
            return new { bounds.Left, bounds.Top, bounds.Width, bounds.Height, bounds.Right, bounds.Bottom };
        }
        catch (InvalidOperationException error) { return new { error = error.Message }; }
    }

    private static object CheckLayout(Window window, DatabasePreviewControl database, string description)
    {
        var grid = Control<DataGrid>(database, "DatabaseRecordsGrid");
        Require(grid.ActualHeight > 45 && grid.ActualWidth > 100, "Find controls leave no space for records: " + description);
        foreach (string name in new[] { "DatabaseFindTextBox", "DatabaseFindOptionsButton" })
        {
            var control = Descendants(database).OfType<FrameworkElement>().Single(candidate => candidate.Name == name);
            Require(control.IsVisible && control.ActualWidth > 8 && control.ActualHeight > 8 &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), "Find control is unavailable or unnamed: " + name);
            var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
            Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
                "Find control extends beyond its window: " + name + " in " + description);
        }
        return new { check = "layout", description, width = window.ActualWidth, height = window.ActualHeight,
            gridWidth = grid.ActualWidth, gridHeight = grid.ActualHeight };
    }

    private static async Task<object> CheckOptionsAsync(Window window, DatabasePreviewControl database, string path, string description, string screenshotSuffix)
    {
        var button = Control<Button>(database, "DatabaseFindOptionsButton");
        var mode = Control<ComboBox>(database, "DatabaseFindModeSelector");
        int matches = database.FindMatchCount;
        var selected = database.CurrentLocation ?? throw new InvalidOperationException("No current database location before opening Find options.");
        Require(button.IsVisible && button.IsEnabled && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
            "Find options are unavailable or unnamed: " + description);
        if (mode.IsVisible)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
        }
        Require(!mode.IsVisible, "Find options did not collapse before the open/close check.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var controls = new List<object>();
        foreach (string name in new[] { "DatabaseFindModeSelector", "DatabaseFindColumnSelector", "DatabaseFindMatchCase" })
        {
            var control = Descendants(database).OfType<FrameworkElement>().Single(candidate => candidate.Name == name);
            Require(control.IsVisible && control.ActualWidth > 8 && control.ActualHeight > 8 &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), "Expanded Find option is unavailable or unnamed: " + name);
            var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
            Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
                "Expanded Find option extends beyond its window: " + name + " in " + description);
            controls.Add(new { name, bounds.Left, bounds.Top, bounds.Width, bounds.Height });
        }
        Save(window, ImagePath(_output, screenshotSuffix));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Require(!mode.IsVisible && database.FindMatchCount == matches, "Closing Find options changed the completed matches.");
        AssertCell(database, path, selected.RowNumber, selected.Column, requireHighlight: true);
        return new { check = "find-options-open-close", description, width = window.ActualWidth, height = window.ActualHeight,
            mode = mode.SelectedValue?.ToString(), column = Control<ComboBox>(database, "DatabaseFindColumnSelector").SelectedValue?.ToString(),
            caseSensitive = Control<CheckBox>(database, "DatabaseFindMatchCase").IsChecked, matches, controls };
    }

    private static async Task<object> CheckQuickFindSizingAsync(QuickSearchWindow window, DatabasePreviewControl database, string path)
    {
        var border = (FrameworkElement)window.FindName("InlinePreviewBorder");
        int matches = database.FindMatchCount;
        var selected = database.CurrentLocation ?? throw new InvalidOperationException("No current cell before checking Quick Find sizing.");
        // Establish an actual compact window before opening the DB. Assigning 420px
        // while the DB minimum is active is a manual, HWND-clamped resize and is
        // intentionally preserved when the preview closes.
        var quick = (QuickSearchViewModel)window.DataContext;
        quick.IsPreviewVisible = false;
        await Settle();
        window.Height = 420;
        await Settle();
        Require(Math.Abs(window.ActualHeight - 420) < 1 && Math.Abs(window.MinHeight - 420) < 1,
            "The hidden-preview setup did not establish an actual compact window. " + JsonSerializer.Serialize(QuickHeightState(window)));
        var initialCompact = QuickHeightState(window);
        quick.IsPreviewVisible = true;
        database = await WaitDatabaseAsync((FileDocumentPreview)window.FindName("QuickFileDocumentPreview"));
        CheckQuickPreviewGeometry(window, border, database, "Database automatically grown from compact window");
        await FindAsync(database, "needle 東京", DatabaseFindMode.AllTerms);
        Require(database.FindMatchCount == matches && database.CurrentLocation == selected,
            "Reopening the database for automatic-growth sizing changed its known Find result.");
        var toggle = Control<Button>(database, "DatabaseFindToggleButton");
        Require(database.IsFindVisible && window.ActualHeight >= 539 && window.MinHeight >= 540,
            "Quick Find did not reserve its supported minimum window. " + JsonSerializer.Serialize(QuickGeometry(window, border, database)));
        CheckQuickPreviewGeometry(window, border, database, "Find initially open");
        var opened = QuickGeometry(window, border, database);

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Require(!database.IsFindVisible && window.MinHeight >= 420,
            "Hiding Find did not retain the database's readable minimum. " + JsonSerializer.Serialize(QuickGeometry(window, border, database)));
        CheckQuickPreviewGeometry(window, border, database, "Find hidden at current height");
        Require(database.FindMatchCount == matches && database.CurrentLocation == selected, "Hiding Find lost its matches or selected cell.");
        AssertCell(database, path, selected.RowNumber, selected.Column, requireHighlight: false);
        var hidden = QuickGeometry(window, border, database);

        Require(!database.IsFindVisible && window.ActualHeight >= 420 && Math.Abs(window.ActualHeight - window.MinHeight) < 1,
            "Quick Search did not retain a readable compact database preview. " + JsonSerializer.Serialize(QuickGeometry(window, border, database)));
        CheckQuickPreviewGeometry(window, border, database, "Find hidden at dynamic database minimum");
        var ordinaryMinimum = QuickGeometry(window, border, database);
        double databaseCompactHeight = window.ActualHeight;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Require(database.IsFindVisible && window.ActualHeight >= 539 && window.MinHeight >= 540 && window.ActualHeight >= databaseCompactHeight,
            "Opening Find from the database minimum did not reserve the supported Find height. " +
            JsonSerializer.Serialize(new { before = ordinaryMinimum, after = QuickGeometry(window, border, database), initiallyOpen = opened }));
        CheckQuickPreviewGeometry(window, border, database, "Find auto-grown from the dynamic database minimum");
        Require(database.FindMatchCount == matches && database.CurrentLocation == selected, "Reopening Find lost its matches or selected cell.");
        AssertCell(database, path, selected.RowNumber, selected.Column, requireHighlight: true);
        var grown = QuickGeometry(window, border, database);
        double findExpandedHeight = window.ActualHeight;

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var shrinkEvidence = new { ordinaryMinimum, before = grown, after = QuickGeometry(window, border, database) };
        File.WriteAllText(_output + "." + Path.GetExtension(path).TrimStart('.') + "-quick-find-shrink-state.json",
            JsonSerializer.Serialize(shrinkEvidence, new JsonSerializerOptions { WriteIndented = true }));
        Require(!database.IsFindVisible && window.MinHeight >= 420 && Math.Abs(window.ActualHeight - window.MinHeight) < 1 &&
            window.ActualHeight < findExpandedHeight - 1 && PrivateValue(window, "_heightBeforePreviewGrowth") is double compactBaseline &&
            Math.Abs(compactBaseline - 420) < 1,
            "Hiding an auto-grown Find did not restore the current readable database minimum and retain its compact baseline. " +
            JsonSerializer.Serialize(shrinkEvidence));
        CheckQuickPreviewGeometry(window, border, database, "Find hidden after auto-growth");
        var restored = QuickGeometry(window, border, database);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Require(database.IsFindVisible && database.FindMatchCount == matches && database.CurrentLocation == selected,
            "Restoring the Find view changed its completed result or selection.");
        CheckQuickPreviewGeometry(window, border, database, "Find reopened after compact restoration");
        AssertCell(database, path, selected.RowNumber, selected.Column, requireHighlight: true);
        var reopened = QuickGeometry(window, border, database);
        Save(window, ImagePath(_output, Path.GetExtension(path).TrimStart('.') + "-quick-restored-find"));
        ((QuickSearchViewModel)window.DataContext).IsPreviewVisible = false;
        await Settle();
        var closingEvidence = new { initialCompact, databaseCompactHeight, ordinaryMinimum, grown, restored, reopened,
            after = QuickHeightState(window) };
        File.WriteAllText(_output + "." + Path.GetExtension(path).TrimStart('.') + "-quick-sizing-state.json",
            JsonSerializer.Serialize(closingEvidence, new JsonSerializerOptions { WriteIndented = true }));
        Save(window, ImagePath(_output, Path.GetExtension(path).TrimStart('.') + "-quick-preview-closed"));
        Require(Math.Abs(window.MinHeight - 420) < 1 && Math.Abs(window.ActualHeight - 420) < 1,
            "Hiding the auto-grown database preview did not restore the original compact window. " +
            JsonSerializer.Serialize(closingEvidence));
        var previewHidden = new { window.ActualHeight, window.MinHeight, border.IsVisible };
        return new
        {
            check = "quick-find-layout-restoration", file = Path.GetFileName(path), matches, selected,
            initialCompact, opened, hidden, ordinaryMinimum, grown, restored, reopened, previewHidden,
            scope = "Database/Find minima follow measured header and record chrome, retain a readable grid, and restore 420px after the auto-grown preview closes. The existing 420px preview cap remains.",
        };
    }

    private static void CheckQuickPreviewGeometry(QuickSearchWindow window, FrameworkElement border,
        DatabasePreviewControl database, string description)
    {
        var grid = Control<DataGrid>(database, "DatabaseRecordsGrid");
        var client = (FrameworkElement)window.FindName("QuickSearchLayout");
        var bounds = border.TransformToAncestor(client).TransformBounds(new Rect(border.RenderSize));
        bool fits = border.IsVisible && border.ActualHeight >= (database.IsFindVisible ? 339 : 299) && border.ActualHeight <= 421 &&
            bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= client.ActualWidth + 1 && bounds.Bottom <= client.ActualHeight + 1;
        bool usable = grid.IsVisible && grid.ActualHeight > 45 && grid.ActualWidth > 100;
        if (!fits || !usable)
        {
            string suffix = "quick-layout-failure-" + DateTime.UtcNow.Ticks;
            var evidence = new { description, fits, usable, geometry = QuickGeometry(window, border, database) };
            File.WriteAllText(_output + "." + suffix + ".json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Save(window, ImagePath(_output, suffix));
            throw new InvalidOperationException("Quick preview is clipped or leaves insufficient record space: " +
                description + ". " + JsonSerializer.Serialize(evidence));
        }
        if (database.IsFindVisible) _ = CheckLayout(window, database, description);
    }

    private static object QuickGeometry(QuickSearchWindow window, FrameworkElement border, DatabasePreviewControl database)
    {
        var grid = Control<DataGrid>(database, "DatabaseRecordsGrid");
        var client = (FrameworkElement)window.FindName("QuickSearchLayout");
        var header = (FrameworkElement)window.FindName("QuickScopeHeader");
        return new
        {
            window.ActualWidth, window.ActualHeight, window.MinWidth, window.MinHeight,
            heightState = QuickHeightState(window),
            previewHeight = border.Height, previewActualHeight = border.ActualHeight,
            database.IsFindVisible, gridHeight = grid.ActualHeight, gridWidth = grid.ActualWidth,
            clientWidth = client.ActualWidth, clientHeight = client.ActualHeight, headerHeight = header.ActualHeight,
            previewClientBounds = Bounds(border, client), previewWindowBounds = Bounds(border, window),
        };
    }

    private static object QuickHeightState(QuickSearchWindow window) => new
    {
        window.Height, window.ActualHeight, window.MinHeight,
        localHeight = window.ReadLocalValue(FrameworkElement.HeightProperty)?.ToString(),
        beforeAutomaticGrowth = PrivateValue(window, "_heightBeforePreviewGrowth"),
        automaticHeight = PrivateValue(window, "_automaticPreviewHeight"),
    };

    private static async Task<DatabasePreviewControl> WaitDatabaseAsync(FileDocumentPreview view)
    {
        try
        {
            await WaitUntilAsync(() => view.PreviewState is not "Loading" and not "Empty" &&
                Descendants(view).OfType<DatabasePreviewControl>().FirstOrDefault()?.PreviewState is "Ready" or "Empty" or "Unavailable");
            var database = Descendants(view).OfType<DatabasePreviewControl>().Single();
            Require(view.PreviewState == "Database" && database.PreviewState == "Ready", "Shared database preview did not load.");
            await Settle();
            return database;
        }
        catch (Exception failure)
        {
            SaveWaitFailure(view, failure);
            throw;
        }
    }

    private static void SaveWaitFailure(FileDocumentPreview view, Exception failure)
    {
        string prefix = Path.Combine(Path.GetDirectoryName(_output)!, Path.GetFileNameWithoutExtension(_output) +
            "-wait-failure-" + (++_waitFailureCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var elements = new[] { (DependencyObject)view }.Concat(Descendants(view)).ToArray();
            var window = Window.GetWindow(view);
            var report = new
            {
                failure = failure.ToString(),
                view.PreviewState, view.IsLoaded, view.IsVisible, view.Visibility,
                path = view.Result?.FullPath,
                preferredHit = view.PreferredHit?.Database,
                bodyType = (view.FindName("Body") as ContentControl)?.Content?.GetType().FullName,
                databases = elements.OfType<DatabasePreviewControl>().Select(database => new
                {
                    database.PreviewState, database.FindState, database.FindStatusText,
                    database.CurrentLocation, database.IsLoaded, database.IsVisible, database.IsEnabled,
                    sourceGeneration = PrivateValue(database, "_sourceGeneration"),
                    pageGeneration = PrivateValue(database, "_generation"),
                    disposed = PrivateValue(database, "_disposed"),
                    sourcePath = PrivateValue(database, "_sourcePath"),
                    snapshotPath = (PrivateValue(database, "_session") as DatabaseReadSession) is { } session
                        ? typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) : null,
                }).ToArray(),
                textBlocks = elements.OfType<TextBlock>().Select(block => new
                {
                    block.Name, text = Bounded(block.Text, 4096), block.IsVisible,
                    inlineText = Bounded(string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)), 4096),
                }).ToArray(),
                fallbackText = elements.OfType<ICSharpCode.AvalonEdit.TextEditor>().Select(editor => Bounded(editor.Text, 16384)).ToArray(),
                windows = System.Windows.Application.Current?.Windows.Cast<Window>().Select(item => new
                {
                    item.Title, item.IsVisible, item.IsLoaded, item.ActualWidth, item.ActualHeight,
                    contentType = item.Content?.GetType().FullName,
                }).ToArray(),
                previewExceptions = PreviewExceptions.ToArray(),
            };
            File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
            if (window is not null) Save(window, prefix + ".png");
        }
        catch (Exception diagnosticError)
        {
            File.WriteAllText(prefix + ".diagnostic-error.txt", diagnosticError.ToString());
        }
    }

    private static object? PrivateValue(object owner, string field) => owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);

    private static string Bounded(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";

    private sealed class PreviewExceptionCapture : IDisposable
    {
        public PreviewExceptionCapture()
        {
            PreviewExceptions.Clear();
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }

        private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (_recordingException) return;
            _recordingException = true;
            try
            {
                string stack = args.Exception.StackTrace ?? "";
                if (!stack.Contains("FileDocumentPreview", StringComparison.Ordinal) &&
                    !stack.Contains("DatabasePreviewControl", StringComparison.Ordinal)) return;
                PreviewExceptions.Enqueue(new(DateTime.UtcNow, Environment.CurrentManagedThreadId,
                    args.Exception.GetType().FullName ?? args.Exception.GetType().Name,
                    Bounded(args.Exception.Message, 4096), Bounded(stack, 16384)));
                while (PreviewExceptions.Count > 512) PreviewExceptions.TryDequeue(out _);
            }
            finally { _recordingException = false; }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            try
            {
                File.WriteAllText(_output + ".first-chance.json", JsonSerializer.Serialize(PreviewExceptions.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { /* Diagnostics must not replace the original probe failure. */ }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(25, timeout.Token);
        await Settle();
    }

    private static T Control<T>(DependencyObject parent, string name) where T : FrameworkElement =>
        Descendants(parent).OfType<T>().Single(control => control.Name == name);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string ImagePath(string output, string suffix) => Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + suffix + ".png");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Settle() { await Task.Delay(120); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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
