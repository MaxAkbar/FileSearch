using System.ComponentModel;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Databases;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Highlighting;
using Microsoft.Data.Sqlite;

namespace FileSearch.Gui.Tests;

public sealed class DatabaseFindPreviewTests : IAsyncDisposable
{
    private const string Table = "Find \"records\"";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseFindGui", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public DatabaseFindPreviewTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) when (attempt < 40) { await Task.Delay(50, CancellationToken.None); }
        }
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task FindOnRow750SelectsDataColumnsNamedLikePreviewMetadata(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "collision.db", 750);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token);
            snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var query = Named<TextBox>(preview, "DatabaseFindTextBox");
            Assert.Equal(1, preview.CurrentLocation!.RowNumber);
            query.Text = "row-target 東京";
            await preview.FindInTableAsync();
            Assert.Equal("Ready", preview.FindState);
            Assert.Equal(1, preview.FindMatchCount);
            Assert.Equal(750, preview.CurrentLocation!.RowNumber);
            Assert.Equal("Row", preview.CurrentLocation.Column);
            Assert.Equal("c1", grid.CurrentCell.Column.SortMemberPath);
            Assert.Contains("7500", preview.CurrentLocation.RowKey);
            Assert.Equal(DatabaseFiles.Fingerprint(path), preview.CurrentLocation.Fingerprint);

            query.Text = "key-target 東京";
            await preview.FindInTableAsync();
            Assert.Equal("Record key", preview.CurrentLocation!.Column);
            Assert.Equal("c2", grid.CurrentCell.Column.SortMemberPath);
            Assert.Equal(750L, (long)((DataRowView)grid.CurrentCell.Item)["r"]);
            Assert.Contains("7500", preview.CurrentLocation.RowKey);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task StartingOrRepeatingFindSelectsItsFirstMatchingCellEvenWhenThatCellIsAlreadyCurrent() => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "first-match.sqlite", 25);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "10";
            Named<ComboBox>(preview, "DatabaseFindColumnSelector").SelectedValue = "Id";
            await preview.FindInTableAsync();
            Assert.True(preview.FindMatchCount > 1);
            Assert.Equal(1, preview.CurrentFindMatchNumber);
            Assert.Equal(1, preview.CurrentLocation!.RowNumber);
            Assert.Equal("Id", preview.CurrentLocation.Column);

            await preview.MoveFindMatchAsync(1);
            Assert.Equal(2, preview.CurrentFindMatchNumber);
            Assert.Equal(10, preview.CurrentLocation!.RowNumber);
            await preview.FindInTableAsync();
            Assert.Equal(1, preview.CurrentFindMatchNumber);
            Assert.Equal(1, preview.CurrentLocation!.RowNumber);
            Assert.Equal("Id", preview.CurrentLocation.Column);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task PageRecyclingAndFindInputChangesRemoveOldHighlightsAndRejectObsoleteResults() => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "recycling.sqlite", 750);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var query = Named<TextBox>(preview, "DatabaseFindTextBox");
            query.Text = "row-target";
            await preview.FindInTableAsync(); await SettleAsync();
            Assert.Contains("row-target", Marked(grid));

            Button(preview, "‹ Rows").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitUntilAsync(() => preview.PreviewState == "Ready" && grid.Items.Count > 0 &&
                grid.Items.Cast<DataRowView>().Min(item => (long)item["r"]) == 251);
            await SettleAsync();
            Assert.Empty(Marked(grid));
            Assert.Equal(1, preview.FindMatchCount);
            await preview.MoveFindMatchAsync(1); await SettleAsync();
            Assert.Contains("row-target", Marked(grid));

            int version = preview.FindHighlightVersion;
            query.Text = "never-present";
            await SettleAsync();
            Assert.Equal("Idle", preview.FindState);
            Assert.Equal(0, preview.FindMatchCount);
            Assert.True(preview.FindHighlightVersion > version);
            Assert.Empty(Marked(grid));

            query.Text = "row-target";
            var obsolete = preview.FindInTableAsync();
            Named<ComboBox>(preview, "DatabaseFindModeSelector").SelectedValue = DatabaseFindMode.Phrase;
            await obsolete;
            Assert.Equal("Idle", preview.FindState);
            Assert.Equal(0, preview.FindMatchCount);

            obsolete = preview.FindInTableAsync();
            query.Text = "different input";
            await obsolete;
            Assert.Equal("Idle", preview.FindState);
            Assert.Equal(0, preview.FindMatchCount);

            query.Text = "row-target";
            obsolete = preview.FindInTableAsync();
            var tables = Named<ComboBox>(preview, "DatabaseTableSelector");
            tables.SelectedItem = tables.Items.Cast<DatabaseTable>().Single(table => table.Name == "EmptyTable");
            await obsolete;
            await WaitUntilAsync(() => preview.PreviewState == "Empty");
            Assert.Equal("EmptyTable", preview.CurrentLocation!.Table);
            Assert.Equal("Idle", preview.FindState);
            Assert.Equal(0, preview.FindMatchCount);
            Assert.Empty(Marked(grid));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task SortedRowsAreSelectedBySnapshotRowNumberRatherThanTheirViewIndex() => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "sorted.sqlite", 750);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        var descriptor = Assert.IsType<DependencyPropertyDescriptor>(DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid)));
        EventHandler sort = (_, _) => { if (grid.ItemsSource is DataView view) view.Sort = "r DESC"; };
        descriptor.AddValueChanged(grid, sort);
        string? snapshot = null;
        try
        {
            var location = new DatabaseLocation(DatabaseKind.Sqlite, Table, 7, "Row", "", DatabaseFiles.Fingerprint(path));
            await preview.InitializeAsync(path, location, Token); snapshot = SnapshotPath(preview);
            Assert.Equal(250L, ((DataRowView)grid.Items[0])["r"]);
            Assert.Equal(7L, ((DataRowView)grid.CurrentCell.Item)["r"]);
            Assert.Equal(7, preview.CurrentLocation!.RowNumber);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "row-target";
            await preview.FindInTableAsync();
            Assert.Equal(750L, ((DataRowView)grid.Items[0])["r"]);
            Assert.Equal(750L, ((DataRowView)grid.CurrentCell.Item)["r"]);
            Assert.Equal(750, preview.CurrentLocation!.RowNumber);
            Assert.Contains("7500", preview.CurrentLocation.RowKey);
        }
        finally
        {
            descriptor.RemoveValueChanged(grid, sort); window.Close();
            if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot));
        }
    });

    [Fact]
    public Task ReinitializingAndDisposingDuringFindCannotPublishOldSessionResultsAndCleansSnapshots() => OnDispatcherAsync(async () =>
    {
        string first = await CreateAsync(DatabaseKind.Sqlite, "first.sqlite", 750);
        string second = await CreateAsync(DatabaseKind.Sqlite, "second.sqlite", 10);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        var snapshots = new List<string>();
        try
        {
            await preview.InitializeAsync(first, InitialLocation(first), Token);
            snapshots.Add(SnapshotPath(preview));
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "row-target";
            var oldFind = preview.FindInTableAsync();
            var newSource = preview.InitializeAsync(second, InitialLocation(second), Token);
            await Task.WhenAll(oldFind, newSource);
            snapshots.Add(SnapshotPath(preview));
            Assert.Equal(DatabaseFiles.Fingerprint(second), preview.CurrentLocation!.Fingerprint);
            Assert.Equal(0, preview.FindMatchCount);
            Assert.Equal("Idle", preview.FindState);
            await WaitUntilAsync(() => !File.Exists(snapshots[0]));

            var lastFind = preview.FindInTableAsync();
            preview.Dispose();
            await lastFind;
            await WaitUntilAsync(() => snapshots.All(snapshot => !File.Exists(snapshot)));
            Assert.Null(preview.CurrentLocation);
            Assert.Equal(0, preview.FindMatchCount);
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => snapshots.All(snapshot => !File.Exists(snapshot)));
        }
    });

    [Fact]
    public Task SourceChangesInvalidateFindNavigationAndItsHighlights() => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "changed.sqlite", 750);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "row-target";
            await preview.FindInTableAsync();
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            await preview.MoveFindMatchAsync(1); await SettleAsync();
            Assert.Equal("Unavailable", preview.PreviewState);
            Assert.Equal("Unavailable", preview.FindState);
            Assert.Equal(0, preview.FindMatchCount);
            Assert.Empty(Marked(Named<DataGrid>(preview, "DatabaseRecordsGrid")));
            Assert.Contains(Descendants(preview).OfType<TextBlock>(), block => block.Text.Contains("changed", StringComparison.OrdinalIgnoreCase));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task LongMatchedCellsKeepVisibleHighlightsAndFullTooltipAndCopyText() => OnDispatcherAsync(async () =>
    {
        string text = new string(' ', 200) + "needle" + new string('x', 153) + "😀" + new string('x', 58) + "😀tail";
        string path = await CreateAsync(DatabaseKind.Sqlite, "long.sqlite", 1, firstValue: text);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "needle";
            await preview.FindInTableAsync(); await SettleAsync();
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var column = grid.CurrentCell.Column;
            var content = column.GetCellContent(grid.CurrentCell.Item);
            Assert.NotNull(content);
            var block = new[] { content }.Concat(Descendants(content)).OfType<TextBlock>().Single(candidate => HitHighlight.GetHit(candidate) is not null);
            var excerpt = HitHighlight.GetHit(block)!;
            Assert.Contains("needle", Marked(block));
            string visible = excerpt.LineContent.TrimStart('…');
            Assert.False(char.IsWhiteSpace(visible[0]));
            Assert.True(HasValidSurrogates(excerpt.LineContent));
            Assert.Equal(text, block.ToolTip);
            var copyBinding = Assert.IsAssignableFrom<BindingBase>(column.ClipboardContentBinding);
            var copied = new TextBlock { DataContext = grid.CurrentCell.Item };
            BindingOperations.SetBinding(copied, TextBlock.TextProperty, copyBinding);
            await SettleAsync();
            Assert.Equal(text, copied.Text);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task DatabaseFindKeyboardScopeReservesEditingKeysAndKeepsWindowShortcutsOutsideThePreview()
        => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "keys.sqlite", 750);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var query = Named<TextBox>(preview, "DatabaseFindTextBox");
            var panel = Named<StackPanel>(preview, "DatabaseFindPanel");
            var rowAddress = Descendants(preview).OfType<TextBox>().Single(box =>
                System.Windows.Automation.AutomationProperties.GetName(box) == "Database row number");
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.True(DatabasePreviewControl.TryRouteFindKey(grid, Key.F, ModifierKeys.Control, out bool handled));
            Assert.True(handled);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.True(DatabasePreviewControl.TryRouteFindKey(query, Key.F, ModifierKeys.Control, out handled));
            Assert.True(handled);

            foreach (var source in new DependencyObject[] { grid, query })
            {
                Assert.True(DatabasePreviewControl.TryRouteFindKey(source, Key.C, ModifierKeys.Control, out handled));
                Assert.False(handled); // Let the grid/text box perform native copy.
            }
            Assert.True(DatabasePreviewControl.TryRouteFindKey(rowAddress, Key.Enter, ModifierKeys.None, out handled));
            Assert.False(handled); // The row address retains its own Enter handler.
            var outside = new TextBox();
            Assert.False(DatabasePreviewControl.TryRouteFindKey(outside, Key.Enter, ModifierKeys.None, out handled));
            Assert.False(handled);
            Assert.False(DatabasePreviewControl.TryRouteFindKey(outside, Key.F, ModifierKeys.Control, out handled));
            Assert.False(handled);

            query.Text = "row-target";
            Assert.True(DatabasePreviewControl.TryRouteFindKey(query, Key.Enter, ModifierKeys.None, out handled));
            Assert.True(handled);
            bool wasSearching = preview.FindState == "Searching";
            Assert.True(DatabasePreviewControl.TryRouteFindKey(query, Key.Escape, ModifierKeys.None, out handled));
            Assert.True(handled);
            await WaitUntilAsync(() => preview.FindState != "Searching");
            if (wasSearching)
            {
                Assert.Equal("Cancelled", preview.FindState);
                Assert.Equal(0, preview.FindMatchCount);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.True(DatabasePreviewControl.TryRouteFindKey(query, Key.Escape, ModifierKeys.None, out handled));
                Assert.True(handled);
            }
            Assert.Equal(Visibility.Collapsed, panel.Visibility);

            Assert.True(DatabasePreviewControl.TryRouteFindKey(grid, Key.F, ModifierKeys.Control, out handled));
            Assert.True(handled);
            Assert.True(DatabasePreviewControl.TryRouteFindKey(query, Key.Enter, ModifierKeys.None, out handled));
            Assert.True(handled);
            await WaitUntilAsync(() => preview.FindState != "Searching" && preview.PreviewState == "Ready" && preview.CurrentLocation?.RowNumber == 750);
            Assert.Equal("Ready", preview.FindState);
            Assert.Equal(1, preview.FindMatchCount);
            Assert.Equal("Row", preview.CurrentLocation!.Column);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Fact]
    public Task MatchLimitRemainsVisibleAsPartialCoverage() => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(DatabaseKind.Sqlite, "partial.sqlite", 1_100, everyRowMatches: true);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, InitialLocation(path), Token); snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "row-target";
            await preview.FindInTableAsync();
            Assert.Equal("Partial", preview.FindState);
            Assert.Equal(1_000, preview.FindMatchCount);
            Assert.Contains("Partial search", preview.FindStatusText);
            Assert.Contains("Match limit", preview.FindStatusText);
            Assert.Equal(1, preview.CurrentFindMatchNumber);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    private async Task<string> CreateAsync(DatabaseKind kind, string name, int count, string? firstValue = null, bool everyRowMatches = false)
    {
        string path = Path.Combine(_root, name);
        const string schema = "CREATE TABLE \"Find \"\"records\"\"\" (Id INTEGER PRIMARY KEY, \"Row\" TEXT, \"Record key\" TEXT)";
        string RowValue(int row) => row == 1 && firstValue is not null ? firstValue : everyRowMatches || row == 750 ? "row-target 東京" : "ordinary";
        string KeyValue(int row) => row == 750 ? "key-target 東京" : "other value";
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand(); command.CommandText = schema + "; CREATE TABLE EmptyTable(Value TEXT);";
            await command.ExecuteNonQueryAsync(Token);
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            for (int row = 1; row <= count; row++)
            {
                command.Parameters.Clear(); command.CommandText = "INSERT INTO \"Find \"\"records\"\"\" VALUES ($id,$row,$key)";
                command.Parameters.AddWithValue("$id", row * 10L); command.Parameters.AddWithValue("$row", RowValue(row)); command.Parameters.AddWithValue("$key", KeyValue(row));
                await command.ExecuteNonQueryAsync(Token);
            }
            transaction.Commit();
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            await using (var query = await database.ExecuteAsync(schema, Token)) { }
            await using (var query = await database.ExecuteAsync("CREATE TABLE EmptyTable(Value TEXT)", Token)) { }
            var batch = database.PrepareInsertBatch(Table);
            for (int row = 1; row <= count; row++) batch.AddRow(DbValue.FromInteger(row * 10L), DbValue.FromText(RowValue(row)), DbValue.FromText(KeyValue(row)));
            await batch.ExecuteAsync(Token);
        }
        return path;
    }

    private static Window Show(DatabasePreviewControl preview)
    {
        var window = new Window { Content = preview, Width = 980, Height = 660, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show(); return window;
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(element => element.Name == name);
    private static Button Button(DependencyObject root, string content) => Descendants(root).OfType<Button>().Single(button => Equals(button.Content, content));
    private static string SnapshotPath(DatabasePreviewControl preview)
    {
        var session = (DatabaseReadSession)typeof(DatabasePreviewControl).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        return (string)typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    }
    private static DatabaseLocation InitialLocation(string path) => new(DatabaseFiles.Detect(path), Table, 1, "Id", "", DatabaseFiles.Fingerprint(path));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Marked(DependencyObject root) => string.Join("", new[] { root }.Concat(Descendants(root)).OfType<TextBlock>()
        .SelectMany(block => block.Inlines.OfType<Run>()).Where(run => run.Background is not null).Select(run => run.Text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static bool HasValidSurrogates(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsLowSurrogate(text[index])) return false;
            if (!char.IsHighSurrogate(text[index])) continue;
            if (++index == text.Length || !char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
    private static async Task SettleAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(20, Token);
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Database preview did not settle or clean its owned snapshot.");
            await SettleAsync();
        }
    }
    private static Task OnDispatcherAsync(Func<Task> action) =>
        StaTestDispatcher.RunAsync(action, TimeSpan.FromSeconds(40), Token);
}
