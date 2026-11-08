using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Databases;
using FileSearch.Gui.Controls;
using Microsoft.Data.Sqlite;

namespace FileSearch.Gui.Tests;

public sealed class DatabaseLoadingPreviewTests : IAsyncDisposable
{
    private const string Table = "Loading \"records\"";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseLoadingGui", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DatabaseLoadingPreviewTests() => Directory.CreateDirectory(_root);

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
    public Task NewLocationInSameSourceReusesSnapshotAndTableColumns(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "reuse.db");
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1), Token);
            snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var columns = grid.Columns.ToArray();
            var session = Session(preview);

            await preview.InitializeAsync(Path.GetFullPath(path), Location(path, 750), Token);
            Assert.Same(session, Session(preview));
            Assert.Equal(snapshot, SnapshotPath(preview));
            Assert.True(File.Exists(snapshot));
            AssertRecord(preview, path, 750);
            Assert.Equal(columns.Length, grid.Columns.Count);
            for (int index = 0; index < columns.Length; index++) Assert.Same(columns[index], grid.Columns[index]);
            Assert.Equal(Visibility.Collapsed, Named<Button>(preview, "DatabaseStopLoadingButton").Visibility);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task ChangedSourceCreatesFreshSnapshotAndRejectsStaleHitLocation(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "changed.db");
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        var snapshots = new List<string>();
        try
        {
            await preview.InitializeAsync(path, Location(path, 1), Token);
            var staleLocation = preview.CurrentLocation!;
            string old = SnapshotPath(preview); snapshots.Add(old);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            Assert.NotEqual(staleLocation.Fingerprint, DatabaseFiles.Fingerprint(path));

            await preview.InitializeAsync(path, null, Token);
            string current = SnapshotPath(preview); snapshots.Add(current);
            Assert.NotEqual(old, current);
            await WaitUntilAsync(() => !File.Exists(old));
            AssertRecord(preview, path, 1);

            await Assert.ThrowsAsync<InvalidDataException>(() => preview.InitializeAsync(path, staleLocation, Token));
            await WaitUntilAsync(() => !File.Exists(current));
            Assert.Null(preview.CurrentLocation);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            Assert.Empty(grid.Items);
            Assert.False(grid.IsEnabled);

            await preview.InitializeAsync(path, null, Token);
            snapshots.Add(SnapshotPath(preview));
            AssertRecord(preview, path, 1);
            Assert.True(grid.IsEnabled);
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); await WaitUntilAsync(() => snapshots.All(path => !File.Exists(path))); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task SameSourcePreparationKeepsExistingRowsDisabledUntilRequestedPageIsReady(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "preserve-grid.db");
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1), Token);
            snapshot = SnapshotPath(preview);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var session = Session(preview);
            var gate = Gate(session);
            var sessionField = typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var pageField = typeof(DatabasePreviewControl).GetField("_pageLoad", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await gate.WaitAsync(Token);
            Task preparing;
            try
            {
                preparing = preview.InitializeAsync(path, Location(path, 750), Token);
                Assert.False(grid.IsEnabled);
                Assert.Equal(250, grid.Items.Count);
                Assert.Equal(1L, ((DataRowView)grid.Items[0])["r"]);
                Assert.Equal("record 1", ((DataRowView)grid.Items[0])["c1"]);
                await WaitUntilAsync(() => ReferenceEquals(sessionField.GetValue(preview), session) && pageField.GetValue(preview) is not null);
                Assert.False(preparing.IsCompleted);
                Assert.False(grid.IsEnabled);
                Assert.Equal(250, grid.Items.Count);
                Assert.Equal(1L, ((DataRowView)grid.Items[0])["r"]);
            }
            finally { gate.Release(); }
            await preparing;
            AssertRecord(preview, path, 750);
            Assert.True(grid.IsEnabled);
            Assert.Equal(snapshot, SnapshotPath(preview));
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task CanceledFindPageDoesNotAdvanceMatchCounterWithoutMovingTheRecord(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "find-page-cancel.db");
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1), Token);
            snapshot = SnapshotPath(preview);
            Named<TextBox>(preview, "DatabaseFindTextBox").Text = "record";
            await preview.FindInTableAsync();
            Assert.Equal(750, preview.FindMatchCount);
            Assert.Equal(1, preview.CurrentFindMatchNumber);
            var gate = Gate(Session(preview));
            await gate.WaitAsync(Token);
            try
            {
                var next = preview.MoveFindMatchAsync(1);
                Assert.Equal("Loading", preview.PreviewState);
                preview.CancelLoading();
                await next;
                Assert.Equal(1, preview.CurrentFindMatchNumber);
                AssertRecord(preview, path, 1);
            }
            finally { gate.Release(); }
            await preview.MoveFindMatchAsync(1);
            Assert.Equal(2, preview.CurrentFindMatchNumber);
            AssertRecord(preview, path, 2);
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task TableChangeSupersedesInitialPageWithoutLeavingTheNewGridDisabled(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "table-supersession.db", alternateTable: true);
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            await preview.InitializeAsync(path, Location(path, 1), Token);
            snapshot = SnapshotPath(preview);
            var session = Session(preview);
            var gate = Gate(session);
            var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
            var pageField = typeof(DatabasePreviewControl).GetField("_pageLoad", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string? stateBeforeSelection = null;
            bool? gridEnabledBeforeSelection = null;
            preview.PagePhaseMeasured = (phase, _) =>
            {
                if (phase != "page.status-controls" || preview.CurrentLocation?.Table != "ZAlternate") return;
                stateBeforeSelection = preview.PreviewState;
                gridEnabledBeforeSelection = grid.IsEnabled;
            };
            await gate.WaitAsync(Token);
            try
            {
                var initializing = preview.InitializeAsync(path, Location(path, 750), Token);
                await WaitUntilAsync(() => ReferenceEquals(Session(preview), session) && pageField.GetValue(preview) is not null);
                Named<ComboBox>(preview, "DatabaseTableSelector").SelectedItem = session.Tables.Single(table => table.Name == "ZAlternate");
                await initializing.WaitAsync(TimeSpan.FromSeconds(8), Token);
                Assert.Equal("Loading", preview.PreviewState);
                Assert.False(grid.IsEnabled);
                Assert.True(File.Exists(snapshot));
                Assert.Same(session, Session(preview));
            }
            finally { gate.Release(); }
            await WaitUntilAsync(() => preview.PreviewState == "Ready");
            Assert.Equal("Loading", stateBeforeSelection);
            Assert.False(gridEnabledBeforeSelection);
            Assert.True(grid.IsEnabled);
            Assert.Equal("ZAlternate", preview.CurrentLocation!.Table);
            Assert.Equal(1, preview.CurrentLocation.RowNumber);
            Assert.Equal(DatabaseFiles.Fingerprint(path), preview.CurrentLocation.Fingerprint);
            Assert.Equal((kind == DatabaseKind.Sqlite ? "rowid=" : "Id=") + "9001", preview.CurrentLocation.RowKey);
            var selected = Assert.IsType<DataRowView>(grid.CurrentCell.Item);
            Assert.Equal("9001", selected["c0"].ToString());
            Assert.Equal("alternate table record", selected["c1"].ToString());
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task SupersededInitializationAndItsCanceledTokenCannotReplaceNewSource(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string first = await CreateAsync(kind, "first.db");
        string second = await CreateAsync(kind, "second.db", 8);
        using var preview = new DatabasePreviewControl();
        using var obsoleteToken = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var window = Show(preview);
        var snapshots = new List<string>();
        try
        {
            await preview.InitializeAsync(first, Location(first, 1), Token);
            string old = SnapshotPath(preview); snapshots.Add(old);
            var gate = Gate(Session(preview));
            await gate.WaitAsync(Token);
            Task obsolete, latest;
            try
            {
                obsolete = preview.InitializeAsync(first, Location(first, 750), obsoleteToken.Token);
                latest = preview.InitializeAsync(second, Location(second, 8), Token);
                obsoleteToken.Cancel();
            }
            finally { gate.Release(); }
            await latest;
            await ObserveCancellationAsync(obsolete);
            snapshots.Add(SnapshotPath(preview));
            await SettleAsync();
            AssertRecord(preview, second, 8);
            Assert.NotEqual(old, SnapshotPath(preview));
            await WaitUntilAsync(() => !File.Exists(old));
            Assert.Equal(Visibility.Collapsed, Named<Button>(preview, "DatabaseStopLoadingButton").Visibility);
        }
        finally { window.Close(); await WaitUntilAsync(() => snapshots.All(path => !File.Exists(path))); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task CanceledInitializationCleanupCannotPublishOverNewSource(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string first = await CreateAsync(kind, "cleanup-first.db");
        string second = await CreateAsync(kind, "cleanup-second.db", 8);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        var snapshots = new List<string>();
        try
        {
            await preview.InitializeAsync(first, Location(first, 1), Token);
            var oldSession = Session(preview);
            string oldSnapshot = SnapshotPath(preview); snapshots.Add(oldSnapshot);
            var sessionField = typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var pageField = typeof(DatabasePreviewControl).GetField("_pageLoad", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var gate = Gate(oldSession);
            await gate.WaitAsync(Token);
            Task canceled;
            try
            {
                canceled = preview.InitializeAsync(first, Location(first, 750), Token);
                await WaitUntilAsync(() => ReferenceEquals(sessionField.GetValue(preview), oldSession) && pageField.GetValue(preview) is not null);
                Assert.False(canceled.IsCompleted);
                preview.CancelLoading();

                // Cancellation detached the session; its cleanup is still blocked on our gate.
                await WaitUntilAsync(() => sessionField.GetValue(preview) is null && pageField.GetValue(preview) is null);
                Assert.False(canceled.IsCompleted);
                Assert.True(File.Exists(oldSnapshot));

                await preview.InitializeAsync(second, Location(second, 8), Token).WaitAsync(TimeSpan.FromSeconds(8), Token);
                snapshots.Add(SnapshotPath(preview));
                AssertRecord(preview, second, 8);
            }
            finally { gate.Release(); }

            await canceled;
            await SettleAsync();
            AssertRecord(preview, second, 8);
            await WaitUntilAsync(() => !File.Exists(oldSnapshot));
            Assert.Equal(Visibility.Collapsed, Named<Button>(preview, "DatabaseStopLoadingButton").Visibility);
        }
        finally { window.Close(); await WaitUntilAsync(() => snapshots.All(path => !File.Exists(path))); }
    });

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public Task StopDuringPreparationOrBlockedPageLeavesRecoverablePreview(DatabaseKind kind) => OnDispatcherAsync(async () =>
    {
        string path = await CreateAsync(kind, "stop.db");
        string before = Hash(path);
        using var preview = new DatabasePreviewControl();
        var window = Show(preview);
        string? snapshot = null;
        try
        {
            Task opening = preview.InitializeAsync(path, null, Token);
            var stop = Named<Button>(preview, "DatabaseStopLoadingButton");
            Assert.Equal(Visibility.Visible, stop.Visibility);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await opening;
            Assert.Equal("Canceled", preview.PreviewState);
            Assert.Equal(Visibility.Collapsed, stop.Visibility);

            await preview.InitializeAsync(path, Location(path, 750), Token);
            snapshot = SnapshotPath(preview);
            AssertRecord(preview, path, 750);
            var gate = Gate(Session(preview));
            await gate.WaitAsync(Token);
            try
            {
                Named<TextBox>(preview, "DatabaseRowAddress").Text = "1";
                Descendants(preview).OfType<Button>().Single(button => Equals(button.Content, "Go to row"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Loading", preview.PreviewState);
                Assert.Equal(Visibility.Visible, stop.Visibility);
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => preview.PreviewState != "Loading");
                AssertRecord(preview, path, 750);
                Assert.Contains("canceled", Named<TextBlock>(preview, "DatabaseLoadingStatus").Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Visibility.Collapsed, stop.Visibility);
            }
            finally { gate.Release(); }

            await preview.InitializeAsync(path, Location(path, 1), Token);
            AssertRecord(preview, path, 1);
            Assert.Equal(snapshot, SnapshotPath(preview));
            Assert.Equal(before, Hash(path));
        }
        finally { window.Close(); if (snapshot is not null) await WaitUntilAsync(() => !File.Exists(snapshot)); }
    });

    private async Task<string> CreateAsync(DatabaseKind kind, string name, int rows = 750, bool alternateTable = false)
    {
        string path = Path.Combine(_root, name);
        string schema = $"CREATE TABLE {DatabaseFiles.Quote(Table)} (Id INTEGER PRIMARY KEY, Notes TEXT)";
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand(); command.CommandText = schema;
            await command.ExecuteNonQueryAsync(Token);
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {DatabaseFiles.Quote(Table)} VALUES ($id,$notes)";
            for (int row = 1; row <= rows; row++)
            {
                command.Parameters.Clear(); command.Parameters.AddWithValue("$id", row * 10L);
                command.Parameters.AddWithValue("$notes", "record " + row.ToString(CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(Token);
            }
            transaction.Commit();
            if (alternateTable)
            {
                command.Transaction = null;
                command.CommandText = "CREATE TABLE ZAlternate (Id INTEGER PRIMARY KEY, Notes TEXT); INSERT INTO ZAlternate VALUES (9001,'alternate table record')";
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            await using (var query = await database.ExecuteAsync(schema, Token)) { }
            var batch = database.PrepareInsertBatch(Table);
            for (int row = 1; row <= rows; row++)
                batch.AddRow(DbValue.FromInteger(row * 10L), DbValue.FromText("record " + row.ToString(CultureInfo.InvariantCulture)));
            await batch.ExecuteAsync(Token);
            if (alternateTable)
            {
                await using (var query = await database.ExecuteAsync("CREATE TABLE ZAlternate (Id INTEGER PRIMARY KEY, Notes TEXT)", Token)) { }
                await using (var query = await database.ExecuteAsync("INSERT INTO ZAlternate VALUES (9001,'alternate table record')", Token)) { }
            }
        }
        return path;
    }

    private static void AssertRecord(DatabasePreviewControl preview, string path, long row)
    {
        Assert.Equal("Ready", preview.PreviewState);
        var location = Assert.IsType<DatabaseLocation>(preview.CurrentLocation);
        Assert.Equal(DatabaseFiles.Detect(path), location.Engine);
        Assert.Equal(Table, location.Table);
        Assert.Equal(row, location.RowNumber);
        Assert.Equal(DatabaseFiles.Fingerprint(path), location.Fingerprint);
        Assert.Equal((location.Engine == DatabaseKind.Sqlite ? "rowid=" : "Id=") + (row * 10).ToString(CultureInfo.InvariantCulture), location.RowKey);
        var grid = Named<DataGrid>(preview, "DatabaseRecordsGrid");
        var selected = Assert.IsType<DataRowView>(grid.CurrentCell.Item);
        Assert.Equal(row, (long)selected["r"]);
        Assert.Equal((row * 10).ToString(CultureInfo.InvariantCulture), selected["c0"].ToString());
        Assert.Equal("record " + row.ToString(CultureInfo.InvariantCulture), selected["c1"].ToString());
    }

    private static DatabaseLocation Location(string path, long row) => new(DatabaseFiles.Detect(path), Table, row, "Notes", "", DatabaseFiles.Fingerprint(path));
    private static DatabaseReadSession Session(DatabasePreviewControl preview) =>
        (DatabaseReadSession)typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
    private static SemaphoreSlim Gate(DatabaseReadSession session) =>
        (SemaphoreSlim)typeof(DatabaseReadSession).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    private static string SnapshotPath(DatabasePreviewControl preview) =>
        (string)typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Session(preview))!;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(element => element.Name == name);

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
            WindowStartupLocation = WindowStartupLocation.Manual
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

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
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
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Database preview did not settle or clean its snapshot.");
            await SettleAsync();
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(40), Token);
    }
}
