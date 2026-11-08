using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Databases;
using FileSearch.Gui.Controls;
using Microsoft.Data.Sqlite;

namespace FileSearch.Gui.Tests;

public sealed class DatabaseGridDisplayTests : IAsyncDisposable
{
    private const string Table = "Grid \"records\"";
    private const string Marker = "distant-target 東京";
    private const int Rows = 800;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseGridGui", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DatabaseGridDisplayTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task SortedPageAndFindNavigationSelectSnapshotRowsAndKeys(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "sorted.db", 3);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        var descriptor = Assert.IsType<DependencyPropertyDescriptor>(DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid)));
        EventHandler sort = (_, _) => { if (grid.ItemsSource is DataView data) data.Sort = "r DESC"; };
        descriptor.AddValueChanged(grid, sort);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 730, 2), Token);
            snapshot = SnapshotPath(preview);
            Assert.Equal(750L, ((DataRowView)grid.Items[0])["r"]);
            await AssertSelectedVisibleAsync(preview, path, 730, 2);

            Named<TextBox>(preview, "DatabaseFindTextBox").Text = Marker;
            await preview.FindInTableAsync();
            Assert.Equal(2, preview.FindMatchCount);
            await AssertSelectedVisibleAsync(preview, path, 750, 2);

            await preview.MoveFindMatchAsync(1);
            Assert.Equal(800L, ((DataRowView)grid.Items[0])["r"]);
            await AssertSelectedVisibleAsync(preview, path, 800, 2);
            Assert.Equal(before, Hash(path));
        }
        finally
        {
            descriptor.RemoveValueChanged(grid, sort);
            window.Close();
            if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot));
        }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task ResizeAndFindOptionBurstsKeepCurrentCellVisible(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "resize.db", 4);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 750, 3), Token);
            snapshot = SnapshotPath(preview);
            Named<Button>(preview, "DatabaseFindToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var options = Named<Button>(preview, "DatabaseFindOptionsButton");
            foreach (var (width, height) in new[] { (760d, 620d), (640d, 540d), (940d, 680d), (680d, 560d), (640d, 540d) })
            {
                window.Width = width; window.Height = height;
                options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await AssertSelectedVisibleAsync(preview, path, 750, 3);
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await AssertSelectedVisibleAsync(preview, path, 750, 3);
            Named<Button>(preview, "DatabaseFindToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await AssertSelectedVisibleAsync(preview, path, 750, 3);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task RestoringFindWithOpenOptionsRecyclesRowsAndRetainsVisibleMatches(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "restore-find.db", 3);
        string before = Hash(path);
        using var original = new DatabasePreviewControl();
        using var expanded = new DatabasePreviewControl();
        var originalWindow = Show(original);
        Window? expandedWindow = null;
        var snapshots = new List<string>();
        try
        {
            await original.InitializeAsync(path, Location(path, 1, 2), Token);
            snapshots.Add(SnapshotPath(original));
            Named<TextBox>(original, "DatabaseFindTextBox").Text = Marker;
            await original.FindInTableAsync();
            await AssertSelectedVisibleAsync(original, path, 750, 2);
            Named<Button>(original, "DatabaseFindOptionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var state = Assert.IsType<DatabasePreviewFindState>(original.CaptureFindState());
            Assert.True(state.IsOpen); Assert.True(state.OptionsOpen);

            expandedWindow = Show(expanded);
            expandedWindow.Width = 1000; expandedWindow.Height = 720;
            await expanded.InitializeAsync(path, original.CurrentLocation, Token);
            snapshots.Add(SnapshotPath(expanded));
            await AssertSelectedVisibleAsync(expanded, path, 750, 2);
            var grid = Named<DataGrid>(expanded, "DatabaseRecordsGrid");
            double heightWithoutFind = grid.ActualHeight;
            var visibleWithoutFind = VisibleRows(grid);
            Assert.True(visibleWithoutFind.Count >= 20, "The initial expanded viewport must contain enough ordinary rows to recycle when Find is restored.");

            await expanded.RestoreFindStateAsync(state);
            await AssertSelectedVisibleAsync(expanded, path, 750, 2);
            Assert.True(expanded.IsFindVisible);
            Assert.True(Named<ComboBox>(expanded, "DatabaseFindModeSelector").IsVisible);
            Assert.Equal("Ready", expanded.FindState);
            Assert.Equal(2, expanded.FindMatchCount);
            Assert.Equal(1, expanded.CurrentFindMatchNumber);
            var visibleWithFind = VisibleRows(grid);
            Assert.True(grid.ActualHeight < heightWithoutFind - 40, "Restoring Find and its options must shrink the expanded records viewport.");
            Assert.True(visibleWithFind.Count < visibleWithoutFind.Count, $"Expected fewer visible records with restored Find options: before {visibleWithoutFind.Count}, after {visibleWithFind.Count}.");
            Assert.NotEmpty(visibleWithoutFind.Except(visibleWithFind));
            AssertSelectedHighlights(grid);

            await expanded.MoveFindMatchAsync(1);
            await AssertSelectedVisibleAsync(expanded, path, 800, 2);
            Assert.Equal(2, expanded.CurrentFindMatchNumber);
            AssertSelectedHighlights(grid);
            await expanded.MoveFindMatchAsync(-1);
            await AssertSelectedVisibleAsync(expanded, path, 750, 2);
            Assert.Equal(1, expanded.CurrentFindMatchNumber);
            AssertSelectedHighlights(grid);
            Assert.Equal(before, Hash(path));
        }
        finally
        {
            expandedWindow?.Close(); originalWindow.Close();
            await WaitUntilAsync(() => snapshots.All(snapshot => !File.Exists(snapshot)));
        }
    });

    private static void AssertSelectedHighlights(DataGrid grid)
    {
        var block = SelectedValueBlock(grid);
        string marked = string.Join(' ', block.Inlines.OfType<Run>().Where(run => run.Background is not null).Select(run => run.Text));
        Assert.Contains("distant-target", marked); Assert.Contains("東京", marked);
        AssertCellCopyAndToolTip(grid, block, Marker);
    }

    private static HashSet<long> VisibleRows(DataGrid grid)
    {
        var visible = new HashSet<long>();
        foreach (var row in Descendants(grid).OfType<DataGridRow>())
        {
            if (row.Item is not DataRowView value || !row.IsVisible || row.ActualHeight <= 0) continue;
            FrameworkElement viewport = Ancestors(row).OfType<ScrollContentPresenter>().FirstOrDefault() ?? (FrameworkElement)grid;
            var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            if (bounds.Bottom > 0 && bounds.Top < viewport.ActualHeight) visible.Add((long)value["r"]);
        }
        return visible;
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task WideTablesRealizeSelectedDistantColumnAndKeepRowsVirtualized(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "wide.db", 40);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        window.Width = 680; window.Height = 560;
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 750, 39), Token);
            snapshot = SnapshotPath(preview);
            await AssertSelectedVisibleAsync(preview, path, 750, 39);
            AssertVirtualizedRows(preview);

            Named<TextBox>(preview, "DatabaseFindTextBox").Text = Marker;
            await preview.FindInTableAsync();
            Assert.Equal(2, preview.FindMatchCount);
            await AssertSelectedVisibleAsync(preview, path, 750, 39);
            await preview.MoveFindMatchAsync(1);
            await AssertSelectedVisibleAsync(preview, path, 800, 39);
            await preview.MoveFindMatchAsync(-1);
            await AssertSelectedVisibleAsync(preview, path, 750, 39);
            AssertVirtualizedRows(preview);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task PageChangesUpdateAllValuesAcrossFullPartialAndEmptyPages(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "page-values.db", 3);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1, 2), Token);
            snapshot = SnapshotPath(preview);
            AssertPageValues(preview, 1, 250, 3);
            foreach (var (row, count) in new[] { (251L, 250), (501L, 250), (751L, 50), (1001L, 0), (1L, 250) })
            {
                await GoToRowAsync(preview, row);
                AssertPageValues(preview, row, count, 3);
                if (count == 0)
                {
                    Assert.Equal("Empty", preview.PreviewState);
                    Assert.Equal("", preview.CurrentLocation!.RowKey);
                }
                else await AssertSelectedVisibleAsync(preview, path, row, 2);
            }
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task PagingBetweenDistantHighlightAndShortCellRetainsFindAndFullCopyValues(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string fullValue = new string('x', 8000) + " long-needle 東京 " + new string('y', 500);
        string path = await CreateAsync(kind, "long-recycle.db", 3, fullValue);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1, 2), Token);
            snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "long-needle 東京";
            await preview.FindInTableAsync();
            Assert.Equal(1, preview.FindMatchCount);
            await AssertSelectedVisibleAsync(preview, path, 1, 2);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var longBlock = SelectedValueBlock(grid);
            Assert.Contains("long-needle", longBlock.Text);
            Assert.Contains(longBlock.Inlines.OfType<Run>(), run => run.Background is not null);
            AssertCellCopyAndToolTip(grid, longBlock, fullValue);

            await GoToRowAsync(preview, 251);
            await AssertSelectedVisibleAsync(preview, path, 251, 2);
            Assert.Equal("Ready", preview.FindState);
            Assert.Equal(1, preview.FindMatchCount);
            var shortBlock = SelectedValueBlock(grid);
            Assert.Equal("value-251-2", shortBlock.Text);
            Assert.DoesNotContain(shortBlock.Inlines.OfType<Run>(), run => run.Background is not null);
            AssertCellCopyAndToolTip(grid, shortBlock, "value-251-2");

            await preview.MoveFindMatchAsync(1);
            await AssertSelectedVisibleAsync(preview, path, 1, 2);
            Assert.Equal(1, preview.CurrentFindMatchNumber);
            longBlock = SelectedValueBlock(grid);
            Assert.Contains("long-needle", longBlock.Text);
            Assert.Contains(longBlock.Inlines.OfType<Run>(), run => run.Background is not null);
            AssertCellCopyAndToolTip(grid, longBlock, fullValue);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    private static TextBlock SelectedValueBlock(DataGrid grid)
    {
        var cell = Descendants(grid).OfType<DataGridCell>().Single(cell => cell.IsSelected &&
            ReferenceEquals(cell.DataContext, grid.CurrentCell.Item) && ReferenceEquals(cell.Column, grid.CurrentCell.Column));
        return Assert.Single(Descendants(cell).OfType<TextBlock>(), block => block.ToolTip is string);
    }

    private static void AssertCellCopyAndToolTip(DataGrid grid, TextBlock block, string expected)
    {
        Assert.Equal(expected, block.ToolTip);
        var copyBinding = Assert.IsAssignableFrom<BindingBase>(grid.CurrentCell.Column.ClipboardContentBinding);
        var copied = new TextBlock { DataContext = grid.CurrentCell.Item };
        BindingOperations.SetBinding(copied, TextBlock.TextProperty, copyBinding);
        Assert.Equal(expected, copied.Text);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task CustomViewSortFilterAndRowStateDoNotLeakIntoOtherPages(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "custom-view.db", 3);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1, 2), Token);
            snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var view = Assert.IsType<DataView>(grid.ItemsSource);
            view.Sort = "r DESC"; view.RowFilter = "r < 200";
            Assert.Equal(199, grid.Items.Count);
            await GoToRowAsync(preview, 251);
            AssertPageValues(preview, 251, 250, 3, ordered: false);
            await AssertSelectedVisibleAsync(preview, path, 251, 2);

            grid.Items.SortDescriptions.Add(new SortDescription("r", ListSortDirection.Descending));
            Assert.Equal(500L, ((DataRowView)grid.Items[0])["r"]);
            await GoToRowAsync(preview, 501);
            AssertPageValues(preview, 501, 250, 3, ordered: false);
            await AssertSelectedVisibleAsync(preview, path, 501, 2);

            Assert.IsType<DataView>(grid.ItemsSource).RowStateFilter = DataViewRowState.Unchanged;
            Assert.Empty(grid.Items);
            await GoToRowAsync(preview, 751);
            AssertPageValues(preview, 751, 50, 3, ordered: false);
            await AssertSelectedVisibleAsync(preview, path, 751, 2);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task CancelingBlockedPageKeepsAllPreviouslyDisplayedValues(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "cancel-page.db", 3);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1, 2), Token);
            snapshot = SnapshotPath(preview);
            var session = (DatabaseReadSession)typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
            var gate = (SemaphoreSlim)typeof(DatabaseReadSession).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
            await gate.WaitAsync(Token);
            try
            {
                StartGoToRow(preview, 501);
                Assert.Equal("Loading", preview.PreviewState);
                preview.CancelLoading();
                await WaitUntilAsync(() => preview.PreviewState != "Loading");
                AssertPageValues(preview, 1, 250, 3);
                await AssertSelectedVisibleAsync(preview, path, 1, 2);
                Assert.Contains("canceled", Named<TextBlock>(preview, "DatabaseLoadingStatus").Text, StringComparison.OrdinalIgnoreCase);
            }
            finally { gate.Release(); }
            await GoToRowAsync(preview, 251);
            AssertPageValues(preview, 251, 250, 3);
            await AssertSelectedVisibleAsync(preview, path, 251, 2);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    private static void AssertPageValues(DatabasePreviewControl preview, long first, int count, int textColumns, bool ordered = true)
    {
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        Assert.Equal(count, grid.Items.Count);
        var items = grid.Items.Cast<DataRowView>();
        if (!ordered) items = items.OrderBy(item => (long)item["r"]);
        int index = 0;
        foreach (var item in items)
        {
            long row = first + index++;
            Assert.Equal(row, (long)item["r"]);
            Assert.Equal((row * 10).ToString(CultureInfo.InvariantCulture), item["c0"].ToString());
            string key = (preview.CurrentLocation!.Engine == DatabaseKind.Sqlite ? "rowid=" : "Id=") + (row * 10).ToString(CultureInfo.InvariantCulture);
            Assert.Equal(key, item["key"].ToString());
            for (int column = 0; column < textColumns; column++)
            {
                string expected = (row is 750 or 800) && column == textColumns - 1 ? Marker :
                    string.Create(CultureInfo.InvariantCulture, $"value-{row}-{column}");
                Assert.Equal(expected, item["c" + (column + 1).ToString(CultureInfo.InvariantCulture)].ToString());
            }
        }
    }

    private static async Task GoToRowAsync(DatabasePreviewControl preview, long row)
    {
        StartGoToRow(preview, row);
        await WaitUntilAsync(() => preview.PreviewState != "Loading");
    }

    private static void StartGoToRow(DatabasePreviewControl preview, long row)
    {
        Named<TextBox>(preview, "DatabaseRowAddress").Text = row.ToString(CultureInfo.InvariantCulture);
        Descendants(preview).OfType<Button>().Single(button => Equals(button.Content, "Go to row"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task<string> CreateAsync(DatabaseKind kind, string name, int textColumns, string? firstLongValue = null)
    {
        string path = Path.Combine(_root, name);
        string schema = $"CREATE TABLE {DatabaseFiles.Quote(Table)} (Id INTEGER PRIMARY KEY," +
            string.Join(',', Enumerable.Range(0, textColumns).Select(index => DatabaseFiles.Quote(ColumnName(index)) + " TEXT")) + ")";
        string Value(int row, int column) => row == 1 && column == textColumns - 1 && firstLongValue is not null ? firstLongValue :
            (row is 750 or 800) && column == textColumns - 1 ? Marker :
            string.Create(CultureInfo.InvariantCulture, $"value-{row}-{column}");
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand(); command.CommandText = schema;
            await command.ExecuteNonQueryAsync(Token);
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {DatabaseFiles.Quote(Table)} VALUES ($id," +
                string.Join(',', Enumerable.Range(0, textColumns).Select(index => "$v" + index.ToString(CultureInfo.InvariantCulture))) + ")";
            command.Parameters.AddWithValue("$id", 0L);
            for (int column = 0; column < textColumns; column++)
                command.Parameters.AddWithValue("$v" + column.ToString(CultureInfo.InvariantCulture), "");
            for (int row = 1; row <= Rows; row++)
            {
                command.Parameters[0].Value = row * 10L;
                for (int column = 0; column < textColumns; column++) command.Parameters[column + 1].Value = Value(row, column);
                await command.ExecuteNonQueryAsync(Token);
            }
            transaction.Commit();
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            await using (var query = await database.ExecuteAsync(schema, Token)) { }
            var batch = database.PrepareInsertBatch(Table);
            for (int row = 1; row <= Rows; row++)
            {
                var values = new DbValue[textColumns + 1]; values[0] = DbValue.FromInteger(row * 10L);
                for (int column = 0; column < textColumns; column++) values[column + 1] = DbValue.FromText(Value(row, column));
                batch.AddRow(values);
            }
            await batch.ExecuteAsync(Token);
        }
        return path;
    }

    private static string ColumnName(int index) => "Field " + index.ToString("D2", CultureInfo.InvariantCulture);
    private static DatabaseLocation Location(string path, long row, int column) =>
        new(DatabaseFiles.Detect(path), Table, row, ColumnName(column), "", DatabaseFiles.Fingerprint(path));

    private static async Task AssertSelectedVisibleAsync(DatabasePreviewControl preview, string path, long row, int column)
    {
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        await WaitUntilAsync(() => IsSelectedVisible(grid, row, ColumnName(column)));
        Assert.Equal("Ready", preview.PreviewState);
        var location = Assert.IsType<DatabaseLocation>(preview.CurrentLocation);
        Assert.Equal(DatabaseFiles.Detect(path), location.Engine);
        Assert.Equal(Table, location.Table); Assert.Equal(row, location.RowNumber);
        Assert.Equal(ColumnName(column), location.Column);
        Assert.Equal(DatabaseFiles.Fingerprint(path), location.Fingerprint);
        Assert.Equal((location.Engine == DatabaseKind.Sqlite ? "rowid=" : "Id=") + (row * 10).ToString(CultureInfo.InvariantCulture), location.RowKey);
        var selected = Assert.IsType<DataRowView>(grid.CurrentCell.Item);
        Assert.Equal(row, (long)selected["r"]);
        Assert.Equal((row * 10).ToString(CultureInfo.InvariantCulture), selected["c0"].ToString());
        Assert.Equal("c" + (column + 1).ToString(CultureInfo.InvariantCulture), grid.CurrentCell.Column.SortMemberPath);
        Assert.Contains(grid.SelectedCells, cell => ReferenceEquals(cell.Item, selected) && ReferenceEquals(cell.Column, grid.CurrentCell.Column));
    }

    private static bool IsSelectedVisible(DataGrid grid, long row, string column)
    {
        if (grid.CurrentCell.Item is not DataRowView item || (long)item["r"] != row || grid.CurrentCell.Column?.Header?.ToString() != column) return false;
        var cell = Descendants(grid).OfType<DataGridCell>().FirstOrDefault(cell => cell.IsSelected &&
            cell.DataContext is DataRowView value && (long)value["r"] == row && cell.Column.Header?.ToString() == column);
        if (cell is null || !cell.IsVisible || cell.ActualWidth <= 0 || cell.ActualHeight <= 0) return false;
        FrameworkElement viewport = Ancestors(cell).OfType<ScrollContentPresenter>().FirstOrDefault() ?? (FrameworkElement)grid;
        var bounds = cell.TransformToAncestor(viewport).TransformBounds(new Rect(cell.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }

    private static void AssertVirtualizedRows(DatabasePreviewControl preview)
    {
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        var rows = Descendants(grid).OfType<DataGridRow>().Where(row => row.ActualHeight > 0).ToArray();
        Assert.NotEmpty(rows);
        int viewportRows = (int)Math.Ceiling(grid.ActualHeight / rows.Min(row => row.ActualHeight));
        int upperBound = Math.Min(grid.Items.Count - 1, viewportRows * 3 + 4);
        Assert.InRange(rows.Length, 1, upperBound);
        var cells = Descendants(grid).OfType<DataGridCell>().ToArray();
        Assert.InRange(cells.Length, 1, upperBound * grid.Columns.Count);
    }

    private static string SnapshotPath(DatabasePreviewControl preview)
    {
        var session = (DatabaseReadSession)typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
        return (string)typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(element => element.Name == name);
    private static Window Show(FrameworkElement preview)
    {
        var window = new Window
        {
            Content = preview,
            Width = 980,
            Height = 660,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show(); return window;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Database selected cell did not become visible or snapshot cleanup did not complete.");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(20, Token);
        }
    }
    private static async Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
    }
    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) when (attempt < 40) { await Task.Delay(50, CancellationToken.None); }
        }
    }
}
